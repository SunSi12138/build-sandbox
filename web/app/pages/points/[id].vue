<script setup lang="ts">
import type { PointDetailDto, PointDto, PointWriteRequest } from '~/types/api'
import { errorMessage, frequencyLabel, queryText, statusColor, statusLabel } from '~/utils/format'

const route = useRoute()
const toast = useToast()
const detail = ref<PointDetailDto | null>(null)
const loading = ref(true), error = ref(''), editorOpen = ref(false), deleteOpen = ref(false), deleting = ref(false), deleteError = ref(''), changing = ref(false)
const id = computed(() => queryText(route.params.id))
const back = computed(() => { const value = queryText(route.query.returnTo); return value.startsWith('/points?') ? value : '/points' })
useHead({ title: computed(() => `${detail.value?.point.name || '点位详情'} · 街道生态环保`) })
async function load() {
  if (!id.value) return
  loading.value = true; error.value = ''
  try { detail.value = await $fetch<PointDetailDto>(`/api/points/${id.value}`) }
  catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
}
watch(id, load, { immediate: true })
function saved(_point: PointDto) { toast.add({ title: '点位信息已更新', color: 'success' }); void load() }
async function toggleDisabled() {
  if (!detail.value) return
  changing.value = true
  const point = detail.value.point
  const body: PointWriteRequest = { name: point.name, communityId: point.communityId, typeId: point.typeId, address: point.address, contact: point.contact, phone: point.phone, frequency: point.frequency, notes: point.notes, disabled: !point.disabled }
  try { await $fetch(`/api/points/${id.value}`, { method: 'PUT', body }); toast.add({ title: body.disabled ? '点位已停用，历史资料保留' : '点位已启用', color: 'success' }); await load() }
  catch (value) { toast.add({ title: '未能更新点位', description: errorMessage(value), color: 'error' }) }
  finally { changing.value = false }
}
async function remove() {
  deleting.value = true; deleteError.value = ''
  try { await $fetch(`/api/points/${id.value}`, { method: 'DELETE', query: { confirm: true } }); deleteOpen.value = false; toast.add({ title: '点位已删除', color: 'success' }); await navigateTo(back.value) }
  catch (value) { deleteError.value = errorMessage(value) }
  finally { deleting.value = false }
}
</script>

<template>
  <div class="page-stack">
    <PageHeader :title="detail?.point.name || '点位详情'" :description="detail ? `${detail.point.communityName} · ${detail.point.typeName}` : ''" :back="back">
      <template v-if="detail" #actions><UButton label="编辑点位" icon="i-lucide-pencil" color="neutral" variant="outline" @click="editorOpen = true" /><UButton :to="{ path: '/inspections/new', query: { pointId: id } }" label="新建排查" icon="i-lucide-plus" :disabled="detail.point.disabled" /></template>
    </PageHeader>
    <PageState :loading="loading" :error="error" @retry="load">
      <template v-if="detail">
        <UAlert v-if="detail.point.disabled" title="此点位已停用" description="历史记录与问题仍然保留，启用后可以继续排查。" color="neutral" variant="subtle" icon="i-lucide-archive" />
        <UCard>
          <template #header><h2 class="section-title">点位信息</h2></template>
          <dl class="detail-grid"><div><dt>所属社区</dt><dd>{{ detail.point.communityName }}</dd></div><div><dt>点位类型</dt><dd>{{ detail.point.typeName }}</dd></div><div><dt>排查频次</dt><dd>{{ frequencyLabel(detail.point.frequency) }}</dd></div><div class="col-span-2 md:col-span-3"><dt>详细地址</dt><dd>{{ detail.point.address }}</dd></div><div><dt>联系人</dt><dd>{{ detail.point.contact || '未填写' }}</dd></div><div><dt>联系电话</dt><dd><a v-if="detail.point.phone" :href="`tel:${detail.point.phone}`" class="text-primary">{{ detail.point.phone }}</a><span v-else>未填写</span></dd></div><div><dt>最近排查日期</dt><dd>{{ detail.point.lastInspectionDate || '暂无记录' }}</dd></div><div v-if="detail.point.notes" class="col-span-2 md:col-span-3"><dt>备注</dt><dd class="whitespace-pre-wrap">{{ detail.point.notes }}</dd></div></dl>
        </UCard>
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header><div class="flex items-center gap-3"><h2 class="section-title">未闭环问题</h2><UBadge :color="detail.openIssues.length ? 'warning' : 'neutral'" variant="subtle">{{ detail.openIssues.length }} 项</UBadge></div></template>
          <div v-if="detail.openIssues.length" class="divide-y divide-default"><NuxtLink v-for="issue in detail.openIssues" :key="issue.id" :to="`/issues/${issue.id}`" class="flex items-start gap-4 p-5 hover:bg-muted/40 sm:p-6"><div class="min-w-0 flex-1"><p class="text-sm font-medium leading-6 text-highlighted">{{ issue.description }}</p><p class="mt-2 text-xs" :class="issue.overdue ? 'text-error' : 'text-muted'">整改期限 {{ issue.deadline }}<span v-if="issue.overdue" class="ml-2">已逾期</span></p></div><UBadge :color="statusColor(issue.status)" variant="subtle">{{ statusLabel(issue.status) }}</UBadge><UIcon name="i-lucide-chevron-right" class="mt-1 size-4 shrink-0 text-dimmed" /></NuxtLink></div>
          <EmptyState v-else title="没有未闭环问题" description="此点位当前无需跟进整改。" icon="i-lucide-circle-check-big" />
        </UCard>
        <UCard :ui="{ body: 'p-0 sm:p-0' }">
          <template #header><div class="flex items-center gap-3"><h2 class="section-title">历史排查</h2><span class="text-xs text-muted">{{ detail.inspections.length }} 次</span></div></template>
          <div v-if="detail.inspections.length" class="divide-y divide-default"><NuxtLink v-for="inspection in detail.inspections" :key="inspection.id" :to="{ path: `/inspections/${inspection.id}`, query: { returnTo: route.fullPath } }" class="flex items-center gap-4 p-5 hover:bg-muted/40 sm:p-6"><span class="flex size-10 shrink-0 items-center justify-center rounded-xl bg-muted text-muted"><UIcon name="i-lucide-clipboard-list" class="size-5" /></span><div class="min-w-0 flex-1"><p class="text-sm font-medium text-highlighted">{{ inspection.date }}</p><p class="mt-1 text-xs text-muted">排查人 {{ inspection.inspector }}<span v-if="inspection.noticeNumber"> · {{ inspection.noticeNumber }}</span></p></div><UBadge :color="inspection.result === 'normal' ? 'success' : 'warning'" variant="subtle">{{ inspection.result === 'normal' ? '正常' : `${inspection.issueCount} 项问题` }}</UBadge><UIcon name="i-lucide-chevron-right" class="size-4 text-dimmed" /></NuxtLink></div>
          <EmptyState v-else title="还没有排查记录" description="从这个点位开始一次现场排查。"><UButton :to="{ path: '/inspections/new', query: { pointId: id } }" label="新建排查" icon="i-lucide-plus" :disabled="detail.point.disabled" /></EmptyState>
        </UCard>
        <div class="flex flex-wrap items-center justify-between gap-4 rounded-xl border border-default px-5 py-4"><p class="text-xs leading-5 text-muted">有历史排查的点位可以停用，<br class="md:hidden">工作资料会完整保留。</p><div class="flex gap-2"><UButton :label="detail.point.disabled ? '启用点位' : '停用点位'" icon="i-lucide-archive" color="neutral" variant="ghost" :loading="changing" @click="toggleDisabled" /><UButton label="删除点位" icon="i-lucide-trash-2" color="error" variant="ghost" @click="deleteError = ''; deleteOpen = true" /></div></div>
      </template>
    </PageState>
    <PointEditor v-model:open="editorOpen" :point="detail?.point" @saved="saved" />
    <UModal v-model:open="deleteOpen" title="确认删除这个点位？" :description="`即将删除「${detail?.point.name || ''}」。已有排查记录的点位不能删除，请改用停用。`" :dismissible="!deleting"><template #body><UAlert v-if="deleteError" :description="deleteError" color="error" variant="subtle" /><p v-else class="text-sm leading-6 text-muted">删除后无法直接撤销，请再次核对点位名称。</p></template><template #footer><div class="flex w-full justify-end gap-3"><UButton label="取消" color="neutral" variant="outline" :disabled="deleting" @click="deleteOpen = false" /><UButton label="确认删除" color="error" :loading="deleting" @click="remove" /></div></template></UModal>
  </div>
</template>
