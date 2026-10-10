<script setup lang="ts">
import type { IssueDto, PageDto } from '~/types/api'
import { errorMessage, queryText, statusColor, statusLabel, todayLocal } from '~/utils/format'

useHead({ title: '台账导出 · 街道生态环保' })
const route = useRoute()
const router = useRouter()
const { bootstrap, load: loadAppData } = useAppData()
const { field, set } = useQueryFilters()
const today = todayLocal()
const date = new Date(`${today}T12:00:00`)
const monthStart = `${today.slice(0, 7)}-01`
const format = (value: Date) => `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`
const monthEnd = format(new Date(date.getFullYear(), date.getMonth() + 1, 0))
const quarterMonth = Math.floor(date.getMonth() / 3) * 3
const quarterStart = format(new Date(date.getFullYear(), quarterMonth, 1))
const quarterEnd = format(new Date(date.getFullYear(), quarterMonth + 3, 0))
const from = field('from', monthStart), to = field('to', monthEnd), communityId = field('communityId', 'all')
const photos = computed({ get: () => queryText(route.query.photos) === 'true', set: (value: boolean) => set('photos', value ? 'true' : '') })
const preset = computed(() => from.value === monthStart && to.value === monthEnd ? 'month' : from.value === quarterStart && to.value === quarterEnd ? 'quarter' : 'custom')
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const result = ref<PageDto<IssueDto> | null>(null)
const loading = ref(true), error = ref('')
const validRange = computed(() => /^\d{4}-\d{2}-\d{2}$/.test(from.value) && /^\d{4}-\d{2}-\d{2}$/.test(to.value) && from.value <= to.value)
const exportUrl = computed(() => {
  const query = new URLSearchParams({ from: from.value, to: to.value, photos: String(photos.value) })
  if (communityId.value !== 'all') query.set('communityId', communityId.value)
  return `/api/exports/ledger?${query.toString()}`
})
function choose(value: 'month' | 'quarter') {
  void router.replace({ path: '/exports', query: { ...route.query, from: value === 'month' ? monthStart : quarterStart, to: value === 'month' ? monthEnd : quarterEnd } })
}
let requestVersion = 0
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/exports') return
  const version = ++requestVersion
  error.value = ''
  if (!validRange.value) { loading.value = false; result.value = null; return }
  loading.value = true
  try {
    await loadAppData()
    const data = await $fetch<PageDto<IssueDto>>('/api/issues', { query: { from: from.value, to: to.value, communityId: communityId.value === 'all' ? undefined : communityId.value, pageSize: 5 } })
    if (version === requestVersion) result.value = data
  } catch (value) { if (version === requestVersion) error.value = errorMessage(value) }
  finally { if (version === requestVersion) loading.value = false }
}
watch(() => route.fullPath, load, { immediate: true })
</script>

<template>
  <div class="page-stack">
    <PageHeader title="导出整改台账" description="选择排查时间与社区，生成可编辑的 Word 台账，用于归档与工作留痕。" />
    <div class="export-columns">
      <UCard>
        <template #header><h2 class="section-title">导出范围</h2></template>
        <div class="space-y-6">
          <div><p class="mb-3 text-sm font-medium text-highlighted">排查时间</p><div class="flex flex-wrap gap-2"><UButton label="本月" :variant="preset === 'month' ? 'soft' : 'outline'" :color="preset === 'month' ? 'primary' : 'neutral'" @click="choose('month')" /><UButton label="本季" :variant="preset === 'quarter' ? 'soft' : 'outline'" :color="preset === 'quarter' ? 'primary' : 'neutral'" @click="choose('quarter')" /><UBadge v-if="preset === 'custom'" variant="subtle" color="neutral">自定义时间</UBadge></div></div>
          <div class="field-grid"><UFormField label="开始日期" required><UInput v-model="from" type="date" class="w-full" /></UFormField><UFormField label="结束日期" required><UInput v-model="to" type="date" class="w-full" /></UFormField></div>
          <UAlert v-if="!validRange" color="warning" variant="subtle" description="请填写完整日期，结束日期不能早于开始日期。" />
          <UFormField label="所属社区"><USelect v-model="communityId" :items="communities" class="w-full" /></UFormField>
          <div class="rounded-xl border border-default bg-muted/40 p-4"><UCheckbox v-model="photos" label="附整改前后对比照片" description="在台账后按问题追加照片页；照片较多时文件会相应增大。" /></div>
          <UButton :to="exportUrl" external :prefetch="false" label="下载 Word 台账" icon="i-lucide-file-down" :disabled="!validRange || loading || !!error" class="w-full justify-center" data-testid="export-ledger" />
        </div>
      </UCard>
      <div class="space-y-5">
        <UCard>
          <h2 class="section-title">这份台账包含什么</h2>
          <ul class="mt-5 space-y-4 text-sm leading-6 text-muted"><li class="flex gap-3"><UIcon name="i-lucide-check" class="mt-1 size-4 shrink-0 text-primary" /><span>横向 A4，逐条列出排查日期、点位、社区、问题与整改期限。</span></li><li class="flex gap-3"><UIcon name="i-lucide-check" class="mt-1 size-4 shrink-0 text-primary" /><span>同步保留状态、社区反馈和现场复查结论。</span></li><li class="flex gap-3"><UIcon name="i-lucide-check" class="mt-1 size-4 shrink-0 text-primary" /><span>末尾汇总闭环、未闭环和逾期数量。</span></li></ul>
          <div class="mt-6 border-t border-default pt-5"><p class="text-xs text-muted">需要单次排查记录或整改通知单？</p><UButton to="/inspections" label="前往排查记录" trailing-icon="i-lucide-arrow-right" color="neutral" variant="link" class="-ml-2 mt-1" /></div>
        </UCard>
      </div>
    </div>
    <UCard v-if="validRange">
      <template #header><div class="flex items-center justify-between gap-3"><h2 class="section-title">范围预览</h2><UBadge color="neutral" variant="subtle">{{ result?.total ?? '—' }} 条问题</UBadge></div></template>
      <PageState :loading="loading" :error="error" @retry="load">
        <div v-if="result?.items.length" class="divide-y divide-default"><div v-for="issue in result.items" :key="issue.id" class="flex items-start justify-between gap-4 py-4 first:pt-0 last:pb-0"><div class="min-w-0"><p class="text-sm font-medium text-highlighted">{{ issue.pointName }}<span class="ml-3 text-xs font-normal text-muted">{{ issue.communityName }}</span></p><p class="mt-1.5 line-clamp-2 text-sm leading-6 text-muted">{{ issue.description }}</p><p class="mt-2 text-xs text-dimmed">排查 {{ issue.inspectionDate }} · 期限 {{ issue.deadline }}</p></div><UBadge :color="statusColor(issue.status)" variant="subtle" class="shrink-0">{{ statusLabel(issue.status) }}</UBadge></div></div>
        <EmptyState v-else title="所选范围内暂无问题" description="可以调整时间或社区，再导出需要的台账。" icon="i-lucide-file-down" />
        <p v-if="result && result.total > 5" class="mt-5 border-t border-default pt-4 text-xs text-muted">此处预览前 5 条，导出文件将包含范围内全部 {{ result.total }} 条问题。</p>
      </PageState>
    </UCard>
  </div>
</template>

<style scoped>
.export-columns { display:grid; grid-template-columns:minmax(0,1fr); gap:24px; }
@media(min-width:1100px) { .export-columns { grid-template-columns:minmax(0,1.35fr) minmax(280px,1fr); } }
</style>
