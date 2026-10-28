<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { PageDto, PointDto } from '~/types/api'
import { recordShape, mapColors } from '~/utils/map'
import { errorMessage, frequencyLabel, queryText } from '~/utils/format'
useHead({ title: '点位管理 · 街道生态环保' })
const route = useRoute(), toast = useToast()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage, set, tagIds } = useQueryFilters()
const view = field('view', 'list')
const q = field('q'), communityId = field('communityId', 'all'), typeId = field('typeId', 'all'), disabled = field('disabled', 'false'), temporary = field('temporary', 'false')
const result = ref<PageDto<PointDto> | null>(null), loading = ref(true), error = ref(''), editorOpen = ref(false), exportOpen = ref(false)
let requestVersion = 0
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const types = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const columns: TableColumn<PointDto>[] = [{ accessorKey: 'coverPhotoId', header: '' }, { accessorKey: 'name', header: '点位名称' }, { accessorKey: 'communityName', header: '社区' }, { accessorKey: 'typeName', header: '类型' }, { accessorKey: 'frequency', header: '排查频次' }, { accessorKey: 'lastInspectionDate', header: '最近排查' }, { accessorKey: 'openIssueCount', header: '未闭环' }, { id: 'actions', header: '操作' }]
const mapRecords = computed(() => (result.value?.items ?? []).map(point => ({ id: point.id, title: point.name, subtitle: `${point.communityName} · ${point.typeName}\n${point.address}`, shape: recordShape(point), color: point.disabled ? mapColors.disabled : point.openIssueCount ? mapColors.danger : mapColors.normal, href: `/points/${point.id}?returnTo=${encodeURIComponent(route.fullPath)}` })))
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/points') return
  const version = ++requestVersion; loading.value = true; error.value = ''
  try { await loadAppData(); const data = await $fetch<PageDto<PointDto>>('/api/points', { query: { q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value, typeId: typeId.value === 'all' ? undefined : typeId.value, disabled: disabled.value, temporary: temporary.value, missed: queryText(route.query.missed) === 'true' || undefined, tagId: tagIds.value, page: page.value, pageSize: 30, map: view.value === 'map' || undefined } }); if (version === requestVersion) result.value = data }
  catch (value) { if (version === requestVersion) error.value = errorMessage(value) }
  finally { if (version === requestVersion) loading.value = false }
}
function detailLink(point: PointDto) { return { path: `/points/${point.id}`, query: { returnTo: route.fullPath } } }
function saved() { toast.add({ title: '已保存', color: 'success', duration: 2000 }); void load() }
watch(() => route.fullPath, load, { immediate: true })
</script>
<template>
  <div class="page-stack" :class="{ 'map-page': view === 'map' }"><PageHeader title="点位管理"><template #actions><UButton label="导出台账" color="neutral" variant="outline" class="desktop-only" data-testid="point-ledger-desktop" @click="exportOpen = true" /><UButton icon="i-lucide-plus" class="header-action" data-testid="point-create" @click="editorOpen = true"><span class="mobile-only">新增</span><span class="desktop-only">新增点位</span></UButton></template></PageHeader>
    <div class="filter-box compact" data-testid="list-filters"><div class="filter-search"><UInput v-model="q" icon="i-lucide-search" placeholder="搜索点位、地址、联系人" type="search" aria-label="搜索点位" class="w-full" /></div><div class="filter-field"><label>社区</label><USelect v-model="communityId" :items="communities" aria-label="社区筛选" class="w-full" /></div><div class="filter-field"><label>类型</label><USelect v-model="typeId" :items="types" aria-label="类型筛选" class="w-full" /></div><div class="filter-field"><label>点位性质</label><USelect v-model="temporary" :items="[{ label: '常态', value: 'false' }, { label: '临时', value: 'true' }, { label: '全部', value: 'all' }]" aria-label="点位性质" class="w-full" /></div><div class="filter-field"><label>状态</label><USelect v-model="disabled" :items="[{ label: '启用中', value: 'false' }, { label: '已停用', value: 'true' }, { label: '全部', value: 'all' }]" aria-label="状态筛选" class="w-full" /></div></div>
    <div class="max-w-md"><TagPicker v-model="tagIds" mode="filter" placeholder="按标签筛选" /></div>
    <div class="filter-tool-row"><MapViewToggle v-model="view" /><UButton label="导出台账" color="neutral" variant="link" class="mobile-only" data-testid="point-ledger-mobile" @click="exportOpen = true" /><div class="flex items-center gap-2"><span>共 {{ result?.total ?? '—' }} 条</span><UButton v-if="queryText(route.query.missed) === 'true'" label="超周期未排查" trailing-icon="i-lucide-x" variant="link" @click="set('missed', '')" /></div><UButton v-if="Object.keys(route.query).length" :to="{ path: '/points', query: view === 'map' ? { view: 'map' } : {} }" label="清空" color="neutral" variant="link" /></div>
    <PageState :loading="loading" :error="error" @retry="load"><RecordMap v-if="view === 'map'" :records="mapRecords" /><template v-else><template v-if="result?.items.length">
      <div class="panel table-wrap"><UTable :data="result.items" :columns="columns" @select="(_event, row) => navigateTo(detailLink(row.original))"><template #coverPhotoId-cell="{ row }"><img v-if="row.original.coverPhotoId" :src="`/photos/${row.original.coverPhotoId}?size=thumb`" class="point-cover" alt="现场图片" loading="lazy"><span v-else class="point-cover point-placeholder"><UIcon name="i-lucide-image" class="size-5" /></span></template><template #name-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="block max-w-56"><p class="truncate text-primary">{{ row.original.name }}</p><p class="truncate text-xs text-muted">{{ row.original.address }}</p></NuxtLink></template><template #frequency-cell="{ row }">{{ frequencyLabel(row.original.frequency) }}</template><template #lastInspectionDate-cell="{ row }"><span class="text-xs">{{ row.original.lastInspectionDate || '—' }}</span></template><template #openIssueCount-cell="{ row }"><span class="block text-right">{{ row.original.openIssueCount }}</span></template><template #actions-cell="{ row }"><MapLinks :name="row.original.name" :longitude="row.original.longitude" :latitude="row.original.latitude" /></template></UTable></div>
      <div class="group-list mobile-records"><NuxtLink v-for="point in result.items" :key="point.id" :to="detailLink(point)" class="list-row"><img v-if="point.coverPhotoId" :src="`/photos/${point.coverPhotoId}?size=thumb`" class="point-cover" alt="现场图片" loading="lazy"><span v-else class="point-cover point-placeholder"><UIcon name="i-lucide-image" class="size-6" /></span><div class="row-body"><div class="row-title"><span>{{ point.name }}</span><StatusBadge v-if="point.disabled" tone="neutral">已停用</StatusBadge><StatusBadge v-else-if="point.openIssueCount" tone="warning">未闭环 {{ point.openIssueCount }}</StatusBadge><StatusBadge v-else-if="point.temporary" tone="outline">临时</StatusBadge></div><p class="row-secondary">{{ point.communityName }} · {{ point.typeName }} · {{ frequencyLabel(point.frequency) }}</p><p class="row-secondary">最近排查 {{ point.lastInspectionDate || '—' }}</p></div></NuxtLink></div>
      <div v-if="result.total > 30" class="flex justify-center"><UPagination :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" /></div>
    </template><div v-else class="panel"><EmptyState title="暂无点位" icon="i-lucide-map-pin"><UButton label="新增点位" @click="editorOpen = true" /></EmptyState></div></template></PageState>
    <LedgerExport v-model:open="exportOpen" kind="point" />
    <PointEditor v-model:open="editorOpen" @saved="saved" />
  </div>
</template>
