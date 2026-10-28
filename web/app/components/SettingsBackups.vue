<script setup lang="ts">
import type { BackupOverviewDto, RestorePreviewDto, RestoreResultDto } from '~/types/api'
import { errorMessage, formatBytes } from '~/utils/format'

const emit = defineEmits<{ restored: [] }>()
const { refresh } = useAppData()
const toast = useToast()
const overview = ref<BackupOverviewDto | null>(null)
const loading = ref(true)
const error = ref('')
const fileInput = ref<HTMLInputElement | null>(null)
const uploading = ref(false)
const restoring = ref(false)
const preview = ref<RestorePreviewDto | null>(null)
const confirmOpen = ref(false)
const confirmed = ref(false)
const restoreError = ref('')
const restoredResult = ref<RestoreResultDto | null>(null)
const cacheRefreshFailed = ref(false)

function dateTime(value: string) { return value.replace('T', ' ').slice(0, 19) }

async function fetchBackups() {
  loading.value = true
  error.value = ''
  try { overview.value = await $fetch<BackupOverviewDto>('/api/backups') }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
}

async function prepareRestore(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || uploading.value || restoring.value) return
  restoreError.value = ''
  preview.value = null
  confirmed.value = false
  if (file.size > 4 * 1024 * 1024 * 1024) { restoreError.value = '备份 ZIP 不能超过 4 GiB。'; return }
  if (!file.name.toLowerCase().endsWith('.zip')) { restoreError.value = '请选择通过本工具下载的 ZIP 备份文件。'; return }
  uploading.value = true
  try {
    preview.value = await $fetch<RestorePreviewDto>('/api/backups/restore/prepare', { method: 'PUT', body: file, headers: { 'Content-Type': 'application/zip' } })
    confirmOpen.value = true
  } catch (cause) { restoreError.value = errorMessage(cause) }
  finally { uploading.value = false }
}

async function restore() {
  if (!confirmed.value || !preview.value || restoring.value) return
  restoring.value = true
  restoreError.value = ''
  cacheRefreshFailed.value = false
  let completed = false
  try {
    restoredResult.value = await $fetch<RestoreResultDto>('/api/backups/restore', { method: 'POST', body: { token: preview.value.token, confirm: true } })
    completed = true
    confirmOpen.value = false
    preview.value = null
    confirmed.value = false
    await refresh()
    emit('restored')
    await fetchBackups()
    toast.add({ title: '已恢复', color: 'success', duration: 2000 })
  } catch (cause) {
    if (completed) {
      cacheRefreshFailed.value = true
      restoreError.value = '恢复完成，请刷新页面'
    } else restoreError.value = errorMessage(cause)
  } finally { restoring.value = false }
}

function reloadPage() { window.location.reload() }
onMounted(fetchBackups)
</script>

<template>
  <div class="page-stack">
    <div class="flex flex-wrap items-center gap-2">
      <UButton to="/api/backups/download" external :prefetch="false" download :disabled="restoring">下载备份 ZIP</UButton>
      <input ref="fileInput" data-testid="restore-file-input" type="file" accept=".zip,application/zip" class="sr-only" aria-label="选择待恢复的 ZIP 备份" :disabled="uploading || restoring" @change="prepareRestore">
      <UButton color="neutral" variant="outline" :loading="uploading" :disabled="restoring" @click="fileInput?.click()">{{ uploading ? '正在校验' : '恢复备份' }}</UButton>
    </div>
    <p class="text-xs text-muted">备份包含地图配置，不包含图块缓存。</p>
    <UProgress v-if="uploading" animation="carousel" aria-label="正在校验备份" />
    <UAlert v-if="restoreError && !confirmOpen" :color="cacheRefreshFailed ? 'warning' : 'error'" variant="subtle" :description="restoreError"><template v-if="cacheRefreshFailed" #actions><UButton color="neutral" variant="outline" @click="reloadPage">刷新</UButton></template></UAlert>
    <div v-if="restoredResult" class="text-xs text-muted break-all">保留位置：{{ restoredResult.preservedBackup }}</div>
    <section>
      <div class="section-heading"><h2 class="section-title">自动备份（不含照片）</h2><UButton color="neutral" variant="ghost" :loading="loading" @click="fetchBackups">刷新</UButton></div>
      <p class="mb-3 text-[13px] leading-5 text-muted">每天自动备份，保留最近 14 份。</p>
      <UAlert v-if="error" :description="error" color="error" variant="subtle"><template #actions><UButton color="neutral" variant="outline" @click="fetchBackups">重试</UButton></template></UAlert>
      <div v-else-if="loading && !overview" class="space-y-2"><USkeleton class="h-12 w-full" /><USkeleton class="h-12 w-full" /></div>
      <ul v-else-if="overview?.items.length" class="group-list">
        <li v-for="item in overview.items" :key="item.name" class="list-row !min-h-11 justify-between text-sm"><time>{{ item.date }}</time><span class="text-xs text-muted">{{ formatBytes(item.bytes) }}</span></li>
      </ul>
      <EmptyState v-else title="暂无自动备份" icon="i-lucide-database" />
    </section>
  </div>
  <UModal v-model:open="confirmOpen" title="恢复备份？" description="将覆盖当前数据库、照片和附件，覆盖前保留旧数据。" :dismissible="!restoring" :close="!restoring">
    <template #body>
      <div v-if="preview" class="space-y-3">
        <UAlert v-if="restoreError" :description="restoreError" color="error" variant="subtle" />
        <dl class="detail-grid">
          <div class="detail-wide"><dt>备份时间</dt><dd>{{ dateTime(preview.createdAt) }}</dd></div>
          <div><dt>点位</dt><dd>{{ preview.pointCount }}</dd></div>
          <div><dt>排查</dt><dd>{{ preview.inspectionCount }}</dd></div>
          <div><dt>问题</dt><dd>{{ preview.issueCount }}</dd></div>
          <div><dt>整治</dt><dd data-testid="restore-remediation-count">{{ preview.remediationCount }}</dd></div>
          <div><dt>活动</dt><dd data-testid="restore-activity-count">{{ preview.activityCount }}</dd></div>
          <div><dt>事项</dt><dd data-testid="restore-matter-count">{{ preview.matterCount }}</dd></div>
          <div><dt>附件</dt><dd data-testid="restore-attachment-count">{{ preview.attachmentCount }}</dd></div>
          <div><dt>标签</dt><dd>{{ preview.tagCount }}</dd></div>
          <div><dt>照片</dt><dd>{{ preview.photoCount }}</dd></div>
          <div><dt>解压大小</dt><dd>{{ formatBytes(preview.expandedBytes) }}</dd></div>
          <div class="detail-wide"><dt>确认截止</dt><dd>{{ dateTime(preview.expiresAt) }}</dd></div>
        </dl>
        <div class="check-row"><UCheckbox v-model="confirmed" label="确认覆盖当前数据" :disabled="restoring" /></div>
      </div>
    </template>
    <template #footer><UButton color="neutral" variant="outline" :disabled="restoring" @click="confirmOpen = false">取消</UButton><UButton data-testid="restore-confirm" color="error" :loading="restoring" :disabled="!confirmed" @click="restore">恢复</UButton></template>
  </UModal>
</template>
