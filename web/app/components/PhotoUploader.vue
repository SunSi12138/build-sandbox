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
}>(), { quality: DEFAULT_JPEG_QUALITY, disabled: false, max: 100 })

const emit = defineEmits<{
  'update:modelValue': [value: PhotoUploadDto[]]
  busy: [value: boolean]
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
}

type Removal = { kind: 'uploaded'; photo: PhotoUploadDto } | { kind: 'pending'; job: UploadJob }
const fileInput = ref<HTMLInputElement | null>(null)
// Keep a local mirror so two uploads finishing in one Vue update cannot drop an ID.
const localPhotos = ref<PhotoUploadDto[]>([...props.modelValue])
const jobs = ref<UploadJob[]>([])
const notice = ref('')
const removeOpen = ref(false)
const removal = ref<Removal | null>(null)
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
const canAdd = computed(() => !props.disabled && !deleting.value && localPhotos.value.length + jobs.value.length < props.max)

function updatePhotos(photos: PhotoUploadDto[]) {
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

async function runJob(job: UploadJob) {
  job.error = ''
  job.status = 'queued'
  job.progress = 0
  job.message = '等待处理'
  const controller = markRaw(new AbortController())
  job.controller = controller
  try {
    if (!job.compressed || job.quality !== props.quality) {
      job.compressed = await compressPhoto(job.file, {
        quality: props.quality,
        signal: controller.signal,
        onProgress(percent, message) {
          if (disposed || controller.signal.aborted) return
          job.status = percent === 0 ? 'queued' : 'compressing'
          job.progress = Math.round(percent * 0.6)
          job.message = message
        },
      })
      job.quality = props.quality
    }
    if (disposed || controller.signal.aborted) return
    job.status = 'uploading'
    job.progress = 60
    job.message = `正在上传 · ${formatBytes(job.compressed.blob.size)}`
    const uploaded = await uploadPhoto(job.compressed.blob, props.phase, {
      signal: controller.signal,
      onProgress(percent) {
        if (disposed || controller.signal.aborted) return
        job.progress = 60 + Math.round(percent * 0.4)
        job.message = percent < 100 ? `正在上传 · ${percent}%` : '确认照片已保存'
      },
    })
    if (disposed || controller.signal.aborted) return
    job.uploaded = uploaded
    if (job.compressed.thumbnail && job.compressed.thumbnail.size <= 200 * 1024) {
      try {
        await $fetch(`/api/photos/${uploaded.id}/thumb`, { method: 'PUT', body: job.compressed.thumbnail, headers: { 'Content-Type': 'image/jpeg' }, signal: controller.signal, retry: 0, timeout: 10000 })
      } catch { /* The API serves the original when a thumbnail is unavailable. */ }
    }
    compressionEngines[uploaded.id] = job.compressed.engine
    updatePhotos([...localPhotos.value, uploaded])
    jobs.value = jobs.value.filter(value => value.key !== job.key)
  } catch (error) {
    if (disposed || controller.signal.aborted) return
    job.status = 'failed'
    job.error = errorMessage(error)
    job.message = '上传未完成'
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
    void runJob(job)
  }
}

function askRemove(target: Removal) {
  if (props.disabled || deleting.value) return
  removal.value = target
  deleteError.value = ''
  removeOpen.value = true
}

async function removeUploadedPhoto(photo: PhotoUploadDto) {
  try {
    await $fetch(`/api/photos/${photo.id}`, { method: 'DELETE', query: { confirm: true }, retry: 0 })
  } catch (error) {
    const status = error && typeof error === 'object' ? (error as { statusCode?: number; status?: number }) : null
    // An expired draft photo may already have been cleaned by the server.
    if (status?.statusCode !== 404 && status?.status !== 404) throw error
  }
  updatePhotos(localPhotos.value.filter(value => value.id !== photo.id))
}

async function confirmRemove() {
  const target = removal.value
  if (!target || deleting.value) return
  deleting.value = true
  deleteError.value = ''
  try {
    if (target.kind === 'uploaded') await removeUploadedPhoto(target.photo)
    else if (target.job.uploaded) await removeUploadedPhoto(target.job.uploaded)
    else {
      target.job.controller.abort()
      jobs.value = jobs.value.filter(value => value.key !== target.job.key)
    }
    removeOpen.value = false
    removal.value = null
  } catch (error) { deleteError.value = errorMessage(error) }
  finally { deleting.value = false }
}

onBeforeUnmount(() => {
  disposed = true
  for (const job of jobs.value) job.controller.abort()
  emit('busy', false)
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
        <div class="min-w-0 flex-1"><p class="truncate text-sm">{{ job.file.name || '照片' }}</p><p class="text-xs text-muted">{{ job.message }}</p><p v-if="job.error" role="alert" class="text-sm text-error">{{ job.error }}</p><UProgress v-else :model-value="job.progress" :max="100" size="xs" :aria-label="`${job.file.name}处理进度`" /><UButton v-if="job.status === 'failed'" type="button" variant="link" label="重试" :disabled="disabled || deleting" @click="runJob(job)" /></div>
        <UButton type="button" icon="i-lucide-x" color="neutral" variant="ghost" :disabled="disabled || deleting" aria-label="移除未上传照片" @click="askRemove({ kind: 'pending', job })" />
      </div><p v-if="failedCount" class="text-xs text-error">{{ failedCount }} 张未上传，请重试或移除</p>
    </div>
    <UModal v-model:open="removeOpen" title="删除照片？" description="删除后无法恢复" :dismissible="!deleting" :close="!deleting">
      <template #body><UAlert v-if="deleteError" :description="deleteError" color="error" variant="subtle" /></template>
      <template #footer><UButton type="button" color="neutral" variant="outline" :disabled="deleting" @click="removeOpen = false">取消</UButton><UButton type="button" color="error" :loading="deleting" @click="confirmRemove">删除</UButton></template>
    </UModal>
    <UModal v-model:open="previewOpen" title="照片预览" :ui="{ content: 'max-w-3xl' }"><template #body><img v-if="previewPhoto" :src="imageUrl(previewPhoto.id, true)" :alt="`${label || phaseLabel(phase)}照片`" class="mx-auto max-h-[70dvh] max-w-full object-contain"></template><template #footer><UButton type="button" icon="i-lucide-chevron-left" color="neutral" variant="outline" aria-label="上一张" :disabled="!previewPhoto || localPhotos.findIndex(p => p.id === previewPhoto?.id) <= 0" @click="previewPhoto = localPhotos[localPhotos.findIndex(p => p.id === previewPhoto?.id) - 1] ?? null" /><UButton type="button" icon="i-lucide-chevron-right" color="neutral" variant="outline" aria-label="下一张" :disabled="!previewPhoto || localPhotos.findIndex(p => p.id === previewPhoto?.id) >= localPhotos.length - 1" @click="previewPhoto = localPhotos[localPhotos.findIndex(p => p.id === previewPhoto?.id) + 1] ?? null" /></template></UModal>
  </div>
</template>
