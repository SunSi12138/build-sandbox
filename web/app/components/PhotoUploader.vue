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
}>(), { quality: DEFAULT_JPEG_QUALITY, disabled: false })

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
const canAdd = computed(() => !props.disabled && !deleting.value && localPhotos.value.length + jobs.value.length < 100)

function updatePhotos(photos: PhotoUploadDto[]) {
  localPhotos.value = photos
  emit('update:modelValue', photos)
}

function imageUrl(id: string) {
  return `/photos/${id}${previewVersions[id] ? `?preview=${previewVersions[id]}` : ''}`
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
  const available = 100 - localPhotos.value.length - jobs.value.length
  if (selected.length > available) notice.value = `每组最多 100 张照片，本次先添加前 ${available} 张。`
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
  <div class="space-y-3" :aria-label="`${phaseLabel(phase)}照片`">
    <input ref="fileInput" data-testid="photo-file-input" type="file" accept="image/*" multiple class="hidden" :disabled="!canAdd" @change="chooseFiles">
    <div class="flex flex-wrap items-center gap-3">
      <UButton type="button" icon="i-lucide-camera" variant="outline" size="lg" class="min-h-12" :disabled="!canAdd" @click="fileInput?.click()">
        拍照或从相册选择
      </UButton>
      <span class="text-sm text-muted" aria-live="polite">已上传 {{ localPhotos.length }} 张</span>
    </div>
    <p class="text-xs leading-5 text-muted">可一次选择多张，照片会自动压缩。{{ phase === 'review' ? '复查照片可选。' : '' }}</p>
    <UAlert v-if="notice" :description="notice" color="warning" variant="subtle" icon="i-lucide-info" />

    <div v-if="localPhotos.length" class="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
      <div v-for="(photo, index) in localPhotos" :key="photo.id" data-testid="photo-uploaded" :data-photo-id="photo.id" :data-compression-engine="compressionEngines[photo.id] ?? 'restored'" class="overflow-hidden rounded-xl border border-default bg-default">
        <UButton type="button" color="neutral" variant="ghost" class="block w-full rounded-none p-0" :aria-label="`查看第 ${index + 1} 张${phaseLabel(phase)}照片`" @click="previewPhoto = photo; previewOpen = true">
          <img v-if="!missingPreviews.includes(photo.id)" :src="imageUrl(photo.id)" :alt="`${phaseLabel(phase)}照片 ${index + 1}`" loading="lazy" class="aspect-[4/3] w-full object-cover" @error="missingPreviews.push(photo.id)">
          <span v-else class="flex aspect-[4/3] w-full flex-col items-center justify-center gap-2 px-2 text-sm text-muted">
            <UIcon name="i-lucide-image-off" class="size-6" />暂时无法预览
          </span>
        </UButton>
        <div class="flex flex-wrap items-center justify-between gap-1 p-2">
          <span class="text-xs text-muted">{{ photo.width }} × {{ photo.height }}</span>
          <UButton v-if="missingPreviews.includes(photo.id)" type="button" icon="i-lucide-refresh-cw" color="neutral" variant="ghost" class="min-h-12" aria-label="重新加载照片预览" @click="retryPreview(photo.id)" />
          <UButton type="button" icon="i-lucide-trash-2" color="error" variant="ghost" class="min-h-12" :disabled="disabled || deleting" :aria-label="`移除第 ${index + 1} 张照片`" @click="askRemove({ kind: 'uploaded', photo })">移除</UButton>
        </div>
      </div>
    </div>

    <div v-if="jobs.length" class="space-y-3" aria-live="polite">
      <div v-for="job in jobs" :key="job.key" data-testid="photo-pending" class="rounded-xl border p-3" :class="job.status === 'failed' ? 'border-error/30 bg-error/5' : 'border-default bg-elevated/40'">
        <div class="flex items-start gap-3">
          <UIcon v-if="job.status === 'failed'" name="i-lucide-circle-alert" class="mt-1 size-5 shrink-0 text-error" />
          <UIcon v-else name="i-lucide-image" class="mt-1 size-5 shrink-0 text-primary" />
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium">{{ job.file.name || '所选照片' }}</p>
            <p class="mt-1 text-xs text-muted">{{ job.message }}</p>
            <p v-if="job.error" role="alert" class="mt-2 text-sm leading-5 text-error">{{ job.error }}</p>
            <UProgress v-else :model-value="job.progress" :max="100" size="xs" class="mt-3" :aria-label="`${job.file.name}处理进度`" />
          </div>
          <UButton type="button" icon="i-lucide-x" color="neutral" variant="ghost" class="min-h-12 min-w-12 shrink-0" :disabled="disabled || deleting" aria-label="移除未上传照片" @click="askRemove({ kind: 'pending', job })" />
        </div>
        <UButton v-if="job.status === 'failed'" type="button" icon="i-lucide-refresh-cw" color="primary" variant="soft" class="mt-3 min-h-12" :disabled="disabled || deleting" @click="runJob(job)">重试这张照片</UButton>
      </div>
      <p v-if="failedCount" class="text-sm text-error">有 {{ failedCount }} 张照片尚未上传，请重试或移除后再保存。</p>
    </div>

    <UModal v-model:open="removeOpen" title="移除这张照片？" :description="removal?.kind === 'uploaded' || removal?.job.uploaded ? '确认后会删除照片，无法撤销。' : '这张照片将从待上传列表中移除，之后可以重新选择。'" :dismissible="!deleting" :close="!deleting" :ui="{ footer: 'justify-end' }">
      <template #body>
        <p class="text-sm leading-6 text-muted">请确认这张照片不再需要保留。</p>
        <UAlert v-if="deleteError" :description="deleteError" color="error" variant="subtle" class="mt-3" />
      </template>
      <template #footer>
        <UButton type="button" color="neutral" variant="outline" class="min-h-12" :disabled="deleting" @click="removeOpen = false">保留照片</UButton>
        <UButton type="button" color="error" class="min-h-12" :loading="deleting" @click="confirmRemove">确认移除</UButton>
      </template>
    </UModal>

    <UModal v-model:open="previewOpen" title="照片预览" :description="`${phaseLabel(phase)}照片`" :ui="{ content: 'max-w-3xl' }">
      <template #body>
        <img v-if="previewPhoto" :src="imageUrl(previewPhoto.id)" :alt="`${phaseLabel(phase)}照片原图`" class="mx-auto max-h-[70svh] max-w-full rounded-lg object-contain">
      </template>
    </UModal>
  </div>
</template>
