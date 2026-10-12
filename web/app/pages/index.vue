<script setup lang="ts">
import type { DashboardDto } from '~/types/api'
import { errorMessage, frequencyLabel } from '~/utils/format'
useHead({ title: '工作首页 · 街道生态环保' })
const dashboard = ref<DashboardDto | null>(null), loading = ref(true), error = ref('')
async function load() { loading.value = true; error.value = ''; try { dashboard.value = await $fetch<DashboardDto>('/api/dashboard') } catch (value) { error.value = errorMessage(value) } finally { loading.value = false } }
const cards = computed(() => dashboard.value ? [
  { label: '逾期未闭环', count: dashboard.value.overdue.count, to: '/inspections?overdue=true', tone: 'danger' },
  { label: '3 天内到期', count: dashboard.value.dueSoon.count, to: '/inspections?dueSoon=true', tone: 'warning' },
  { label: '待复查', count: dashboard.value.awaitingReview.count, to: '/inspections?awaitingReview=true', tone: 'info' },
  { label: '超周期未排查', count: dashboard.value.missedInspections.count, to: '/points?missed=true', tone: 'neutral' }
] : [])
onMounted(load)
</script>
<template>
  <div class="page-stack">
    <PageHeader title="首页"><template #actions><SystemListen /></template></PageHeader>
    <PageState :loading="loading" :error="error" @retry="load"><template v-if="dashboard">
      <section class="stats-grid" aria-label="工作提醒"><NuxtLink v-for="card in cards" :key="card.label" :to="card.to" class="stat-block" :data-tone="card.count ? card.tone : 'neutral'"><span class="stat-label">{{ card.label }}</span><strong class="stat-number">{{ card.count }}</strong></NuxtLink></section>
      <div class="quick-actions"><UButton to="/inspections/new" icon="i-lucide-plus" label="新增排查" data-testid="home-new-inspection" /><UButton to="/campaigns/new" icon="i-lucide-plus" label="新增整治" color="neutral" variant="outline" /></div>
      <div class="dashboard-columns"><section><div class="section-heading"><h2 class="section-title">待办</h2><UButton to="/inspections?openOnly=true" label="查看全部" color="neutral" variant="link" data-testid="home-all-todos" /></div><div class="group-list" data-testid="home-todos"><InspectionRow v-for="record in dashboard.todos?.slice(0, 5) ?? []" :key="record.id" :record="record" /><EmptyState v-if="!dashboard.todos?.length" title="暂无待办" icon="i-lucide-circle-check" /></div></section>
        <div class="dashboard-right"><section><div class="section-heading"><h2 class="section-title">超周期未排查</h2><UButton to="/points?missed=true" label="查看全部" color="neutral" variant="link" /></div><div class="group-list"><div v-for="point in dashboard.missedInspections.items.slice(0, 5)" :key="point.id" class="list-row"><NuxtLink :to="`/points/${point.id}`" class="row-body"><p class="row-title"><span>{{ point.name }}</span></p><p class="row-secondary">{{ point.communityName }} · {{ frequencyLabel(point.frequency) }}</p></NuxtLink><UButton :to="{ path: '/inspections/new', query: { pointId: point.id } }" label="排查" color="neutral" variant="outline" /></div><EmptyState v-if="!dashboard.missedInspections.items.length" title="暂无超周期点位" icon="i-lucide-map-pin" /></div></section>
          <section><div class="section-heading"><h2 class="section-title">最近排查</h2><UButton to="/inspections" label="查看全部" color="neutral" variant="link" /></div><div class="group-list"><InspectionRow v-for="record in dashboard.recentInspections?.slice(0, 5) ?? []" :key="record.id" :record="record" /><EmptyState v-if="!dashboard.recentInspections?.length" title="暂无排查记录" /></div></section>
        </div>
      </div>
    </template></PageState>
  </div>
</template>
