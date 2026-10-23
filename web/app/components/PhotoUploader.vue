<script setup lang="ts">
import type { PhotoPhase, PhotoUploadDto } from '~/types/api'
import { compressPhoto } from '~/lib/photos/compress'
import { uploadPhoto } from '~/lib/photos/upload'
import { DEFAULT_JPEG_QUALITY, type CompressedPhoto } from '~/lib/photos/types'
import { errorMessage, formatBytes, phaseLabel } from '~/utils/format'

const props = withDefaults(defineProps<{
  modelValue: PhotoUploadDto[]
  phase: PhotoPhase
  quality?: number
  disabled?: boolean
  max?: number
  label?: string
  deferDeleteIds?: string[]
}>(), { quality: DEFAULT_JPEG_QUALITY, disabled: false, max: 100 })

const emit = defineEmits<{
  'update:modelValue': [value: PhotoUploadDto[]]
  busy: [value: boolean]
  failed: [value: boolean]
}>()

interface UploadJob {
  key: number
  file: File
  status: 'queued' | 'compressing' | 'uploading' | 'failed'
  progress: number
  message: string
  error: string
  controller: AbortController
  compressed?: CompressedPhoto
  quality?: number
  uploaded?: PhotoUploadDto
  task?: Promise<void>
  deleteFailed?: boolean
}

type Removal = { kind: 'uploaded'; photo: PhotoUploadDto } | { kind: 'pending'; job: UploadJob }
const fileInput = ref<HTMLInputElement | null>(null)
// Keep a local mirror so two uploads finishing in one Vue update cannot drop an ID.
const localPhotos = ref<PhotoUploadDto[]>([...props.modelValue])
const jobs = ref<UploadJob[]>([])
const notice = ref('')
const removeOpen = ref(false)
const removal = ref<Removal | null>(null)
const deferredRemoval = computed(() => removal.value?.kind === 'uploaded' && !!props.deferDeleteIds?.includes(removal.value.photo.id))
const deleting = ref(false)
const deleteError = ref('')
const previewPhoto = ref<PhotoUploadDto | null>(null)
const previewOpen = ref(false)
const missingPreviews = ref<string[]>([])
const previewVersions = reactive<Record<string, number>>({})
const compressionEngines = reactive<Record<string, string>>({})
let nextJobKey = 1
let disposed = false

watch(() => props.modelValue, value => { localPhotos.value = [...value] }, { deep: true })
const busy = computed(() => jobs.value.length > 0 || deleting.value)
watch(busy, value => emit('busy', value), { immediate: true, flush: 'sync' })
const failedCount = computed(() => jobs.value.filter(job => job.status === 'failed').length)
watch(failedCount, value => emit('failed', value > 0), { immediate: true, flush: 'sync' })
const canAdd = computed(() => !props.disabled && !deleting.value && localPhotos.value.length + jobs.value.length < props.max)

function updatePhotos(photos: PhotoUploadDto[]) {
  if (disposed) return
  localPhotos.value = photos
  emit('update:modelValue', photos)
}

function imageUrl(id: string, original = false) {
  return `/photos/${id}?size=${original ? 'original' : 'thumb'}&preview=${previewVersions[id] ?? 0}`
}

function retryPreview(id: string) {
  missingPreviews.value = missingPreviews.value.filter(value => value !== id)
  previewVersions[id] = (previewVersions[id] ?? 0) + 1
}

function removeJob(job: UploadJob) {
  jobs.value = jobs.value.filter(value => value.key !== job.key)
}

function startJob(job: UploadJob) {
  if (disposed || deleting.value || props.disabled) return
  job.task = markRaw(runJob(job))
}

async function runJob(job: UploadJob) {
  job.error = ''
  job.deleteFailed = false
  job.status = 'queued'
  job.progress = 0
  job.message = '等待处理'
  const controller = markRaw(new AbortController())
  job.controller = controller
  const active = () => !disposed && !controller.signal.aborted && job.controller === controller && jobs.value.some(value => value.key === job.key)
  const quality = props.quality
  try {
    if (!job.uploaded && (!job.compressed || job.quality !== quality)) {
      const compressed = await compressPhoto(job.file, {
        quality,
        signal: controller.signal,
        onProgress(percent, message) {
          if (!active()) return
          job.status = percent === 0 ? 'queued' : 'compressing'
          job.progress = Math.round(percent * 0.6)
          job.message = message
        },
      })
      if (!active()) return
      job.compressed = compressed
      job.quality = quality
    }
    if (!active() || !job.compressed) return
    job.status = 'uploading'
    job.progress = 60
    job.message = `正在上传 · ${formatBytes(job.compressed.blob.size)}`
    if (!job.uploaded) {
      // Retain the returned ID even if cancellation won this await, so the
      // cancellation path can delete a late server response before forgetting it.
      job.uploaded = await uploadPhoto(job.compressed.blob, props.phase, {
        signal: controller.signal,
        onProgress(percent) {
          if (!active()) return
          job.progress = 60 + Math.round(percent * 0.4)
          job.message = percent < 100 ? `正在上传 · ${percent}%` : '确认照片已保存'
        },
      })
    }
    if (!active()) return
    const uploaded = job.uploaded
    if (job.compressed.thumbnail && job.compressed.thumbnail.size <= 200 * 1024) {
      try {
        await $fetch(`/api/photos/${uploaded.id}/thumb`, { method: 'PUT', body: job.compressed.thumbnail, headers: { 'Content-Type': 'image/jpeg' }, signal: controller.signal, retry: 0, timeout: 10000 })
      } catch { /* An optional thumbnail failure must not discard the original. */ }
    }
    // In particular, deleting during the thumbnail await must never resurrect
    // the deleted ID in a parent form or its persisted draft.
    if (!active()) return
    compressionEngines[uploaded.id] = job.compressed.engine
    if (!localPhotos.value.some(photo => photo.id === uploaded.id)) updatePhotos([...localPhotos.value, uploaded])
    removeJob(job)
  } catch (error) {
    if (!active()) return
    job.status = 'failed'
    job.error = errorMessage(error)
    job.message = '照片未完成'
  }
}

function chooseFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const selected = Array.from(input.files ?? [])
  input.value = '' // Picking the same file again must still emit change.
  if (!canAdd.value || selected.length === 0) return
  notice.value = ''
  const available = props.max - localPhotos.value.length - jobs.value.length
  if (selected.length > available) notice.value = `最多 ${props.max} 张照片`
  for (const file of selected.slice(0, available)) {
    const job = reactive<UploadJob>({
      key: nextJobKey++, file: markRaw(file), status: 'queued', progress: 0,
      message: '等待处理', error: '', controller: markRaw(new AbortController()),
    })
    jobs.value.push(job)
    startJob(job)
  }
}

function askRemove(target: Removal) {
  if (props.disabled || deleting.value) return
  removal.value = target
  deleteError.value = ''
  removeOpen.value = true
}

async function deleteServerPhoto(photo: PhotoUploadDto) {
  try {
    await $fetch(`/api/photos/${photo.id}`, { method: 'DELETE', query: { confirm: true }, retry: 0, timeout: 10000 })
  } catch (error) {
    const status = error && typeof error === 'object' ? (error as { statusCode?: number; status?: number }) : null
    // An expired draft photo may already have been cleaned by the server.
    if (status?.statusCode !== 404 && status?.status !== 404) throw error
  }
}

async function confirmRemove() {
  const target = removal.value
  if (!target || deleting.value) return
  deleting.value = true
  deleteError.value = ''
  try {
    if (target.kind === 'uploaded') {
      if (!props.deferDeleteIds?.includes(target.photo.id)) await deleteServerPhoto(target.photo)
      updatePhotos(localPhotos.value.filter(value => value.id !== target.photo.id))
    } else {
      // Invalidate FIRST, then wait for the task's final ID, then remove it.
      target.job.controller.abort()
      await target.job.task
      if (target.job.uploaded) await deleteServerPhoto(target.job.uploaded)
      if (!disposed) removeJob(target.job)
    }
    if (!disposed) { removeOpen.value = false; removal.value = null }
  } catch (error) {
    if (disposed) return
    deleteError.value = errorMessage(error)
    if (target.kind === 'pending') {
      target.job.status = 'failed'
      target.job.deleteFailed = true
      target.job.message = '照片删除未完成'
      target.job.error = deleteError.value
    }
  } finally { if (!disposed) deleting.value = false }
}

function cancelPendingJobs() {
  // Accepted navigation can precede unmount while Nuxt loads the next page.
  // Invalidate unpublished jobs before a late thumbnail completion can publish.
  const pending = [...jobs.value]
  for (const job of pending) job.controller.abort()
  jobs.value = []
  for (const job of pending) {
    job.controller.abort()
    // Only unpublished jobs are in this queue. If best-effort cleanup fails,
    // existing server orphan maintenance retains its seven-day safety window.
    void job.task?.then(async () => { if (job.uploaded) await deleteServerPhoto(job.uploaded) }).catch(() => {})
  }
}
const router = useRouter()
watch(() => router.currentRoute.value.path, (next, previous) => {
  if (next !== previous) cancelPendingJobs()
}, { flush: 'sync' })
onBeforeUnmount(() => {
  disposed = true
  cancelPendingJobs()
  emit('busy', false)
  emit('failed', false)
})

</script>

<template>
  <div class="space-y-2" :aria-label="`${label || phaseLabel(phase)}照片`">
    <input ref="fileInput" data-testid="photo-file-input" type="file" accept="image/*" multiple class="hidden" :disabled="!canAdd" @change="chooseFiles">
    <div class="flex items-center gap-2"><UButton type="button" icon="i-lucide-camera" variant="outline" :disabled="!canAdd" @click="fileInput?.click()">添加照片</UButton><span class="text-xs text-muted" aria-live="polite">{{ localPhotos.length }}/{{ max }}</span></div>
    <p v-if="notice" class="text-xs text-warning" role="alert">{{ notice }}</p>
    <div v-if="localPhotos.length" class="photo-grid">
      <div v-for="(photo, index) in localPhotos" :key="photo.id" data-testid="photo-uploaded" :data-photo-id="photo.id" :data-compression-engine="compressionEngines[photo.id] ?? 'restored'" class="photo-cell">
        <button type="button" class="photo-button" :aria-label="`查看第 ${index + 1} 张照片`" @click="previewPhoto = photo; previewOpen = true"><img v-if="!missingPreviews.includes(photo.id)" :src="imageUrl(photo.id)" :alt="`${label || phaseLabel(phase)}照片 ${index + 1}`" loading="lazy" @error="missingPreviews.push(photo.id)"><UIcon v-else name="i-lucide-image-off" class="size-6 text-muted" /></button>
        <UButton type="button" icon="i-lucide-x" color="error" variant="ghost" class="photo-remove" :disabled="disabled || deleting" :aria-label="`删除第 ${index + 1} 张照片`" @click="askRemove({ kind: 'uploaded', photo })" />
        <UButton v-if="missingPreviews.includes(photo.id)" type="button" label="重试" color="neutral" variant="link" @click="retryPreview(photo.id)" />
      </div>
    </div>
    <div v-if="jobs.length" aria-live="polite">
      <div v-for="job in jobs" :key="job.key" data-testid="photo-pending" class="flex items-start gap-2 border-b border-[var(--divider)] py-2">
        <div class="min-w-0 flex-1"><p class="truncate text-sm">{{ job.file.name || '照片' }}</p><p class="text-xs text-muted">{{ job.message }}</p><p v-if="job.error" role="alert" class="text-sm text-error">{{ job.error }}</p><UProgress v-else :model-value="job.progress" :max="100" size="xs" :aria-label="`${job.file.name}处理进度`" /><UButton v-if="job.status === 'failed'" type="button" variant="link" :label="job.deleteFailed ? '重试删除' : '重试'" :disabled="disabled || deleting" @click="job.deleteFailed ? askRemove({ kind: 'pending', job }) : startJob(job)" /></div>
        <UButton type="button" icon="i-lucide-x" color="neutral" variant="ghost" :disabled="disabled || deleting" aria-label="移除未上传照片" @click="askRemove({ kind: 'pending', job })" />
      </div><p v-if="failedCount" class="text-xs text-error">{{ failedCount }} 张未完成，请重试或移除</p>
    </div>
    <UModal v-model:open="removeOpen" :title="deferredRemoval ? '移除照片？' : '删除照片？'" :description="deferredRemoval ? '保存活动后移除，取消编辑会保留原照片' : '删除后无法恢复'" :dismissible="!deleting" :close="!deleting">
      <template #body><UAlert v-if="deleteError" :description="deleteError" color="error" variant="subtle" /></template>
      <template #footer><UButton type="button" color="neutral" variant="outline" :disabled="deleting" @click="removeOpen = false">取消</UButton><UButton type="button" color="error" :loading="deleting" @click="confirmRemove">{{ deferredRemoval ? '移除' : '删除' }}</UButton></template>
    </UModal>
    <UModal v-model:open="previewOpen" title="照片预览" :ui="{ content: 'max-w-3xl' }"><template #body><img v-if="previewPhoto" :src="imageUrl(previewPhoto.id, true)" :alt="`${label || phaseLabel(phase)}照片`" class="mx-auto max-h-[70dvh] max-w-full object-contain"></template><template #footer><UButton type="button" icon="i-lucide-chevron-left" color="neutral" variant="outline" aria-label="上一张" :disabled="!previewPhoto || localPhotos.findIndex(p => p.id === previewPhoto?.id) <= 0" @click="previewPhoto = localPhotos[localPhotos.findIndex(p => p.id === previewPhoto?.id) - 1] ?? null" /><UButton type="button" icon="i-lucide-chevron-right" color="neutral" variant="outline" aria-label="下一张" :disabled="!previewPhoto || localPhotos.findIndex(p => p.id === previewPhoto?.id) >= localPhotos.length - 1" @click="previewPhoto = localPhotos[localPhotos.findIndex(p => p.id === previewPhoto?.id) + 1] ?? null" /></template></UModal>
  </div>
</template>
