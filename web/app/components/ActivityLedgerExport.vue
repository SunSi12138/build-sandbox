<script setup lang="ts">
import type { PageDto } from '~/types/api'
import { downloadWord } from '~/utils/wordDownload'
import { errorMessage, todayLocal } from '~/utils/format'
import { currentLedgerFilters, ledgerDates, ledgerFilterSummary, ledgerRanges } from '~/utils/ledger'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ communityId?: string; typeId?: string; currentQuery?: Record<string, unknown> }>()
const { bootstrap, load } = useAppData()
const from = ref(''), to = ref(''), range = ref('month')
const community = ref('all'), type = ref('all')
const scope = ref<'current' | 'custom'>('current')
const includePhotos = ref(false), exporting = ref(false), loading = ref(false), error = ref('')
const preview = ref<number | null>(null), previewBusy = ref(false), previewError = ref('')
const disabled = computed(() => exporting.value || loading.value)
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(c => ({ label: c.name, value: c.id }))])
const types = computed(() => [{ label: '全部类型', value: 'all' }, ...(bootstrap.value?.activityTypes ?? []).map(t => ({ label: t.name, value: t.id }))])
const query = computed(() => {
  if (scope.value === 'current') return currentLedgerFilters('activity', props.currentQuery)
  const result: Record<string, string> = { from: from.value, to: to.value }
  if (community.value !== 'all') result.communityId = community.value
  if (type.value !== 'all') result.typeId = type.value
  return result
})
const summary = computed(() => ledgerFilterSummary('activity', query.value, bootstrap.value))
function selectRange(value: string) {
  range.value = value
  if (value !== 'custom') { const dates = ledgerDates(value, bootstrap.value?.today ?? todayLocal()); from.value = dates.from; to.value = dates.to }
}
let request: AbortController | undefined
let version = 0
let timer: ReturnType<typeof setTimeout> | undefined
async function loadPreview() {
  const current = ++version; request?.abort()
  preview.value = null; previewError.value = ''
  if (!open.value) { previewBusy.value = false; return }
  previewBusy.value = true
  const controller = new AbortController(); request = controller
  try {
    const result = await $fetch<PageDto<unknown>>('/api/activities', { signal: controller.signal, query: { ...query.value, page: 1, pageSize: 1 } })
    if (current === version) preview.value = result.total
  } catch (cause) { if (current === version && !controller.signal.aborted) previewError.value = errorMessage(cause) }
  finally { if (current === version) previewBusy.value = false }
}
watch([open, query], () => {
  ++version; request?.abort(); clearTimeout(timer)
  preview.value = null; previewError.value = ''; previewBusy.value = open.value
  if (open.value) timer = setTimeout(() => { void loadPreview() }, 150)
}, { flush: 'sync' })
onBeforeUnmount(() => { clearTimeout(timer); request?.abort() })
watch(open, async value => {
  if (!value) return
  error.value = ''
  scope.value = props.currentQuery !== undefined ? 'current' : 'custom'
  community.value = props.communityId || 'all'; type.value = props.typeId || 'all'; includePhotos.value = false
  loading.value = true
  try { await load(); selectRange('month') }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
})
async function exportWord() {
  if (disabled.value || previewBusy.value || previewError.value) return
  if (scope.value === 'custom' && (!from.value || !to.value || from.value > to.value)) { error.value = '请选择正确的日期范围'; return }
  exporting.value = true; error.value = ''
  const parameters = new URLSearchParams({ ...query.value, scope: scope.value, photos: String(includePhotos.value) })
  try { await downloadWord(`/api/exports/activity-ledger?${parameters}`, '活动台账.docx'); open.value = false }
  catch (cause) { error.value = errorMessage(cause) }
  finally { exporting.value = false }
}
</script>
<template>
  <UModal v-model:open="open" title="导出活动台账" :dismissible="!exporting" :close="!exporting" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <UFormField v-if="currentQuery !== undefined" label="导出范围"><div class="chip-row">
          <UButton label="当前筛选" :aria-pressed="scope === 'current'" color="neutral" variant="ghost" class="filter-chip" :disabled="disabled" @click="scope = 'current'" />
          <UButton label="自定义范围" :aria-pressed="scope === 'custom'" color="neutral" variant="ghost" class="filter-chip" :disabled="disabled" @click="scope = 'custom'" />
        </div></UFormField>
        <template v-if="scope === 'custom'">
          <UFormField label="时间范围"><div class="chip-row"><UButton v-for="item in ledgerRanges" :key="item.value" :label="item.label" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="range === item.value" :disabled="disabled" @click="selectRange(item.value)" /></div></UFormField>
          <p v-if="range !== 'custom'" class="text-sm">{{ from }} 至 {{ to }}</p>
          <div class="field-grid">
            <template v-if="range === 'custom'">
              <UFormField label="开始日期"><UInput v-model="from" type="date" :max="to || undefined" class="w-full" :disabled="disabled" /></UFormField>
              <UFormField label="结束日期"><UInput v-model="to" type="date" :min="from || undefined" class="w-full" :disabled="disabled" /></UFormField>
            </template>
            <UFormField label="所属社区"><USelect v-model="community" :items="communities" class="w-full" :disabled="disabled" /></UFormField>
            <UFormField label="活动类型"><USelect v-model="type" :items="types" class="w-full" :disabled="disabled" /></UFormField>
          </div>
        </template>
        <p class="text-sm text-muted" data-testid="ledger-summary">{{ summary }}</p>
        <p class="text-sm" aria-live="polite" data-testid="ledger-preview">{{ previewBusy ? '正在核对记录…' : preview === null ? '' : `预计 ${preview} 条记录` }}</p>
        <UAlert v-if="previewError" :description="previewError" color="error"><template #actions><UButton label="重试" @click="loadPreview" /></template></UAlert>
        <UCheckbox v-model="includePhotos" label="附活动照片" :disabled="disabled" />
      </div>
    </template>
    <template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="exporting" @click="open = false" /><UButton label="导出 Word" :loading="exporting" :disabled="disabled || previewBusy || !!previewError" data-testid="activity-ledger-export" @click="exportWord" /></template>
  </UModal>
</template>
