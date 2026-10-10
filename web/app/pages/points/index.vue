<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { PageDto, PointDto } from '~/types/api'
import { errorMessage, frequencyLabel, queryText } from '~/utils/format'

useHead({ title: '点位管理 · 街道生态环保' })
const route = useRoute()
const toast = useToast()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage, set } = useQueryFilters()
const q = field('q'), communityId = field('communityId', 'all'), typeId = field('typeId', 'all'), disabled = field('disabled', 'false')
const result = ref<PageDto<PointDto> | null>(null)
const loading = ref(true), error = ref(''), editorOpen = ref(false)
let requestVersion = 0
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const types = computed(() => [{ label: '全部类型', value: 'all' }, ...(bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const columns: TableColumn<PointDto>[] = [
  { accessorKey: 'name', header: '点位名称 / 地址' }, { accessorKey: 'communityName', header: '所属社区' },
  { accessorKey: 'typeName', header: '点位类型' }, { accessorKey: 'frequency', header: '排查频次' },
  { accessorKey: 'lastInspectionDate', header: '最近排查' }, { accessorKey: 'openIssueCount', header: '未闭环问题' }, { id: 'actions', header: '' }
]
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/points') return
  const version = ++requestVersion
  loading.value = true; error.value = ''
  try {
    await loadAppData()
    const data = await $fetch<PageDto<PointDto>>('/api/points', { query: {
      q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value,
      typeId: typeId.value === 'all' ? undefined : typeId.value, disabled: disabled.value,
      missed: queryText(route.query.missed) === 'true' || undefined, page: page.value, pageSize: 30
    } })
    if (version === requestVersion) result.value = data
  } catch (value) { if (version === requestVersion) error.value = errorMessage(value) }
  finally { if (version === requestVersion) loading.value = false }
}
watch(() => route.fullPath, load, { immediate: true })
function detailLink(point: PointDto) { return { path: `/points/${point.id}`, query: { returnTo: route.fullPath } } }
function saved() { toast.add({ title: '点位已保存', color: 'success' }); void load() }
</script>

<template>
  <div class="page-stack">
    <PageHeader title="点位管理" description="维护辖区排查点位，随时查看历史与待处理问题。">
      <template #actions><UButton label="新增点位" icon="i-lucide-plus" data-testid="point-create" @click="editorOpen = true" /></template>
    </PageHeader>
    <UCard>
      <div class="filter-bar">
        <UFormField label="搜索点位"><UInput v-model="q" icon="i-lucide-search" placeholder="点位名称、地址或联系人" class="w-full" type="search" /></UFormField>
        <UFormField label="所属社区"><USelect v-model="communityId" :items="communities" class="w-full" /></UFormField>
        <UFormField label="点位类型"><USelect v-model="typeId" :items="types" class="w-full" /></UFormField>
      </div>
      <div class="mt-4 flex flex-wrap items-center justify-between gap-3 border-t border-default pt-4">
        <div class="flex flex-wrap items-center gap-3"><p class="text-xs text-muted">共 <span class="font-semibold text-default">{{ result?.total ?? '—' }}</span> 个点位</p><UButton v-if="queryText(route.query.missed) === 'true'" label="超周期未排查" trailing-icon="i-lucide-x" color="warning" variant="soft" size="xs" @click="set('missed', '')" /></div>
        <USelect v-model="disabled" :items="[{ label: '启用中的点位', value: 'false' }, { label: '已停用的点位', value: 'true' }, { label: '全部状态', value: 'all' }]" aria-label="点位状态筛选" class="w-44" />
      </div>
    </UCard>
    <PageState :loading="loading" :error="error" @retry="load">
      <template v-if="result?.items.length">
        <UCard class="table-wrap" :ui="{ body: 'p-0 sm:p-0' }">
          <UTable :data="result.items" :columns="columns">
            <template #name-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="block max-w-64"><p class="font-medium text-highlighted hover:text-primary">{{ row.original.name }} <UBadge v-if="row.original.disabled" color="neutral" size="sm" variant="soft">停用</UBadge></p><p class="mt-1.5 truncate text-xs text-muted">{{ row.original.address }}</p></NuxtLink></template>
            <template #frequency-cell="{ row }"><span class="text-sm">{{ frequencyLabel(row.original.frequency) }}</span></template>
            <template #lastInspectionDate-cell="{ row }"><span class="text-xs tabular-nums text-muted">{{ row.original.lastInspectionDate || '暂无记录' }}</span></template>
            <template #openIssueCount-cell="{ row }"><UBadge v-if="row.original.openIssueCount" color="warning" variant="subtle">{{ row.original.openIssueCount }} 项</UBadge><span v-else class="text-xs text-dimmed">无</span></template>
            <template #actions-cell="{ row }"><UButton :to="detailLink(row.original)" label="查看" trailing-icon="i-lucide-chevron-right" color="neutral" variant="ghost" /></template>
          </UTable>
        </UCard>
        <div class="mobile-records">
          <NuxtLink v-for="point in result.items" :key="point.id" :to="detailLink(point)" class="record-card">
            <div class="flex items-start justify-between gap-3"><h2 class="text-base font-medium text-highlighted">{{ point.name }}</h2><UIcon name="i-lucide-chevron-right" class="mt-1 size-4 shrink-0 text-dimmed" /></div>
            <p class="mt-2 text-xs text-muted">{{ point.communityName }} · {{ point.typeName }}</p><p class="mt-3 text-sm leading-6 text-muted">{{ point.address }}</p>
            <div class="mt-4 flex flex-wrap items-center justify-between gap-2 border-t border-default pt-3"><span class="text-xs text-muted">{{ frequencyLabel(point.frequency) }} · {{ point.lastInspectionDate ? `最近 ${point.lastInspectionDate}` : '暂无排查' }}</span><UBadge v-if="point.disabled" color="neutral" variant="soft">已停用</UBadge><UBadge v-else-if="point.openIssueCount" color="warning" variant="subtle">{{ point.openIssueCount }} 项未闭环</UBadge><UBadge v-else color="success" variant="subtle">无待办问题</UBadge></div>
          </NuxtLink>
        </div>
        <div v-if="result.total > 30" class="flex justify-center"><UPagination :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" /></div>
      </template>
      <UCard v-else><EmptyState title="没有符合条件的点位" description="可以调整筛选条件，或新增一个需要排查的点位。" icon="i-lucide-map-pin"><UButton label="新增点位" icon="i-lucide-plus" @click="editorOpen = true" /></EmptyState></UCard>
    </PageState>
    <PointEditor v-model:open="editorOpen" @saved="saved" />
  </div>
</template>
