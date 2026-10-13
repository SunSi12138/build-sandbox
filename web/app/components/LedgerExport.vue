<script setup lang="ts">
import { errorMessage, todayLocal } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ kind: 'inspection' | 'campaign'; communityId?: string; typeId?: string }>()
const { bootstrap, load } = useAppData()
const kind = ref<'inspection' | 'rectification'>('inspection')
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
const canIncludePhotos = computed(() => props.kind === 'campaign' || kind.value === 'rectification')
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
  community.value = props.communityId || 'all'
  campaignType.value = props.typeId || 'all'
  photos.value = false
  loading.value = true
  try { await load(); selectRange('month') }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
})
async function exportLedger() {
  if (disabled.value) return
  if (!from.value || !to.value || from.value > to.value) { error.value = '请选择正确的时间范围'; return }
  busy.value = true
  error.value = ''
  const name = props.kind === 'campaign' ? '整治台账' : kind.value === 'inspection' ? '排查台账' : '整改台账'
  const route = props.kind === 'campaign' ? 'campaign-ledger' : `${kind.value}-ledger`
  const query = new URLSearchParams({ from: from.value, to: to.value })
  if (community.value !== 'all') query.set('communityId', community.value)
  if (props.kind === 'campaign' && campaignType.value !== 'all') query.set('typeId', campaignType.value)
  if (canIncludePhotos.value) query.set('photos', String(photos.value))
  try { await downloadWord(`/api/exports/${route}?${query}`, `${name}.docx`); open.value = false }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
</script>

<template>
  <UModal v-model:open="open" title="导出台账" :dismissible="!disabled" :close="!disabled" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <p class="text-xs text-muted" data-testid="ledger-scope-help">{{ props.kind === 'campaign' ? '导出使用下方日期、社区及整治类型，列表搜索词不参与。' : '导出使用下方日期和社区，列表搜索、点位、类型及状态筛选不参与。' }}</p>
        <UFormField v-if="props.kind === 'inspection'" label="台账类型">
          <div class="chip-row">
            <UButton v-for="option in [{ label: '排查台账', value: 'inspection' as const }, { label: '整改台账', value: 'rectification' as const }]" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="kind === option.value" :data-testid="`ledger-kind-${option.value}`" :disabled="disabled" @click="kind = option.value">{{ option.label }}</UButton>
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
        <UFormField v-if="props.kind === 'campaign'" label="整治类型"><USelect v-model="campaignType" :items="campaignTypes" class="w-full" :disabled="disabled" data-testid="ledger-campaign-type" /></UFormField>
        <div v-if="canIncludePhotos" class="check-row"><UCheckbox v-model="photos" label="附照片" :disabled="disabled" data-testid="ledger-photos" /></div>
      </div>
    </template>
    <template #footer>
      <UButton label="取消" color="neutral" variant="outline" :disabled="disabled" @click="open = false" />
      <UButton label="导出 Word" :loading="busy" :disabled="loading" data-testid="export-ledger" @click="exportLedger" />
    </template>
  </UModal>
</template>
