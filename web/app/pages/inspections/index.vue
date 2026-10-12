<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { InspectionCountsDto, InspectionDto, PageDto } from '~/types/api'
import { errorMessage, overdueDays, queryText, rectifyLabel, rectifyTone } from '~/utils/format'
useHead({ title: '排查记录 · 街道生态环保' })
const route = useRoute()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage, set } = useQueryFilters()
const q = field('q'), communityId = field('communityId', 'all'), typeId = field('typeId', 'all'), from = field('from'), to = field('to'), rectify = field('rectify', 'all')
const result = ref<PageDto<InspectionDto> | null>(null), counts = ref<InspectionCountsDto>({ all: 0, none: 0, pending: 0, feedback: 0, closed: 0, issueCount: 0 })
const loading = ref(true), error = ref(''), datesOpen = ref(false), exportOpen = ref(false)
let version = 0
const overdue = computed(() => queryText(route.query.overdue) === 'true'), temporary = computed(() => queryText(route.query.temporary) === 'true'), dueSoon = computed(() => queryText(route.query.dueSoon) === 'true')
const awaitingReview = computed(() => queryText(route.query.awaitingReview) === 'true'), openOnly = computed(() => queryText(route.query.openOnly) === 'true')
const issueFilterActive = computed(() => overdue.value || dueSoon.value || awaitingReview.value || openOnly.value)
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const types = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const statusOptions = computed(() => [{ label: '全部', value: 'all', count: counts.value.all }, { label: '待整改', value: 'pending', count: counts.value.pending }, { label: '待复查', value: 'feedback', count: counts.value.feedback }, { label: '已闭环', value: 'closed', count: counts.value.closed }, { label: '无需整改', value: 'none', count: counts.value.none }])
const columns: TableColumn<InspectionDto>[] = [{ accessorKey: 'date', header: '排查日期' }, { accessorKey: 'pointName', header: '点位' }, { accessorKey: 'inspector', header: '排查人' }, { accessorKey: 'rectifyStatus', header: '整改情况' }, { accessorKey: 'noticeNumber', header: '通知单编号' }]
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/inspections') return
  const current = ++version; loading.value = true; error.value = ''
  try {
    await loadAppData()
    const query = { q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value, typeId: typeId.value === 'all' ? undefined : typeId.value, from: from.value || undefined, to: to.value || undefined, pointId: queryText(route.query.pointId) || undefined, overdue: overdue.value || undefined, temporary: temporary.value || undefined, dueSoon: dueSoon.value || undefined, awaitingReview: awaitingReview.value || undefined, openOnly: openOnly.value || undefined }
    const [data, totals] = await Promise.all([$fetch<PageDto<InspectionDto>>('/api/inspections', { query: { ...query, rectify: rectify.value === 'all' ? undefined : rectify.value, page: page.value, pageSize: 30 } }), $fetch<InspectionCountsDto>('/api/inspections/counts', { query })])
    if (current === version) { result.value = data; counts.value = totals }
  } catch (value) { if (current === version) error.value = errorMessage(value) }
  finally { if (current === version) loading.value = false }
}
function detailLink(record: InspectionDto) { return { path: `/inspections/${record.id}`, query: { returnTo: route.fullPath } } }
function clearDates() { void navigateTo({ path: '/inspections', query: { ...route.query, from: undefined, to: undefined, page: undefined } }, { replace: true }) }
watch(() => route.fullPath, load, { immediate: true })
</script>
<template>
  <div class="page-stack compact-inspections"><PageHeader title="排查记录"><template #actions><UButton label="导出台账" color="neutral" variant="outline" class="desktop-only" @click="exportOpen = true" /><UButton to="/inspections/new" icon="i-lucide-plus" class="header-action"><span class="mobile-only">新增</span><span class="desktop-only">新增排查</span></UButton></template></PageHeader>
    <div class="chip-row" aria-label="整改状态"><UButton v-for="option in statusOptions" :key="option.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="rectify === option.value" @click="rectify = option.value">{{ option.label }} {{ option.count }}</UButton></div>
    <div class="chip-row"><UButton color="neutral" variant="ghost" class="filter-chip" :aria-pressed="overdue" @click="set('overdue', overdue ? '' : 'true')">仅逾期</UButton><UButton color="neutral" variant="ghost" class="filter-chip" :aria-pressed="temporary" @click="set('temporary', temporary ? '' : 'true')">仅临时点位</UButton><UButton v-if="dueSoon" label="3 天内到期" trailing-icon="i-lucide-x" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="true" @click="set('dueSoon', '')" /><UButton v-if="awaitingReview" label="待复查问题" trailing-icon="i-lucide-x" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="true" @click="set('awaitingReview', '')" /><UButton v-if="openOnly" label="未闭环问题" trailing-icon="i-lucide-x" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="true" @click="set('openOnly', '')" /></div>
    <div class="filter-box compact" data-testid="list-filters"><div class="filter-search"><UInput v-model="q" type="search" icon="i-lucide-search" placeholder="搜索点位、排查人、编号" aria-label="搜索排查" class="w-full" /><UButton color="neutral" variant="outline" class="filter-date-toggle" :aria-expanded="datesOpen" @click="datesOpen = !datesOpen">日期<span v-if="from || to" class="date-dot" /></UButton></div><div class="filter-field"><label>社区</label><USelect v-model="communityId" :items="communities" aria-label="社区筛选" class="w-full" /></div><div class="filter-field"><label>类型</label><USelect v-model="typeId" :items="types" aria-label="类型筛选" class="w-full" /></div><div class="filter-dates" :class="{ 'is-open': datesOpen }"><div class="filter-field"><label>开始日期</label><UInput v-model="from" type="date" aria-label="开始日期" class="w-full" /></div><div class="filter-field"><label>结束日期</label><UInput v-model="to" type="date" aria-label="结束日期" class="w-full" /></div></div></div>
    <div v-if="from || to" class="chip-row"><UButton :label="`${from || '不限'} 至 ${to || '不限'}`" trailing-icon="i-lucide-x" variant="ghost" color="neutral" class="filter-chip" :aria-pressed="true" @click="clearDates" /></div>
    <div class="filter-tool-row"><span v-if="issueFilterActive">涉及 {{ result?.total ?? '—' }} 条排查 · {{ result?.matchingIssueCount ?? 0 }} 项问题</span><span v-else>共 {{ result?.total ?? '—' }} 条</span><div class="flex items-center gap-1"><UButton v-if="Object.keys(route.query).length" to="/inspections" label="清空" color="neutral" variant="link" /><UButton label="导出台账" color="neutral" variant="link" class="mobile-only" @click="exportOpen = true" /></div></div>
    <PageState :loading="loading" :error="error" @retry="load"><template v-if="result?.items.length"><div class="panel table-wrap"><UTable :data="result.items" :columns="columns" @select="(_event, row) => navigateTo(detailLink(row.original))"><template #date-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="text-primary">{{ row.original.date }}</NuxtLink></template><template #pointName-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="block max-w-72"><p class="truncate font-medium">{{ row.original.pointName }} <StatusBadge v-if="row.original.pointTemporary" tone="outline">临时</StatusBadge></p><p class="truncate text-xs text-muted">{{ row.original.communityName }} · {{ row.original.typeName }}</p></NuxtLink></template><template #rectifyStatus-cell="{ row }"><div class="flex items-center gap-1"><StatusBadge :tone="rectifyTone(row.original.rectifyStatus)">{{ rectifyLabel(row.original, true) }}</StatusBadge><StatusBadge v-if="row.original.overdueCount" tone="danger">逾期 {{ overdueDays(row.original.nearestDeadline) }} 天</StatusBadge></div></template><template #noticeNumber-cell="{ row }">{{ row.original.noticeNumber || '—' }}</template></UTable></div><div class="group-list mobile-records"><InspectionRow v-for="record in result.items" :key="record.id" :record="record" :return-to="route.fullPath" /></div><div v-if="result.total > 30" class="flex justify-center"><UPagination :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" /></div></template><div v-else class="panel"><EmptyState title="暂无排查记录"><UButton to="/inspections/new" label="新增排查" /></EmptyState></div></PageState>
    <LedgerExport v-model:open="exportOpen" kind="inspection" :community-id="communityId === 'all' ? undefined : communityId" />
  </div>
</template>
