<script setup lang="ts">
import type { MatterDto, PageDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
import { matterSources, matterStatuses, matterSourceLabel, matterStatusLabel } from '~/utils/matters'
useHead({ title: '事项办理 · 街道生态环保' })
const route = useRoute(), { field, page, setPage, tagIds, set } = useQueryFilters()
const q = field('q'), status = field('status', 'all'), source = field('source', 'all'), from = field('from'), to = field('to')
const result = ref<PageDto<MatterDto> | null>(null), loading = ref(true), error = ref(''), filtersOpen = ref(false)
const overdue = computed(() => queryText(route.query.overdue) === 'true')
let version = 0, controller: AbortController | undefined
async function refresh() {
  if (route.path.replace(/\/$/, '') !== '/matters') return
  const current = ++version; controller?.abort(); controller = new AbortController(); loading.value = true; error.value = ''
  try {
    const value = await $fetch<PageDto<MatterDto>>('/api/matters', { signal: controller.signal, query: { q: q.value || undefined, status: status.value === 'all' ? undefined : status.value, source: source.value === 'all' ? undefined : source.value, from: from.value || undefined, to: to.value || undefined, overdue: overdue.value || undefined, pointId: queryText(route.query.pointId) || undefined, tagId: tagIds.value, page: page.value, pageSize: 30 } })
    if (current === version) result.value = value
  } catch (cause) { if (current === version && !controller.signal.aborted) error.value = errorMessage(cause) } finally { if (current === version) loading.value = false }
}
watch(() => route.fullPath, refresh, { immediate: true }); onBeforeUnmount(() => { ++version; controller?.abort() })
</script>
<template><div class="page-stack"><PageHeader title="事项办理"><template #actions><UButton to="/matters/new" label="登记事项" icon="i-lucide-plus" class="header-action" /></template></PageHeader>
<div class="filter-box compact"><div class="filter-search"><UInput v-model="q" type="search" placeholder="搜索标题、内容、编号、经办人" aria-label="搜索事项" icon="i-lucide-search" class="w-full" /><UButton label="筛选" color="neutral" variant="outline" :aria-expanded="filtersOpen" @click="filtersOpen = !filtersOpen" /></div>
<div class="filter-field"><label>状态</label><USelect v-model="status" :items="[{ label: '全部状态', value: 'all' }, ...matterStatuses]" aria-label="事项状态筛选" class="w-full" /></div><div class="filter-field"><label>来源</label><USelect v-model="source" :items="[{ label: '全部来源', value: 'all' }, ...matterSources]" aria-label="事项来源筛选" class="w-full" /></div>
<div v-show="filtersOpen" class="field-full space-y-2"><TagPicker v-model="tagIds" mode="filter" placeholder="按标签筛选" /><div class="field-grid"><UFormField label="接收开始日期"><UInput v-model="from" type="date" class="w-full" /></UFormField><UFormField label="接收结束日期"><UInput v-model="to" type="date" class="w-full" /></UFormField></div><UCheckbox :model-value="overdue" label="仅看逾期未办结" @update:model-value="value => set('overdue', value ? 'true' : '')" /></div></div>
<div class="filter-tool-row"><span>共 {{ result?.total ?? '—' }} 件事项</span><UButton v-if="Object.keys(route.query).length" to="/matters" label="清空筛选" color="neutral" variant="link" /></div>
<PageState :loading="loading" :error="error" @retry="refresh"><section v-if="result?.items.length" class="group-list"><NuxtLink v-for="matter in result.items" :key="matter.id" :to="`/matters/${matter.id}`" class="list-row" data-testid="matter-row"><div class="row-body"><p class="row-title"><span>{{ matter.title }}</span><StatusBadge :tone="matter.status === 'closed' ? 'success' : matter.overdue ? 'danger' : 'warning'">{{ matterStatusLabel(matter.status) }}</StatusBadge></p><p class="row-secondary">{{ matterSourceLabel(matter.source) }} · {{ matter.receivedDate }} · {{ matter.handler || '未指定经办人' }}</p><p class="row-footer"><span>{{ matter.pointName || matter.location || '未关联点位' }}</span><span v-if="matter.deadline" :class="matter.overdue ? 'text-error' : ''">{{ matter.deadline }} {{ matter.overdue ? '已逾期' : '到期' }}</span></p></div></NuxtLink></section><EmptyState v-else title="暂无事项"><UButton to="/matters/new" label="登记事项" /></EmptyState></PageState>
<UPagination v-if="result && result.total > 30" :page="page" :total="result.total" :items-per-page="30" :sibling-count="0" @update:page="setPage" />
</div></template>
