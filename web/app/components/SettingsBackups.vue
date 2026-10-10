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
    toast.add({ title: '备份恢复完成', description: '当前数据已更新，覆盖前的数据已自动保留。', color: 'success' })
  } catch (cause) {
    if (completed) {
      cacheRefreshFailed.value = true
      restoreError.value = '恢复已经完成，但页面资料暂未重新载入。请刷新页面查看恢复后的内容。'
    } else restoreError.value = errorMessage(cause)
  } finally { restoring.value = false }
}

function reloadPage() { window.location.reload() }
onMounted(fetchBackups)
</script>

<template>
  <UCard>
    <template #header><div><h2 class="font-semibold text-highlighted">备份与恢复</h2><p class="mt-1 text-sm text-muted">下载完整备份，妥善保存每一次排查和整改照片。</p></div></template>
    <div class="space-y-5">
      <div class="grid gap-4 md:grid-cols-2">
        <div class="rounded-xl border border-primary/15 bg-primary/5 p-4">
          <div class="mb-3 flex items-center gap-2 font-medium"><UIcon name="i-lucide-download" class="size-5 text-primary" />保存完整备份</div>
          <p class="mb-4 text-sm leading-6 text-muted">包含当前数据库和全部照片，下载后可用于迁移或恢复。</p>
          <UButton to="/api/backups/download" external :prefetch="false" download icon="i-lucide-download" size="xl" class="min-h-12" :disabled="restoring">下载备份 ZIP</UButton>
        </div>
        <div class="rounded-xl border border-default p-4">
          <div class="mb-3 flex items-center gap-2 font-medium"><UIcon name="i-lucide-archive-restore" class="size-5 text-muted" />从备份恢复</div>
          <p class="mb-4 text-sm leading-6 text-muted">先选择 ZIP 核对摘要，确认后覆盖。覆盖前会自动保留旧数据。</p>
          <input ref="fileInput" data-testid="restore-file-input" type="file" accept=".zip,application/zip" class="sr-only" aria-label="选择待恢复的 ZIP 备份" :disabled="uploading || restoring" @change="prepareRestore">
          <UButton icon="i-lucide-upload" color="neutral" variant="outline" size="xl" class="min-h-12" :loading="uploading" :disabled="restoring" @click="fileInput?.click()">{{ uploading ? '正在上传并校验' : '选择备份文件' }}</UButton>
        </div>
      </div>
      <div v-if="uploading" class="space-y-2" aria-live="polite"><UProgress animation="carousel" /><p class="text-sm text-muted">正在上传并检查数据库与照片，请保持此页面打开。当前数据尚未改变。</p></div>
      <UAlert v-if="restoreError && !confirmOpen" :color="cacheRefreshFailed ? 'warning' : 'error'" variant="subtle" :title="cacheRefreshFailed ? '恢复完成，请刷新页面' : '备份未能通过处理'" :description="restoreError">
        <template v-if="cacheRefreshFailed" #actions><UButton color="warning" variant="soft" class="min-h-12" @click="reloadPage">刷新页面</UButton></template>
      </UAlert>
      <UAlert v-if="restoredResult" title="恢复完成，旧数据已保留" :description="`${restoredResult.message} 保留位置：data/${restoredResult.preservedBackup}`" color="success" variant="subtle" icon="i-lucide-circle-check" />
      <div class="border-t border-default pt-5">
        <div class="mb-3 flex items-center justify-between gap-3"><h3 class="text-sm font-semibold">每日数据库快照</h3><UButton color="neutral" variant="ghost" icon="i-lucide-refresh-cw" class="min-h-12" :loading="loading" @click="fetchBackups">刷新</UButton></div>
        <p class="mb-4 text-xs leading-6 text-muted">程序每天自动保存数据库，保留最近 {{ overview?.retentionCount || 14 }} 份，存放在电脑的 data/backup 目录中。完整备份还会保存照片。</p>
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <div v-else-if="loading && !overview" class="space-y-2"><USkeleton class="h-10 w-full" /><USkeleton class="h-10 w-full" /></div>
        <ul v-else-if="overview?.items.length" class="max-h-64 divide-y divide-default overflow-y-auto rounded-lg border border-default px-4">
          <li v-for="item in overview.items" :key="item.name" class="flex items-center justify-between gap-3 py-3 text-sm"><span class="flex items-center gap-2"><UIcon name="i-lucide-database" class="size-4 text-muted" /><time>{{ item.date }}</time></span><span class="tabular-nums text-muted">{{ formatBytes(item.bytes) }}</span></li>
        </ul>
        <p v-else class="py-5 text-center text-sm text-muted">暂无自动备份记录。</p>
      </div>
    </div>
  </UCard>

  <UModal v-model:open="confirmOpen" title="核对备份并确认恢复" description="请确认这正是需要恢复的备份。确认后会用它覆盖当前数据库和照片。" :dismissible="!restoring" :close="!restoring" :ui="{ content: 'sm:max-w-xl' }">
    <template #body>
      <div v-if="preview" class="space-y-5">
        <UAlert v-if="restoreError" :description="restoreError" color="error" variant="subtle" />
        <div class="rounded-xl bg-elevated/50 p-4"><p class="text-xs text-muted">备份生成时间</p><p class="mt-1 font-semibold">{{ dateTime(preview.createdAt) }}</p><p class="mt-2 text-xs text-muted">解压后 {{ formatBytes(preview.expandedBytes) }} · 请在 {{ dateTime(preview.expiresAt) }} 前确认</p></div>
        <dl class="grid grid-cols-2 gap-3"><div class="rounded-lg border border-default p-3"><dt class="text-xs text-muted">点位</dt><dd class="mt-1 text-2xl font-semibold">{{ preview.pointCount }}</dd></div><div class="rounded-lg border border-default p-3"><dt class="text-xs text-muted">排查记录</dt><dd class="mt-1 text-2xl font-semibold">{{ preview.inspectionCount }}</dd></div><div class="rounded-lg border border-default p-3"><dt class="text-xs text-muted">整改问题</dt><dd class="mt-1 text-2xl font-semibold">{{ preview.issueCount }}</dd></div><div class="rounded-lg border border-default p-3"><dt class="text-xs text-muted">照片</dt><dd class="mt-1 text-2xl font-semibold">{{ preview.photoCount }}</dd></div></dl>
        <UAlert title="当前数据将被覆盖" description="覆盖前会自动保留一份当前数据库和照片，恢复结束后显示保存位置。" color="warning" variant="subtle" icon="i-lucide-triangle-alert" />
        <UCheckbox v-model="confirmed" label="我已核对摘要，确认用这份备份覆盖当前数据" size="lg" :disabled="restoring" />
      </div>
    </template>
    <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="restoring" @click="confirmOpen = false">取消</UButton><UButton color="error" size="xl" class="min-h-12" :loading="restoring" :disabled="!confirmed" @click="restore">确认覆盖并恢复</UButton></div></template>
  </UModal>
</template>
