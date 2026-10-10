<script setup lang="ts">
import type { TableColumn } from '@nuxt/ui'
import type { IssueDto, PageDto } from '~/types/api'
import { errorMessage, queryText, statusColor, statusLabel } from '~/utils/format'

useHead({ title: '整改跟踪' })
const route = useRoute()
const router = useRouter()
const { bootstrap, load } = useAppData()
const records = ref<PageDto<IssueDto> | null>(null)
const loading = ref(true)
const error = ref('')
const filters = reactive({ q: '', status: 'all', communityId: 'all', overdue: false, dueSoon: false, from: '', to: '' })
const page = computed(() => Math.max(1, Number.parseInt(queryText(route.query.page), 10) || 1))
const communityItems = computed(() => [
  { label: '全部社区', value: 'all' },
  ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))
])
const statusItems = [
  { label: '全部状态', value: 'all' }, { label: '待整改', value: 'pending' },
  { label: '社区已反馈', value: 'feedback' }, { label: '已复查闭环', value: 'closed' }
]
const columns: TableColumn<IssueDto>[] = [
  { accessorKey: 'pointName', header: '点位 / 社区' },
  { accessorKey: 'description', header: '问题情况' },
  { accessorKey: 'deadline', header: '整改期限' },
  { accessorKey: 'status', header: '办理状态' },
  { id: 'actions', header: '操作' }
]
const hasFilters = computed(() => Object.entries(route.query).some(([key, value]) => key !== 'page' && Boolean(value)))
const filterError = computed(() => filters.from && filters.to && filters.from > filters.to ? '开始日期不能晚于结束日期。' : '')
let requestVersion = 0

function syncFilters() {
  filters.q = queryText(route.query.q)
  filters.status = queryText(route.query.status) || 'all'
  filters.communityId = queryText(route.query.communityId) || 'all'
  filters.overdue = ['true', '1'].includes(queryText(route.query.overdue))
  filters.dueSoon = ['true', '1'].includes(queryText(route.query.dueSoon))
  filters.from = queryText(route.query.from)
  filters.to = queryText(route.query.to)
}

async function fetchRecords() {
  const version = ++requestVersion
  loading.value = true
  error.value = ''
  try {
    const query: Record<string, string | number> = { page: page.value, pageSize: 20 }
    for (const key of ['q', 'status', 'communityId', 'overdue', 'dueSoon', 'from', 'to', 'pointId', 'inspectionId']) {
      const value = queryText(route.query[key])
      if (value && value !== 'all') query[key] = value
    }
    const [result] = await Promise.all([$fetch<PageDto<IssueDto>>('/api/issues', { query }), load()])
    if (version === requestVersion) records.value = result
  } catch (cause) {
    if (version === requestVersion) error.value = errorMessage(cause)
  } finally {
    if (version === requestVersion) loading.value = false
  }
}

async function applyFilters() {
  if (filterError.value) return
  const query: Record<string, string> = {}
  if (filters.q.trim()) query.q = filters.q.trim()
  if (filters.status !== 'all') query.status = filters.status
  if (filters.communityId !== 'all') query.communityId = filters.communityId
  if (filters.overdue) query.overdue = 'true'
  if (filters.dueSoon && !filters.overdue) query.dueSoon = 'true'
  if (filters.from) query.from = filters.from
  if (filters.to) query.to = filters.to
  for (const key of ['pointId', 'inspectionId']) {
    const value = queryText(route.query[key])
    if (value) query[key] = value
  }
  await router.push({ path: '/issues', query })
}

async function changePage(value: number) {
  await router.push({ query: { ...route.query, page: String(value) } })
  window.scrollTo({ top: 0, behavior: 'smooth' })
}

function issueLink(id: string) {
  return { path: `/issues/${id}`, query: { returnTo: route.fullPath } }
}

watch(() => route.fullPath, () => { syncFilters(); void fetchRecords() }, { immediate: true })
onBeforeUnmount(() => { requestVersion++ })
</script>

<template>
  <div class="page-stack">
    <PageHeader title="整改跟踪" eyebrow="排查后的每一步，都有记录" description="跟进社区反馈、现场复查，让问题按期闭环。">
      <template #actions>
        <UButton to="/inspections/new" icon="i-lucide-plus" size="xl" class="min-h-12">新建排查</UButton>
      </template>
    </PageHeader>

    <UCard :ui="{ body: 'p-4 sm:p-5' }">
      <UForm :state="filters" class="space-y-4" @submit="applyFilters">
        <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
          <UFormField label="搜索" name="q">
            <UInput v-model="filters.q" icon="i-lucide-search" placeholder="点位、问题或责任单位" :maxlength="200" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" />
          </UFormField>
          <UFormField label="办理状态" name="status">
            <USelect v-model="filters.status" :items="statusItems" size="xl" class="min-h-12 w-full" />
          </UFormField>
          <UFormField label="所属社区" name="communityId">
            <USelect v-model="filters.communityId" :items="communityItems" size="xl" class="min-h-12 w-full" />
          </UFormField>
          <div class="grid grid-cols-1 gap-4 md:col-span-2 md:grid-cols-2">
            <UFormField label="排查开始日期" name="from">
              <UInput v-model="filters.from" type="date" :max="filters.to || undefined" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" />
            </UFormField>
            <UFormField label="排查结束日期" name="to">
              <UInput v-model="filters.to" type="date" :min="filters.from || undefined" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" />
            </UFormField>
          </div>
        </div>
        <UAlert v-if="filterError" color="error" variant="subtle" :description="filterError" />
        <div class="flex flex-wrap items-center justify-between gap-3 border-t border-default pt-4">
          <div class="flex min-h-12 flex-wrap items-center gap-x-5 gap-y-2">
            <UCheckbox v-model="filters.overdue" label="只看逾期未闭环" size="lg" @update:model-value="filters.dueSoon = false" />
            <UCheckbox v-model="filters.dueSoon" label="3 天内到期" size="lg" @update:model-value="filters.overdue = false" />
          </div>
          <div class="flex w-full gap-2 sm:w-auto">
            <UButton v-if="hasFilters" color="neutral" variant="ghost" size="xl" class="min-h-12" to="/issues">清空筛选</UButton>
            <UButton type="submit" icon="i-lucide-list-filter" size="xl" class="min-h-12 flex-1 justify-center sm:flex-none" :disabled="Boolean(filterError)">查询</UButton>
          </div>
        </div>
      </UForm>
    </UCard>

    <div class="flex items-center justify-between gap-3">
      <div class="flex items-baseline gap-2">
        <h2 class="text-lg font-semibold text-highlighted">问题清单</h2>
        <span v-if="records" class="text-sm text-muted">共 {{ records.total }} 条</span>
      </div>
      <UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" size="lg" class="min-h-12" :loading="loading" @click="fetchRecords">刷新</UButton>
    </div>

    <UAlert v-if="error" color="error" variant="subtle" title="暂时未能载入问题" :description="error" icon="i-lucide-circle-alert">
      <template #actions><UButton color="error" variant="soft" class="min-h-12" @click="fetchRecords">重试</UButton></template>
    </UAlert>
    <div v-else-if="loading && !records" class="space-y-3" aria-label="正在载入整改记录" aria-busy="true">
      <USkeleton v-for="item in 3" :key="item" class="h-36 w-full rounded-xl" />
    </div>
    <UCard v-else-if="records?.items.length === 0" class="text-center">
      <div class="mx-auto flex max-w-md flex-col items-center gap-3 py-10">
        <span class="flex size-14 items-center justify-center rounded-full bg-primary/10"><UIcon name="i-lucide-clipboard-check" class="size-7 text-primary" /></span>
        <h3 class="text-lg font-semibold">{{ hasFilters ? '没有符合筛选条件的问题' : '暂时没有待跟踪的问题' }}</h3>
        <p class="text-sm leading-6 text-muted">{{ hasFilters ? '可以调整社区、状态或排查日期范围后再查看。' : '新建排查时登记发现的问题，就会出现在这里。正常排查也会完整留存。' }}</p>
        <UButton v-if="hasFilters" to="/issues" variant="outline" size="xl" class="min-h-12">查看全部问题</UButton>
        <UButton v-else to="/inspections/new" size="xl" class="min-h-12">去新建排查</UButton>
      </div>
    </UCard>
    <template v-else-if="records">
      <div class="space-y-3 md:hidden" :aria-busy="loading">
        <UCard v-for="issue in records.items" :key="issue.id" :ui="{ body: 'p-4' }">
          <div class="flex items-start justify-between gap-3">
            <div class="min-w-0">
              <ULink :to="issueLink(issue.id)" class="inline-flex min-h-12 items-center text-base font-semibold text-highlighted">{{ issue.pointName }}</ULink>
              <p class="text-sm text-muted">{{ issue.communityName }} · {{ issue.inspectionDate }} 排查</p>
            </div>
            <UBadge :color="statusColor(issue.status)" variant="subtle" class="mt-2 shrink-0">{{ statusLabel(issue.status) }}</UBadge>
          </div>
          <p class="my-4 line-clamp-3 whitespace-pre-wrap text-sm leading-6">{{ issue.description }}</p>
          <div class="flex items-center justify-between gap-3 border-t border-default pt-3">
            <div :class="issue.overdue ? 'text-error' : 'text-muted'" class="text-sm">
              <p v-if="issue.overdue" class="mb-1 font-semibold">已逾期</p>
              <p>{{ issue.deadline }} 前整改</p>
            </div>
            <UButton :to="issueLink(issue.id)" :variant="issue.status === 'closed' ? 'outline' : 'soft'" trailing-icon="i-lucide-arrow-right" size="xl" class="min-h-12">{{ issue.status === 'closed' ? '查看记录' : '去跟进' }}</UButton>
          </div>
        </UCard>
      </div>
      <UCard class="hidden overflow-hidden md:block" :ui="{ body: 'p-0 sm:p-0' }">
        <UTable :data="records.items" :columns="columns" :loading="loading" :ui="{ td: 'align-top py-4', th: 'bg-elevated/50 py-4' }">
          <template #pointName-cell="{ row }">
            <ULink :to="issueLink(row.original.id)" class="inline-flex min-h-12 items-center whitespace-normal font-semibold text-highlighted">{{ row.original.pointName }}</ULink>
            <p class="text-xs text-muted">{{ row.original.communityName }} · {{ row.original.inspectionDate }}</p>
          </template>
          <template #description-cell="{ row }">
            <p class="max-w-sm whitespace-pre-wrap text-sm leading-6">{{ row.original.description }}</p>
            <p class="mt-1 text-xs text-muted">责任单位：{{ row.original.responsibleUnit }}</p>
          </template>
          <template #deadline-cell="{ row }">
            <div class="pt-3" :class="row.original.overdue ? 'font-medium text-error' : 'text-default'">
              {{ row.original.deadline }}
              <p v-if="row.original.overdue" class="mt-1 text-xs">已逾期未闭环</p>
            </div>
          </template>
          <template #status-cell="{ row }"><UBadge :color="statusColor(row.original.status)" variant="subtle" class="mt-3">{{ statusLabel(row.original.status) }}</UBadge></template>
          <template #actions-cell="{ row }"><UButton :to="issueLink(row.original.id)" variant="ghost" trailing-icon="i-lucide-arrow-right" class="min-h-12">{{ row.original.status === 'closed' ? '查看' : '跟进' }}</UButton></template>
        </UTable>
      </UCard>
      <div v-if="records.total > records.pageSize" class="flex flex-col items-center justify-between gap-3 py-2 sm:flex-row">
        <p class="text-sm text-muted">第 {{ records.page }} 页，共 {{ Math.ceil(records.total / records.pageSize) }} 页</p>
        <UPagination :page="page" :items-per-page="records.pageSize" :total="records.total" :sibling-count="1" size="lg" :show-edges="false" @update:page="changePage" />
      </div>
    </template>
  </div>
</template>
