<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { InspectionDto, PageDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'

useHead({ title: '排查记录 · 街道生态环保' })
const route = useRoute()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage } = useQueryFilters()
const q = field('q'), communityId = field('communityId', 'all'), from = field('from'), to = field('to')
const loading = ref(true), error = ref('')
const result = ref<PageDto<InspectionDto> | null>(null)
let version = 0
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const columns: TableColumn<InspectionDto>[] = [
  { accessorKey: 'date', header: '排查日期' }, { accessorKey: 'pointName', header: '点位 / 社区' },
  { accessorKey: 'inspector', header: '排查人' }, { accessorKey: 'result', header: '排查结果' },
  { accessorKey: 'noticeNumber', header: '通知单编号' }, { id: 'actions', header: '' }
]
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/inspections') return
  const current = ++version
  loading.value = true; error.value = ''
  try {
    await loadAppData()
    const data = await $fetch<PageDto<InspectionDto>>('/api/inspections', { query: {
      q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value,
      from: from.value || undefined, to: to.value || undefined, pointId: queryText(route.query.pointId) || undefined,
      page: page.value, pageSize: 30
    } })
    if (current === version) result.value = data
  } catch (value) { if (current === version) error.value = errorMessage(value) }
  finally { if (current === version) loading.value = false }
}
watch(() => route.fullPath, load, { immediate: true })
function detailLink(record: InspectionDto) { return { path: `/inspections/${record.id}`, query: { returnTo: route.fullPath } } }
</script>

<template>
  <div class="page-stack">
    <PageHeader title="排查记录" description="正常与发现问题的排查，都会保存在这里。进入记录可导出 Word。">
      <template #actions><UButton to="/inspections/new" icon="i-lucide-plus" label="新建排查" /></template>
    </PageHeader>
    <UCard>
      <div class="field-grid">
        <UFormField label="搜索记录"><UInput v-model="q" type="search" icon="i-lucide-search" placeholder="点位、排查人或通知单编号" class="w-full" /></UFormField>
        <UFormField label="所属社区"><USelect v-model="communityId" :items="communities" class="w-full" /></UFormField>
        <UFormField label="开始日期"><UInput v-model="from" type="date" class="w-full" /></UFormField>
        <UFormField label="结束日期"><UInput v-model="to" type="date" class="w-full" /></UFormField>
      </div>
      <div class="mt-4 flex items-center justify-between border-t border-default pt-4"><p class="text-xs text-muted">共 <span class="font-semibold text-default">{{ result?.total ?? '—' }}</span> 条排查记录</p><UButton v-if="Object.keys(route.query).length" to="/inspections" label="清空筛选" icon="i-lucide-x" color="neutral" variant="link" class="-my-2 text-xs" /></div>
    </UCard>
    <PageState :loading="loading" :error="error" @retry="load">
      <template v-if="result?.items.length">
        <UCard class="table-wrap" :ui="{ body: 'p-0 sm:p-0' }">
          <UTable :data="result.items" :columns="columns">
            <template #date-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="text-sm font-medium tabular-nums text-primary">{{ row.original.date }}</NuxtLink></template>
            <template #pointName-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="block max-w-64"><p class="truncate font-medium text-highlighted">{{ row.original.pointName }}</p><p class="mt-1.5 text-xs text-muted">{{ row.original.communityName }}</p></NuxtLink></template>
            <template #result-cell="{ row }"><UBadge :color="row.original.result === 'normal' ? 'success' : 'warning'" variant="subtle">{{ row.original.result === 'normal' ? '正常' : `发现 ${row.original.issueCount} 项问题` }}</UBadge></template>
            <template #noticeNumber-cell="{ row }"><span class="text-xs text-muted">{{ row.original.noticeNumber || '未填写' }}</span></template>
            <template #actions-cell="{ row }"><UButton :to="detailLink(row.original)" label="查看记录" trailing-icon="i-lucide-chevron-right" color="neutral" variant="ghost" /></template>
          </UTable>
        </UCard>
        <div class="mobile-records"><NuxtLink v-for="record in result.items" :key="record.id" :to="detailLink(record)" class="record-card"><div class="flex items-start justify-between gap-3"><div class="min-w-0"><h2 class="text-base font-medium text-highlighted">{{ record.pointName }}</h2><p class="mt-1.5 text-xs text-muted">{{ record.communityName }}</p></div><UBadge :color="record.result === 'normal' ? 'success' : 'warning'" variant="subtle">{{ record.result === 'normal' ? '正常' : `${record.issueCount} 项问题` }}</UBadge></div><div class="mt-4 flex items-center justify-between border-t border-default pt-3"><span class="text-xs text-muted">{{ record.date }} · {{ record.inspector }}</span><UIcon name="i-lucide-chevron-right" class="size-4 text-dimmed" /></div></NuxtLink></div>
        <div v-if="result.total > 30" class="flex justify-center"><UPagination :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" /></div>
      </template>
      <UCard v-else><EmptyState title="还没有符合条件的排查记录" description="调整日期或社区筛选，也可以现在开始一次排查。"><UButton to="/inspections/new" icon="i-lucide-plus" label="新建排查" /></EmptyState></UCard>
    </PageState>
  </div>
</template>
