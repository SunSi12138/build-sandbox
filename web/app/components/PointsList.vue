<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { PageDto, PointDto } from '~/types/api'
import { errorMessage, frequencyLabel, queryText } from '~/utils/format'
const route = useRoute(), toast = useToast()
defineProps<{ embedded?: boolean }>()
const emit = defineEmits<{ viewMap: [] }>()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage, set } = useQueryFilters()
const q = field('q'), communityId = field('communityId', 'all'), typeId = field('typeId', 'all'), disabled = field('disabled', 'false'), temporary = field('temporary', 'false')
const result = ref<PageDto<PointDto> | null>(null), loading = ref(true), error = ref(''), editorOpen = ref(false), exportOpen = ref(false)
let requestVersion = 0
const communities = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const types = computed(() => [{ label: '全部', value: 'all' }, ...(bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id }))])
const columns: TableColumn<PointDto>[] = [{ accessorKey: 'coverPhotoId', header: '' }, { accessorKey: 'name', header: '场所名称' }, { accessorKey: 'communityName', header: '网格' }, { accessorKey: 'typeName', header: '类型' }, { accessorKey: 'frequency', header: '检查频次' }, { accessorKey: 'lastInspectionDate', header: '最近检查' }, { accessorKey: 'openIssueCount', header: '未完成' }, { id: 'actions', header: '操作' }]
async function load() {
  const version = ++requestVersion; loading.value = true; error.value = ''
  try { await loadAppData(); const data = await $fetch<PageDto<PointDto>>('/api/points', { query: { q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value, typeId: typeId.value === 'all' ? undefined : typeId.value, disabled: disabled.value, temporary: temporary.value, missed: queryText(route.query.missed) === 'true' || undefined, page: page.value, pageSize: 30 } }); if (version === requestVersion) result.value = data }
  catch (value) { if (version === requestVersion) error.value = errorMessage(value) }
  finally { if (version === requestVersion) loading.value = false }
}
function detailLink(point: PointDto) { return { path: `/points/${point.id}`, query: { returnTo: route.fullPath } } }
function saved() { toast.add({ title: '已保存', color: 'success', duration: 2000 }); void load() }
watch(() => route.fullPath, load, { immediate: true })
</script>
<template>
  <div class="page-stack"><PageHeader title="场所档案"><template #actions><UButton to="/reports?kind=places" label="导出场所台账" variant="outline" /><UButton label="导出检查台账" color="neutral" variant="outline" class="desktop-only" data-testid="point-ledger-desktop" @click="exportOpen = true" /><UButton icon="i-lucide-plus" class="header-action" data-testid="point-create" @click="editorOpen = true"><span class="mobile-only">新增</span><span class="desktop-only">新增场所</span></UButton></template></PageHeader>
    <div class="filter-box compact" data-testid="list-filters"><div class="filter-search"><UButton icon="i-lucide-map" aria-label="地图模式" title="地图模式" color="neutral" variant="outline" @click="emit('viewMap')" /><UInput v-model="q" icon="i-lucide-search" placeholder="搜索场所、地址、联系人" type="search" aria-label="搜索场所" class="w-full" /></div><div class="filter-field"><label>网格</label><USelect v-model="communityId" :items="communities" aria-label="网格筛选" class="w-full" /></div><div class="filter-field"><label>类型</label><USelect v-model="typeId" :items="types" aria-label="类型筛选" class="w-full" /></div><div class="filter-field"><label>场所性质</label><USelect v-model="temporary" :items="[{ label: '已建档', value: 'false' }, { label: '临时地点', value: 'true' }, { label: '全部', value: 'all' }]" aria-label="场所性质" class="w-full" /></div><div class="filter-field"><label>状态</label><USelect v-model="disabled" :items="[{ label: '启用中', value: 'false' }, { label: '已停用', value: 'true' }, { label: '全部', value: 'all' }]" aria-label="状态筛选" class="w-full" /></div></div>
    <div class="filter-tool-row"><UButton label="导出检查台账" color="neutral" variant="link" class="mobile-only" data-testid="point-ledger-mobile" @click="exportOpen = true" /><div class="flex items-center gap-2"><span>共 {{ result?.total ?? '—' }} 条</span><UButton v-if="queryText(route.query.missed) === 'true'" label="超周期未检查" trailing-icon="i-lucide-x" variant="link" @click="set('missed', '')" /></div><UButton v-if="Object.keys(route.query).length" to="/map?view=list" label="清空" color="neutral" variant="link" /></div>
    <PageState :loading="loading" :error="error" @retry="load"><template v-if="result?.items.length">
      <div class="panel table-wrap"><UTable :data="result.items" :columns="columns" @select="(_event, row) => navigateTo(detailLink(row.original))"><template #coverPhotoId-cell="{ row }"><img v-if="row.original.coverPhotoId" :src="`/photos/${row.original.coverPhotoId}?size=thumb`" class="point-cover" alt="现场图片" loading="lazy"><span v-else class="point-cover point-placeholder"><UIcon name="i-lucide-image" class="size-5" /></span></template><template #name-cell="{ row }"><NuxtLink :to="detailLink(row.original)" class="block max-w-56"><p class="truncate text-primary">{{ row.original.name }}</p><p class="truncate text-xs text-muted">{{ row.original.address }}</p></NuxtLink></template><template #frequency-cell="{ row }">{{ frequencyLabel(row.original.frequency) }}</template><template #lastInspectionDate-cell="{ row }"><span class="text-xs">{{ row.original.lastInspectionDate || '—' }}</span></template><template #openIssueCount-cell="{ row }"><span class="block text-right">{{ row.original.openIssueCount }}</span></template><template #actions-cell="{ row }"><MapLinks :point-id="row.original.id" :longitude="row.original.longitude" :latitude="row.original.latitude" /></template></UTable></div>
      <div class="group-list mobile-records"><NuxtLink v-for="point in result.items" :key="point.id" :to="detailLink(point)" class="list-row"><img v-if="point.coverPhotoId" :src="`/photos/${point.coverPhotoId}?size=thumb`" class="point-cover" alt="现场图片" loading="lazy"><span v-else class="point-cover point-placeholder"><UIcon name="i-lucide-image" class="size-6" /></span><div class="row-body"><div class="row-title"><span>{{ point.name }}</span><StatusBadge v-if="point.disabled" tone="neutral">已停用</StatusBadge><StatusBadge v-else-if="point.openIssueCount" tone="warning">未完成 {{ point.openIssueCount }}</StatusBadge><StatusBadge v-else-if="point.temporary" tone="outline">临时</StatusBadge></div><p class="row-secondary">{{ point.communityName }} · {{ point.typeName }} · {{ frequencyLabel(point.frequency) }}</p><p class="row-secondary">最近检查 {{ point.lastInspectionDate || '—' }}</p></div></NuxtLink></div>
      <div v-if="result.total > 30" class="flex justify-center"><UPagination :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" /></div>
    </template><div v-else class="panel"><EmptyState title="暂无场所" icon="i-lucide-map-pin"><UButton label="新增场所" @click="editorOpen = true" /></EmptyState></div></PageState>
    <LedgerExport v-model:open="exportOpen" kind="point" />
    <PointEditor v-model:open="editorOpen" @saved="saved" />
  </div>
</template>
