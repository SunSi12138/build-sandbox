<script setup lang="ts">
import { errorMessage, todayLocal } from '~/utils/format'
import type { PageDto, PointDto } from '~/types/api'
import { ledgerSearchParams, currentLedgerFilters, ledgerDates, ledgerFilterSummary, ledgerRanges } from '~/utils/ledger'
import { downloadWord } from '~/utils/wordDownload'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ kind: 'inspection' | 'point'; communityId?: string; typeId?: string; currentQuery?: Record<string, unknown> }>()
const { bootstrap, load } = useAppData()
const kind = ref<'inspection' | 'rectification' | 'remediation'>('inspection')
const ledgerKind = computed(() => kind.value)
const kindOptions = computed(() => [
  { label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const },
  ...(props.kind === 'point' ? [{ label: '整治台账', value: 'remediation' as const }] : []),
])
const pointSearch = ref('')
const pointOptions = ref<PointDto[]>([])
const selectedPoints = ref<PointDto[]>([])
const pointsBusy = ref(false)
const pointPage = ref(1)
const pointTotal = ref(0)
let pointSequence = 0
let pointTimer: ReturnType<typeof setTimeout> | undefined
const range = ref('month')
const from = ref('')
const to = ref('')
const community = ref('all')
const remediationType = ref('all')
const photos = ref(false)
const busy = ref(false)
const loading = ref(false)
const error = ref('')
const disabled = computed(() => busy.value || loading.value)
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const remediationTypes = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.remediationTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const ranges = ledgerRanges
const scope = ref<'current' | 'custom'>('custom')
const canIncludePhotos = computed(() => ledgerKind.value !== 'inspection')
const query = computed(() => {
  if (scope.value === 'current' && props.kind !== 'point') return currentLedgerFilters(props.kind, props.currentQuery)
  const result: Record<string, string> = { from: from.value, to: to.value }
  if (community.value !== 'all') result.communityId = community.value
  if (ledgerKind.value === 'remediation' && remediationType.value !== 'all') result.remediationTypeId = remediationType.value
  return result
})
const summary = computed(() => ledgerFilterSummary(props.kind === 'point' ? 'inspection' : props.kind, query.value, bootstrap.value))
const preview = ref<number | null>(null), previewBusy = ref(false), previewError = ref('')
let previewRequest: AbortController | undefined
let previewTimer: ReturnType<typeof setTimeout> | undefined
let previewVersion = 0
function selectRange(value: string) {
  range.value = value
  if (value !== 'custom') {
    const dates = ledgerDates(value, bootstrap.value?.today ?? todayLocal())
    from.value = dates.from; to.value = dates.to
  }
}
async function loadPreview() {
  const version = ++previewVersion
  previewRequest?.abort()
  preview.value = null; previewError.value = ''
  if (!open.value || props.kind === 'point') { previewBusy.value = false; return }
  previewBusy.value = true
  const controller = new AbortController(); previewRequest = controller
  try {
    const data = await $fetch<PageDto<unknown>>('/api/inspections', { signal: controller.signal, query: { ...query.value, page: 1, pageSize: 1 } })
    if (version === previewVersion) preview.value = ledgerKind.value === 'rectification' ? data.matchingIssueCount ?? 0 : data.total
  } catch (cause) { if (version === previewVersion && !controller.signal.aborted) previewError.value = errorMessage(cause) }
  finally { if (version === previewVersion) previewBusy.value = false }
}
watch([open, query, ledgerKind], () => {
  ++previewVersion; previewRequest?.abort(); clearTimeout(previewTimer)
  preview.value = null; previewError.value = ''; previewBusy.value = open.value && props.kind !== 'point'
  if (open.value) previewTimer = setTimeout(() => { void loadPreview() }, 150)
}, { flush: 'sync' })
onBeforeUnmount(() => { clearTimeout(previewTimer); previewRequest?.abort() })
watch(open, async value => {
  if (!value) return
  error.value = ''
  selectedPoints.value = []
  pointSearch.value = ''
  kind.value = 'inspection'
  scope.value = props.currentQuery !== undefined && props.kind !== 'point' ? 'current' : 'custom'
  community.value = props.communityId || 'all'
  remediationType.value = props.typeId || 'all'
  photos.value = false
  loading.value = true
  try { await load(); selectRange('month'); if (props.kind === 'point') await listPoints() }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
})
async function exportLedger() {
  if (disabled.value || previewBusy.value || previewError.value) return
  if (scope.value === 'custom' && (!from.value || !to.value || from.value > to.value)) { error.value = '请选择正确的时间范围'; return }
  if (props.kind === 'point' && !selectedPoints.value.length) { error.value = '请选择点位'; return }
  busy.value = true
  error.value = ''
  const name = ledgerKind.value === 'remediation' ? '整治台账' : kind.value === 'inspection' ? '排查台账' : '整改台账'
  const route = `${ledgerKind.value}-ledger`
  const parameters = ledgerSearchParams({ ...query.value, scope: scope.value })
  if (props.kind === 'point') for (const point of selectedPoints.value) parameters.append('pointId', point.id)
  if (canIncludePhotos.value) parameters.set('photos', String(photos.value))
  try { await downloadWord(`/api/exports/${route}?${parameters}`, `${name}.docx`); open.value = false }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
async function listPoints(reset = true) {
  const sequence = ++pointSequence
  pointsBusy.value = true
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { temporary: 'all', disabled: 'all', q: pointSearch.value.trim() || undefined, page: reset ? 1 : pointPage.value + 1, pageSize: 10 } })
    if (sequence !== pointSequence) return
    pointOptions.value = reset ? result.items : [...pointOptions.value, ...result.items]
    pointPage.value = result.page
    pointTotal.value = result.total
  } catch (cause) { if (sequence === pointSequence) error.value = errorMessage(cause) }
  finally { if (sequence === pointSequence) pointsBusy.value = false }
}
function selectPoint(point: PointDto, checked: boolean | 'indeterminate') {
  if (disabled.value) return
  if (!checked) selectedPoints.value = selectedPoints.value.filter(p => p.id !== point.id)
  else if (!selectedPoints.value.some(p => p.id === point.id)) {
    if (selectedPoints.value.length >= 200) { error.value = '最多选择 200 个点位'; return }
    selectedPoints.value.push(point)
  }
}
watch(pointSearch, () => { clearTimeout(pointTimer); if (open.value && props.kind === 'point') pointTimer = setTimeout(() => { void listPoints() }, 250) })
onBeforeUnmount(() => { clearTimeout(pointTimer); pointSequence++ })
</script>

<template>
  <UModal v-model:open="open" title="导出台账" :dismissible="!disabled" :close="!disabled" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <UFormField v-if="currentQuery !== undefined && props.kind !== 'point'" label="导出范围">
          <div class="chip-row"><UButton label="当前筛选" :aria-pressed="scope === 'current'" color="neutral" variant="ghost" class="filter-chip" :disabled="disabled" @click="scope = 'current'" /><UButton label="自定义范围" :aria-pressed="scope === 'custom'" color="neutral" variant="ghost" class="filter-chip" :disabled="disabled" @click="scope = 'custom'" /></div>
        </UFormField>
        <UFormField label="台账类型">
          <div class="chip-row">
            <UButton v-for="option in kindOptions" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="kind === option.value" :data-testid="`ledger-kind-${option.value}`" :disabled="disabled" @click="kind = option.value">{{ option.label }}</UButton>
          </div>
        </UFormField>
        <template v-if="props.kind === 'point'"><UFormField label="点位" required>
          <UInput v-model="pointSearch" type="search" placeholder="搜索点位" class="w-full" :disabled="disabled" data-testid="ledger-point-search" /></UFormField>
          <div v-if="selectedPoints.length" class="mt-2 flex flex-wrap gap-1"><UButton v-for="point in selectedPoints" :key="point.id" :label="point.name" trailing-icon="i-lucide-x" color="neutral" variant="soft" :disabled="disabled" :aria-label="`移除点位 ${point.name}`" @click="selectPoint(point, false)" /></div>
          <div class="mt-2 max-h-48 overflow-y-auto" data-testid="ledger-point-options">
            <UCheckbox v-for="point in pointOptions" :key="point.id" :id="`ledger-point-${point.id}`" :label="point.name" :aria-label="point.name" :model-value="selectedPoints.some(p => p.id === point.id)" :disabled="disabled" class="check-row" @update:model-value="selectPoint(point, $event)" />
            <p v-if="!pointsBusy && !pointOptions.length" class="text-xs text-muted">暂无点位</p>
            <UButton v-if="pointOptions.length < pointTotal" label="加载更多" color="neutral" variant="link" :loading="pointsBusy" :disabled="disabled" @click="listPoints(false)" />
          </div>
        </template>
        <template v-if="scope === 'custom'">
        <UFormField label="时间范围">
          <div class="chip-row"><UButton v-for="option in ranges" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="range === option.value" :disabled="disabled" @click="selectRange(option.value)">{{ option.label }}</UButton></div>
        </UFormField>
        <p v-if="range !== 'custom'" class="text-sm" data-testid="ledger-dates">{{ from }} 至 {{ to }}</p>
        <div v-else class="field-grid">
          <UFormField label="开始日期"><UInput v-model="from" type="date" class="w-full" :max="to || undefined" :disabled="disabled" data-testid="ledger-from" /></UFormField>
          <UFormField label="结束日期"><UInput v-model="to" type="date" class="w-full" :min="from || undefined" :disabled="disabled" data-testid="ledger-to" /></UFormField>
        </div>
        <UFormField label="所属社区"><USelect v-model="community" :items="communities" class="w-full" :disabled="disabled" data-testid="ledger-community" /></UFormField>
        <UFormField v-if="ledgerKind === 'remediation'" label="整治类型"><USelect v-model="remediationType" :items="remediationTypes" class="w-full" :disabled="disabled" data-testid="ledger-remediation-type" /></UFormField>
        </template>
        <p class="text-sm text-muted" data-testid="ledger-summary">{{ summary }}</p>
        <p v-if="props.kind !== 'point'" class="text-sm" aria-live="polite" data-testid="ledger-preview">{{ previewBusy ? '正在核对记录…' : preview === null ? '' : `预计 ${preview} ${ledgerKind === 'rectification' ? '项问题' : '条记录'}` }}</p>
        <UAlert v-if="previewError" :description="previewError" color="error"><template #actions><UButton label="重试" @click="loadPreview" /></template></UAlert>
        <div v-if="canIncludePhotos" class="check-row"><UCheckbox v-model="photos" label="附照片" :disabled="disabled" data-testid="ledger-photos" /></div>
      </div>
    </template>
    <template #footer>
      <UButton label="取消" color="neutral" variant="outline" :disabled="disabled" @click="open = false" />
      <UButton label="导出 Word" :loading="busy" :disabled="loading || previewBusy || !!previewError" data-testid="export-ledger" @click="exportLedger" />
    </template>
  </UModal>
</template>
