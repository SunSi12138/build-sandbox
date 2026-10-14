<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { CampaignDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
useHead({ title: '整治记录 · 街道生态环保' })
const route = useRoute()
const { bootstrap, load: loadAppData } = useAppData()
const { field, page, setPage } = useQueryFilters()
const q = field('q'),
  pointId = field('pointId'),
  communityId = field('communityId', 'all'),
  typeId = field('typeId', 'all'),
  from = field('from'),
  to = field('to')
const result = ref<PageDto<CampaignDto> | null>(null),
  loading = ref(true),
  error = ref(''),
  datesOpen = ref(false),
  exportOpen = ref(false)
const communities = computed(() => [
  { label: '全部', value: 'all' },
  ...(bootstrap.value?.communities ?? []).map((item) => ({ label: item.name, value: item.id })),
])
const types = computed(() => [
  { label: '全部', value: 'all' },
  ...(bootstrap.value?.campaignTypes ?? []).map((item) => ({ label: item.name, value: item.id })),
])
const columns: TableColumn<CampaignDto>[] = [
  { accessorKey: 'date', header: '整治日期' },
  { accessorKey: 'name', header: '名称' },
  { accessorKey: 'typeName', header: '类型' },
  { accessorKey: 'communityName', header: '社区' },
  { accessorKey: 'location', header: '地点' },
  {
    accessorKey: 'participantCount',
    header: '参加人数',
    meta: { class: { th: 'text-right', td: 'text-right' } },
  },
  {
    accessorKey: 'garbageTons',
    header: '清运垃圾（吨）',
    meta: { class: { th: 'text-right', td: 'text-right' } },
  },
]
let version = 0
async function load() {
  if (route.path.replace(/\/+$/, '') !== '/campaigns') return
  const current = ++version
  loading.value = true
  error.value = ''
  try {
    await loadAppData()
    const data = await $fetch<PageDto<CampaignDto>>('/api/campaigns', {
      query: {
        pointId: pointId.value || undefined,
        q: q.value || undefined,
        communityId: communityId.value === 'all' ? undefined : communityId.value,
        typeId: typeId.value === 'all' ? undefined : typeId.value,
        from: from.value || undefined,
        to: to.value || undefined,
        page: page.value,
        pageSize: 30,
      },
    })
    if (current === version) result.value = data
  } catch (value) {
    if (current === version) error.value = errorMessage(value)
  } finally {
    if (current === version) loading.value = false
  }
}
function detailLink(record: CampaignDto) {
  return { path: `/campaigns/${record.id}`, query: { returnTo: route.fullPath } }
}
function quantities(record: CampaignDto) {
  return [
    record.participantCount !== null ? `${record.participantCount} 人` : '',
    record.garbageTons !== null ? `${record.garbageTons.toFixed(2)} 吨` : '',
  ]
    .filter(Boolean)
    .join(' · ')
}
function clearDates() {
  void navigateTo(
    {
      path: '/campaigns',
      query: { ...route.query, from: undefined, to: undefined, page: undefined },
    },
    { replace: true },
  )
}
watch(() => route.fullPath, load, { immediate: true })
</script>
<template>
  <div class="page-stack">
    <PageHeader title="整治记录"
      ><template #actions
        ><UButton
          label="导出台账"
          color="neutral"
          variant="outline"
          class="desktop-only"
          @click="exportOpen = true"
        /><UButton
          to="/campaigns/new"
          icon="i-lucide-plus"
          class="header-action"
          data-testid="campaign-new"
          ><span class="mobile-only">新增</span><span class="desktop-only">新增整治</span></UButton
        ></template
      ></PageHeader
    >
    <div class="filter-box compact" data-testid="list-filters">
      <div class="filter-search">
        <UInput
          v-model="q"
          type="search"
          icon="i-lucide-search"
          placeholder="搜索名称、地点"
          aria-label="搜索整治"
          class="w-full"
        /><UButton
          color="neutral"
          variant="outline"
          class="filter-date-toggle"
          :aria-expanded="datesOpen"
          @click="datesOpen = !datesOpen"
          >日期<span v-if="from || to" class="date-dot"
        /></UButton>
      </div>
      <div class="filter-field">
        <label>社区</label
        ><USelect v-model="communityId" :items="communities" aria-label="社区筛选" class="w-full" />
      </div>
      <div class="filter-field">
        <label>类型</label
        ><USelect v-model="typeId" :items="types" aria-label="类型筛选" class="w-full" />
      </div>
      <div class="filter-dates" :class="{ 'is-open': datesOpen }">
        <div class="filter-field">
          <label>开始日期</label
          ><UInput v-model="from" type="date" class="w-full" aria-label="开始日期" />
        </div>
        <div class="filter-field">
          <label>结束日期</label
          ><UInput v-model="to" type="date" class="w-full" aria-label="结束日期" />
        </div>
      </div>
    </div>
    <div v-if="pointId" class="chip-row"><UButton :label="result?.items[0]?.pointName || '所选点位'" trailing-icon="i-lucide-x" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="true" @click="pointId = ''" /></div>
    <div v-if="from || to" class="chip-row">
      <UButton
        :label="`${from || '不限'} 至 ${to || '不限'}`"
        trailing-icon="i-lucide-x"
        variant="ghost"
        color="neutral"
        class="filter-chip"
        :aria-pressed="true"
        @click="clearDates"
      />
    </div>
    <div class="filter-tool-row">
      <span>共 {{ result?.total ?? '—' }} 条</span>
      <div class="flex items-center gap-1">
        <UButton
          v-if="Object.keys(route.query).length"
          to="/campaigns"
          label="清空"
          color="neutral"
          variant="link"
        /><UButton
          label="导出台账"
          color="neutral"
          variant="link"
          class="mobile-only"
          @click="exportOpen = true"
        />
      </div>
    </div>
    <PageState :loading="loading" :error="error" @retry="load"
      ><template v-if="result?.items.length"
        ><div class="panel table-wrap">
          <UTable
            :data="result.items"
            :columns="columns"
            @select="(_event, row) => navigateTo(detailLink(row.original))"
            ><template #date-cell="{ row }"
              ><NuxtLink :to="detailLink(row.original)" class="text-primary">{{
                row.original.date
              }}</NuxtLink></template
            ><template #name-cell="{ row }"
              ><NuxtLink
                :to="detailLink(row.original)"
                class="block max-w-64 truncate font-medium"
                >{{ row.original.name }}</NuxtLink
              ></template
            ><template #location-cell="{ row }"
              ><span class="block max-w-64 truncate">{{ row.original.location }}</span></template
            ><template #participantCount-cell="{ row }">{{
              row.original.participantCount ?? '—'
            }}</template
            ><template #garbageTons-cell="{ row }">{{
              row.original.garbageTons?.toFixed(2) ?? '—'
            }}</template></UTable
          >
        </div>
        <div class="group-list mobile-records">
          <NuxtLink
            v-for="record in result.items"
            :key="record.id"
            :to="detailLink(record)"
            class="list-row"
            data-testid="campaign-row"
            ><div class="row-body">
              <div class="row-title">
                <span>{{ record.name }}</span
                ><span class="shrink-0 text-xs font-normal text-muted">{{ record.date }}</span>
              </div>
              <div class="row-secondary">{{ record.communityName }} · {{ record.typeName }}</div>
              <div class="row-footer">
                <span>{{ record.location }}</span
                ><span v-if="quantities(record)" class="shrink-0">{{ quantities(record) }}</span>
              </div>
            </div></NuxtLink
          >
        </div>
        <div v-if="result.total > 30" class="flex justify-center">
          <UPagination
            :page="page"
            :total="result.total"
            :items-per-page="30"
            :sibling-count="0"
            @update:page="setPage"
          /></div
      ></template>
      <div v-else class="panel">
        <EmptyState title="暂无整治记录"
          ><UButton to="/campaigns/new" label="新增整治"
        /></EmptyState></div
    ></PageState>
    <LedgerExport
      v-model:open="exportOpen"
      kind="campaign"
      :community-id="communityId === 'all' ? undefined : communityId"
      :type-id="typeId === 'all' ? undefined : typeId"
    />
  </div>
</template>
