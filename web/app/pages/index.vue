<script setup lang="ts">
import type { DashboardDto, InspectionDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

useHead({ title: '工作首页 · 街道生态环保' })
const dashboard = ref<DashboardDto | null>(null)
const recent = ref<InspectionDto[]>([])
const loading = ref(true)
const error = ref('')
async function load() {
  loading.value = true
  error.value = ''
  try {
    const [reminders, records] = await Promise.all([
      $fetch<DashboardDto>('/api/dashboard'),
      $fetch<PageDto<InspectionDto>>('/api/inspections', { query: { pageSize: 5 } })
    ])
    dashboard.value = reminders
    recent.value = records.items
  } catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
}
onMounted(load)
const cards = computed(() => dashboard.value ? [
  { title: '已逾期未闭环', count: dashboard.value.overdue.count, icon: 'i-lucide-circle-alert', detail: '需要优先督促整改', to: '/issues?overdue=true', tone: 'overdue' },
  { title: '3 天内到期', count: dashboard.value.dueSoon.count, icon: 'i-lucide-clock-3', detail: '含今天，请提前跟进', to: '/issues?dueSoon=true', tone: 'due' },
  { title: '待复查', count: dashboard.value.awaitingReview.count, icon: 'i-lucide-circle-check-big', detail: '社区已反馈，等待复查', to: '/issues?status=feedback', tone: 'review' },
  { title: '超周期未排查', count: dashboard.value.missedInspections.count, icon: 'i-lucide-calendar-days', detail: '本周 / 月 / 季尚无记录', to: '/points?missed=true', tone: 'missed' }
] : [])
const priority = computed(() => {
  if (!dashboard.value) return { title: '待处理问题', items: [], to: '/issues' }
  if (dashboard.value.overdue.count) return { title: '优先处理 · 逾期问题', items: dashboard.value.overdue.items.slice(0, 5), to: '/issues?overdue=true' }
  if (dashboard.value.dueSoon.count) return { title: '近期需要跟进', items: dashboard.value.dueSoon.items.slice(0, 5), to: '/issues?dueSoon=true' }
  return { title: '等待现场复查', items: dashboard.value.awaitingReview.items.slice(0, 5), to: '/issues?status=feedback' }
})
</script>

<template>
  <div class="page-stack">
    <PageHeader title="今天的环保工作" description="把每一次排查、每一项整改，认真记录下来。" eyebrow="工作首页">
      <template #actions><UButton to="/inspections/new" icon="i-lucide-plus" label="新建排查" data-testid="home-new-inspection" /></template>
    </PageHeader>
    <PageState :loading="loading" :error="error" @retry="load">
      <section class="reminder-grid" aria-label="工作提醒">
        <NuxtLink v-for="card in cards" :key="card.title" :to="card.to" class="reminder-card" :class="[card.tone, { urgent: card.tone === 'overdue' && card.count > 0 }]" :aria-label="`${card.title}，${card.count} 项，点击查看`">
          <div class="flex items-center justify-between gap-2"><p class="text-xs font-medium sm:text-sm">{{ card.title }}</p><UIcon :name="card.icon" class="size-5 shrink-0 opacity-70" /></div>
          <p class="my-4 flex items-end gap-2"><strong class="text-4xl font-semibold leading-none tracking-tight">{{ card.count }}</strong><span class="pb-0.5 text-xs opacity-60">{{ card.tone === 'missed' ? '处' : '项' }}</span></p>
          <p class="flex items-center justify-between text-[10px] opacity-70 sm:text-xs"><span>{{ card.detail }}</span><UIcon name="i-lucide-arrow-up-right" class="size-3.5" /></p>
        </NuxtLink>
      </section>

      <div class="home-columns">
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header><div class="flex items-center justify-between gap-3"><h2 class="section-title">{{ priority.title }}</h2><UButton :to="priority.to" label="查看全部" trailing-icon="i-lucide-chevron-right" color="neutral" variant="link" class="-my-2 -mr-2 text-xs" /></div></template>
          <div v-if="priority.items.length" class="divide-y divide-default">
            <NuxtLink v-for="issue in priority.items" :key="issue.id" :to="`/issues/${issue.id}`" class="priority-row">
              <span class="mt-1 flex size-9 shrink-0 items-center justify-center rounded-xl" :class="issue.overdue ? 'bg-error/10 text-error' : 'bg-warning/10 text-warning'"><UIcon :name="issue.overdue ? 'i-lucide-circle-alert' : 'i-lucide-clock-3'" class="size-4.5" /></span>
              <div class="min-w-0 flex-1"><div class="flex flex-wrap items-center gap-x-3 gap-y-1"><h3 class="truncate text-sm font-medium text-highlighted">{{ issue.pointName }}</h3><span class="text-xs text-muted">{{ issue.communityName }}</span></div><p class="mt-1.5 line-clamp-2 text-sm leading-6 text-muted">{{ issue.description }}</p><p class="mt-2 text-xs" :class="issue.overdue ? 'text-error' : 'text-muted'">整改期限 {{ issue.deadline }}<span v-if="issue.overdue" class="ml-2 font-medium">已逾期</span></p></div>
              <UIcon name="i-lucide-chevron-right" class="mt-3 size-4 shrink-0 text-dimmed" />
            </NuxtLink>
          </div>
          <EmptyState v-else title="当前没有紧急待办" description="继续做好日常排查，新的提醒会自动出现在这里。" icon="i-lucide-circle-check-big" />
        </UCard>

        <div class="space-y-5">
          <section class="field-entry">
            <span class="flex size-11 items-center justify-center rounded-2xl border border-white/20 bg-white/10"><UIcon name="i-lucide-camera" class="size-6" /></span>
            <h2 class="mt-5 text-lg font-semibold">现场发现，随手记录</h2>
            <p class="mt-2 text-sm leading-6 text-white/70">选点位、记问题、传照片。<br>正常排查也留下完整记录。</p>
            <UButton to="/inspections/new" label="开始一次排查" trailing-icon="i-lucide-arrow-right" color="neutral" variant="solid" class="mt-6 w-full justify-between bg-white text-primary hover:bg-white/90" />
          </section>
          <UCard>
            <h2 class="section-title">整理工作资料</h2>
            <p class="mt-2 text-sm leading-6 text-muted">按日期与社区导出台账，排查记录和通知单可在记录详情中下载。</p>
            <UButton to="/exports" label="导出整改台账" icon="i-lucide-file-down" variant="soft" class="mt-4 w-full justify-center" />
          </UCard>
        </div>
      </div>

      <UCard :ui="{ body: 'p-0 sm:p-0' }">
        <template #header><div class="flex items-center justify-between gap-3"><div><h2 class="section-title">最近的排查记录</h2><p class="mt-1 text-xs text-muted">每一次现场工作，都有迹可循</p></div><UButton to="/inspections" label="全部记录" trailing-icon="i-lucide-chevron-right" color="neutral" variant="link" class="-mr-2 text-xs" /></div></template>
        <div v-if="recent.length" class="divide-y divide-default">
          <NuxtLink v-for="record in recent" :key="record.id" :to="`/inspections/${record.id}`" class="flex items-center gap-4 px-5 py-4 hover:bg-muted/50 sm:px-6">
            <span class="hidden w-24 shrink-0 text-sm tabular-nums text-muted sm:block">{{ record.date }}</span>
            <div class="min-w-0 flex-1"><p class="truncate text-sm font-medium text-highlighted">{{ record.pointName }}</p><p class="mt-1 text-xs text-muted">{{ record.communityName }}<span class="sm:hidden"> · {{ record.date }}</span> · {{ record.inspector }}</p></div>
            <UBadge :color="record.result === 'normal' ? 'success' : 'warning'" variant="subtle">{{ record.result === 'normal' ? '正常' : `${record.issueCount} 项问题` }}</UBadge>
            <UIcon name="i-lucide-chevron-right" class="size-4 shrink-0 text-dimmed" />
          </NuxtLink>
        </div>
        <EmptyState v-else title="还没有排查记录" description="从一次排查开始，建立自己的工作档案。"><UButton to="/inspections/new" label="新建排查" icon="i-lucide-plus" /></EmptyState>
      </UCard>
    </PageState>
  </div>
</template>

<style scoped>
.reminder-grid { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px; }
.reminder-card { padding:18px 16px; border:1px solid var(--ui-border); border-radius:16px; background:#fff; color:#4c6154; transition:transform .15s,box-shadow .15s; }
.reminder-card:hover { transform:translateY(-2px); box-shadow:0 8px 25px #23352b08; }
.reminder-card.urgent { background:#fff5f2; color:#aa4438; border-color:#efd5cd; }
.reminder-card.due { color:#947029; }
.reminder-card.review { color:#3b6e8a; }
.reminder-card.missed { color:#466c51; }
.home-columns { display:grid; gap:20px; grid-template-columns:minmax(0,1fr); }
.priority-row { display:flex; align-items:flex-start; gap:14px; padding:20px; }
.priority-row:hover { background:#f8f9f6; }
.field-entry { padding:26px; background:#24553e; color:#fff; border-radius:16px; }
@media(min-width:1024px) { .reminder-grid { grid-template-columns:repeat(4,minmax(0,1fr)); gap:18px; } .reminder-card { padding:22px; } .home-columns { grid-template-columns:minmax(0,1.7fr) minmax(250px,1fr); gap:24px; } .priority-row { padding:22px 24px; } }
</style>
