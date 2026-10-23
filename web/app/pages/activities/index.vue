<script setup lang="ts">
import type { ActivityDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
useHead({ title: '活动记录 · 街道生态环保' })
const route = useRoute()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage } = useQueryFilters()
const q = field('q')
const communityId = field('communityId', 'all')
const typeId = field('typeId', 'all')
const from = field('from')
const to = field('to')
const result = ref<PageDto<ActivityDto> | null>(null)
const loading = ref(true)
const error = ref('')
const datesOpen = ref(false)
const exportOpen = ref(false)
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(v => ({ label: v.name, value: v.id }))])
const types = computed(() => [{ label: '全部类型', value: 'all' }, ...(bootstrap.value?.activityTypes ?? []).map(v => ({ label: v.name, value: v.id }))])
let sequence = 0
async function refresh() {
  if (route.path.replace(/\/+$/, '') !== '/activities') return
  const current = ++sequence
  loading.value = true
  error.value = ''
  try {
    await loadAppData()
    const value = await $fetch<PageDto<ActivityDto>>('/api/activities', { query: {
      q: q.value || undefined, communityId: communityId.value === 'all' ? undefined : communityId.value,
      typeId: typeId.value === 'all' ? undefined : typeId.value,
      from: from.value || undefined, to: to.value || undefined, page: page.value, pageSize: 30
    } })
    if (current === sequence) result.value = value
  } catch (cause) { if (current === sequence) error.value = errorMessage(cause) }
  finally { if (current === sequence) loading.value = false }
}
function link(record: ActivityDto) { return { path: `/activities/${record.id}`, query: { returnTo: route.fullPath } } }
watch(() => route.fullPath, refresh, { immediate: true })
</script>
<template>
  <div class="page-stack">
    <PageHeader title="活动记录">
      <template #actions>
        <UButton label="导出台账" color="neutral" variant="outline" class="desktop-only" @click="exportOpen = true" />
        <UButton to="/activities/new" icon="i-lucide-plus" class="header-action" data-testid="activity-new"><span class="desktop-only">新增活动</span><span class="mobile-only">新增</span></UButton>
      </template>
    </PageHeader>
    <div class="filter-box compact" data-testid="list-filters">
      <div class="filter-search">
        <UInput v-model="q" type="search" placeholder="搜索名称、地点、内容" icon="i-lucide-search" aria-label="搜索活动" class="w-full" />
        <UButton color="neutral" variant="outline" :aria-expanded="datesOpen" class="filter-date-toggle" @click="datesOpen = !datesOpen">日期</UButton>
      </div>
      <div class="filter-field"><label>社区</label><USelect v-model="communityId" :items="communities" class="w-full" aria-label="社区筛选" /></div>
      <div class="filter-field"><label>类型</label><USelect v-model="typeId" :items="types" class="w-full" aria-label="类型筛选" /></div>
      <div class="filter-dates" :class="{ 'is-open': datesOpen }">
        <div class="filter-field"><label>开始日期</label><UInput v-model="from" type="date" aria-label="开始日期" class="w-full" /></div>
        <div class="filter-field"><label>结束日期</label><UInput v-model="to" type="date" aria-label="结束日期" class="w-full" /></div>
      </div>
    </div>
    <div class="mobile-only"><UButton label="导出活动台账" color="neutral" variant="outline" icon="i-lucide-file-down" @click="exportOpen = true" /></div>
    <UAlert v-if="error" :description="error" color="error" variant="subtle"><template #actions><UButton label="重试" color="neutral" variant="outline" @click="refresh" /></template></UAlert>
    <div v-if="loading && !result" class="space-y-2"><USkeleton v-for="index in 5" :key="index" class="h-16 w-full" /></div>
    <template v-if="result">
      <section v-if="result.items.length" class="panel">
        <ul class="group-list">
          <li v-for="record in result.items" :key="record.id">
            <NuxtLink :to="link(record)" class="list-row" data-testid="activity-row">
              <div class="row-body">
                <div class="row-title"><span class="font-semibold">{{ record.name }}</span></div>
                <p class="text-xs text-muted">{{ record.communityName || '跨社区' }} · {{ record.typeName }} · {{ record.location || '—' }}</p>
                <p v-if="record.participantCount !== null" class="text-xs text-muted">参加 {{ record.participantCount }} 人</p>
              </div>
              <div class="shrink-0 text-right text-xs text-muted">{{ record.date }}<UIcon name="i-lucide-chevron-right" class="ml-1" /></div>
            </NuxtLink>
          </li>
        </ul>
      </section>
      <EmptyState v-else-if="!loading" title="暂无活动记录"><UButton to="/activities/new">新增活动</UButton></EmptyState>
      <div v-if="result.total > 30" class="flex items-center justify-between gap-2"><UButton label="上一页" color="neutral" variant="outline" :disabled="page <= 1 || loading" @click="setPage(page - 1)" /><span class="text-xs text-muted">{{ page }} / {{ Math.ceil(result.total / 30) }}</span><UButton label="下一页" color="neutral" variant="outline" :disabled="page >= Math.ceil(result.total / 30) || loading" @click="setPage(page + 1)" /></div>
    </template>
    <ActivityLedgerExport v-model:open="exportOpen" :community-id="communityId === 'all' ? undefined : communityId" :type-id="typeId === 'all' ? undefined : typeId" />
  </div>
</template>
