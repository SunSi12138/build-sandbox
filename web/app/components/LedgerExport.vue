<script setup lang="ts">
import { errorMessage, todayLocal } from '~/utils/format'
import type { PageDto, PointDto } from '~/types/api'
import { downloadWord } from '~/utils/wordDownload'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ kind: 'inspection' | 'campaign' | 'point'; communityId?: string; typeId?: string }>()
const { bootstrap, load } = useAppData()
const kind = ref<'inspection' | 'rectification' | 'campaign'>('inspection')
const ledgerKind = computed(() => props.kind === 'campaign' ? 'campaign' : kind.value)
const kindOptions = computed(() => [
  { label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const },
  ...(props.kind === 'point' ? [{ label: '整治台账', value: 'campaign' as const }] : []),
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
const campaignType = ref('all')
const photos = ref(false)
const busy = ref(false)
const loading = ref(false)
const error = ref('')
const disabled = computed(() => busy.value || loading.value)
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const campaignTypes = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.campaignTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const ranges = [{ label: '本月', value: 'month' }, { label: '本季', value: 'quarter' }, { label: '本年', value: 'year' }, { label: '自定义', value: 'custom' }]
const canIncludePhotos = computed(() => ledgerKind.value !== 'inspection')
function selectRange(value: string) {
  range.value = value
  if (value === 'custom') return
  const today = bootstrap.value?.today ?? todayLocal()
  const [year, month] = today.split('-').map(Number)
  const firstMonth = value === 'year' ? 1 : value === 'quarter' ? Math.floor(((month ?? 1) - 1) / 3) * 3 + 1 : month ?? 1
  const lastMonth = value === 'year' ? 12 : value === 'quarter' ? firstMonth + 2 : firstMonth
  const lastDay = new Date(year ?? 2026, lastMonth, 0).getDate()
  from.value = `${year}-${String(firstMonth).padStart(2, '0')}-01`
  to.value = `${year}-${String(lastMonth).padStart(2, '0')}-${String(lastDay).padStart(2, '0')}`
}
watch(open, async value => {
  if (!value) return
  error.value = ''
  selectedPoints.value = []
  pointSearch.value = ''
  kind.value = 'inspection'
  community.value = props.communityId || 'all'
  campaignType.value = props.typeId || 'all'
  photos.value = false
  loading.value = true
  try { await load(); selectRange('month'); if (props.kind === 'point') await listPoints() }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
})
async function exportLedger() {
  if (disabled.value) return
  if (!from.value || !to.value || from.value > to.value) { error.value = '请选择正确的时间范围'; return }
  if (props.kind === 'point' && !selectedPoints.value.length) { error.value = '请选择点位'; return }
  busy.value = true
  error.value = ''
  const name = ledgerKind.value === 'campaign' ? '整治台账' : kind.value === 'inspection' ? '排查台账' : '整改台账'
  const route = `${ledgerKind.value}-ledger`
  const query = new URLSearchParams({ from: from.value, to: to.value })
  if (community.value !== 'all') query.set('communityId', community.value)
  if (ledgerKind.value === 'campaign' && campaignType.value !== 'all') query.set('typeId', campaignType.value)
  if (props.kind === 'point') for (const point of selectedPoints.value) query.append('pointId', point.id)
  if (canIncludePhotos.value) query.set('photos', String(photos.value))
  try { await downloadWord(`/api/exports/${route}?${query}`, `${name}.docx`); open.value = false }
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
        <p v-if="props.kind !== 'point'" class="text-xs text-muted" data-testid="ledger-scope-help">{{ props.kind === 'campaign' ? '导出使用下方日期、社区及整治类型，列表搜索词不参与。' : '导出使用下方日期和社区，列表搜索、点位、类型及状态筛选不参与。' }}</p>
        <UFormField v-if="props.kind !== 'campaign'" label="台账类型">
          <div class="chip-row">
            <UButton v-for="option in kindOptions" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="kind === option.value" :data-testid="`ledger-kind-${option.value}`" :disabled="disabled" @click="kind = option.value">{{ option.label }}</UButton>
          </div>
        </UFormField>
        <UFormField v-if="props.kind === 'point'" label="点位" required>
          <UInput v-model="pointSearch" type="search" placeholder="搜索点位" class="w-full" :disabled="disabled" data-testid="ledger-point-search" />
          <div v-if="selectedPoints.length" class="mt-2 flex flex-wrap gap-1"><UButton v-for="point in selectedPoints" :key="point.id" :label="point.name" trailing-icon="i-lucide-x" color="neutral" variant="soft" :disabled="disabled" :aria-label="`移除点位 ${point.name}`" @click="selectPoint(point, false)" /></div>
          <div class="mt-2 max-h-48 overflow-y-auto" data-testid="ledger-point-options">
            <UCheckbox v-for="point in pointOptions" :key="point.id" :label="point.name" :model-value="selectedPoints.some(p => p.id === point.id)" :disabled="disabled" class="check-row" @update:model-value="selectPoint(point, $event)" />
            <p v-if="!pointsBusy && !pointOptions.length" class="text-xs text-muted">暂无点位</p>
            <UButton v-if="pointOptions.length < pointTotal" label="加载更多" color="neutral" variant="link" :loading="pointsBusy" :disabled="disabled" @click="listPoints(false)" />
          </div>
        </UFormField>
        <UFormField label="时间范围" help="日期单独选择，默认本月">
          <div class="chip-row"><UButton v-for="option in ranges" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="range === option.value" :disabled="disabled" @click="selectRange(option.value)">{{ option.label }}</UButton></div>
        </UFormField>
        <p v-if="range !== 'custom'" class="text-sm" data-testid="ledger-dates">{{ from }} 至 {{ to }}</p>
        <div v-else class="field-grid">
          <UFormField label="开始日期"><UInput v-model="from" type="date" class="w-full" :max="to || undefined" :disabled="disabled" data-testid="ledger-from" /></UFormField>
          <UFormField label="结束日期"><UInput v-model="to" type="date" class="w-full" :min="from || undefined" :disabled="disabled" data-testid="ledger-to" /></UFormField>
        </div>
        <UFormField label="所属社区"><USelect v-model="community" :items="communities" class="w-full" :disabled="disabled" data-testid="ledger-community" /></UFormField>
        <UFormField v-if="ledgerKind === 'campaign'" label="整治类型"><USelect v-model="campaignType" :items="campaignTypes" class="w-full" :disabled="disabled" data-testid="ledger-campaign-type" /></UFormField>
        <div v-if="canIncludePhotos" class="check-row"><UCheckbox v-model="photos" label="附照片" :disabled="disabled" data-testid="ledger-photos" /></div>
      </div>
    </template>
    <template #footer>
      <UButton label="取消" color="neutral" variant="outline" :disabled="disabled" @click="open = false" />
      <UButton label="导出 Word" :loading="busy" :disabled="loading" data-testid="export-ledger" @click="exportLedger" />
    </template>
  </UModal>
</template>
