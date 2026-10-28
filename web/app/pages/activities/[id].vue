<script setup lang="ts">
import type { ActivityDetailDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
const route = useRoute()
const id = computed(() => String(route.params.id))
const toast = useToast()
const detail = ref<ActivityDetailDto | null>(null)
const loading = ref(true), error = ref(''), deleting = ref(false), deleteOpen = ref(false), actionError = ref(''), downloading = ref(false)
const back = computed(() => { const value = queryText(route.query.returnTo); return value === '/activities' || value.startsWith('/activities?') ? value : '/activities' })
const menu = computed(() => [[{ label: '编辑', icon: 'i-lucide-pencil', to: `/activities/new?edit=${id.value}` },
  { label: '删除活动', icon: 'i-lucide-trash-2', color: 'error' as const, onSelect: () => { actionError.value = ''; deleteOpen.value = true } }]])
useHead({ title: () => `${detail.value?.activity.name ?? '活动详情'} · 街道生态环保` })
const detailRequest = useLatestRequest()
async function load() {
  if (!id.value) return
  const request = detailRequest.start(), recordId = id.value
  loading.value = true; error.value = ''
  try {
    const result = await $fetch<ActivityDetailDto>(`/api/activities/${recordId}`, { signal: request.signal })
    if (request.isCurrent()) { detail.value = result }
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) { loading.value = false } }
}
async function remove() {
  if (deleting.value) return
  deleting.value = true; actionError.value = ''
  try {
    await $fetch(`/api/activities/${id.value}`, { method: 'DELETE', query: { confirm: true }, retry: 0 })
    deleteOpen.value = false
    toast.add({ title: '已删除', color: 'success', duration: 2000 })
    await navigateTo('/activities')
  } catch (cause) { actionError.value = errorMessage(cause) }
  finally { deleting.value = false }
}
async function exportRecord() {
  downloading.value = true; actionError.value = ''
  try { await downloadWord(`/api/activities/${id.value}/export/record`, '活动开展情况记录单.docx') }
  catch (cause) { actionError.value = errorMessage(cause) }
  finally { downloading.value = false }
}
watch(id, () => { detail.value = null; void load() }, { immediate: true })
</script>
<template>
  <div class="page-stack">
    <PageHeader :title="detail?.activity.name ?? '活动详情'" :back="back">
      <template v-if="detail" #actions>
        <UButton color="neutral" variant="outline" :loading="downloading" data-testid="export-activity-record" @click="exportRecord"><span class="desktop-only">导出活动记录单</span><span class="mobile-only">导出</span></UButton>
        <UDropdownMenu :items="menu"><UButton icon="i-lucide-ellipsis" color="neutral" variant="ghost" aria-label="更多" /></UDropdownMenu>
      </template>
    </PageHeader>
    <UAlert v-if="actionError && !deleteOpen" :description="actionError" color="error" variant="subtle" />
    <PageState :loading="loading" :error="error" @retry="load">
      <template v-if="detail">
        <section class="panel">
          <div class="panel-head"><h2 class="section-title">基本信息</h2></div>
          <div class="panel-body"><dl class="detail-grid">
            <div class="detail-wide"><dt>活动名称</dt><dd>{{ detail.activity.name }}</dd></div>
            <div><dt>活动类型</dt><dd>{{ detail.activity.typeName }}</dd></div>
            <div><dt>所属社区</dt><dd>{{ detail.activity.communityName || '跨社区' }}</dd></div>
            <div><dt>开展日期</dt><dd>{{ detail.activity.date }}<template v-if="detail.activity.endDate && detail.activity.endDate !== detail.activity.date"> 至 {{ detail.activity.endDate }}</template></dd></div>
            <div><dt>活动地点</dt><dd>{{ detail.activity.location || '—' }}</dd></div>
            <div class="detail-wide"><dt>组织单位</dt><dd>{{ detail.activity.organizer || '—' }}</dd></div>
            <div class="detail-wide"><dt>参加单位及人员</dt><dd><NameTags :value="detail.activity.participants" /></dd></div>
            <div v-if="detail.activity.participantCount !== null"><dt>参加人数</dt><dd>{{ detail.activity.participantCount }} 人</dd></div>
          </dl></div>
        </section>
        <RecordRelations kind="activity" :id="id" :tags="detail.activity.tags" :matters="detail.activity.matters" @changed="load" />
        <section v-if="detail.activity.pointId || detail.activity.remediation" class="panel"><div class="panel-body"><dl class="detail-grid"><div v-if="detail.activity.pointId" class="detail-wide"><dt>关联点位</dt><dd><NuxtLink :to="`/points/${detail.activity.pointId}`" class="text-primary">{{ detail.activity.pointName }}</NuxtLink></dd></div><template v-if="detail.activity.remediation"><div><dt>整治类别</dt><dd>{{ detail.activity.remediation.typeName }}</dd></div><div v-if="detail.activity.remediation.vehicleCount !== null"><dt>出动车辆</dt><dd>{{ detail.activity.remediation.vehicleCount }} 辆</dd></div><div v-if="detail.activity.remediation.garbageTons !== null"><dt>清运垃圾</dt><dd>{{ detail.activity.remediation.garbageTons }} 吨</dd></div><div v-if="detail.activity.remediation.areaSqm !== null"><dt>清理面积</dt><dd>{{ detail.activity.remediation.areaSqm }} ㎡</dd></div></template></dl></div></section>
        <section v-if="detail.activity.relatedIssues.length" class="panel"><div class="panel-head"><h2 class="section-title">相关整改问题</h2></div><NuxtLink v-for="issue in detail.activity.relatedIssues" :key="issue.id" :to="{ path: `/inspections/${issue.inspectionId}`, query: { issue: issue.id } }" class="list-row">{{ issue.pointName }} · {{ issue.description }}</NuxtLink></section>
        <section class="panel"><div class="panel-head"><h2 class="section-title">开展情况</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.activity.content }}</p></section>
        <section v-if="detail.activity.outcome" class="panel"><div class="panel-head"><h2 class="section-title">活动成效</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.activity.outcome }}</p></section>
        <section v-if="detail.photos.length" class="panel"><div class="panel-head"><h2 class="section-title">{{ detail.activity.template === 'remediation' ? '整治照片' : '活动照片' }}</h2></div><div class="panel-body space-y-4"><template v-if="detail.activity.template === 'remediation'"><div v-for="phase in [{ key: 'before', label: '整治前' }, { key: 'process', label: '整治中' }, { key: 'after', label: '整治后' }]" :key="phase.key"><h3 class="mb-2 text-sm">{{ phase.label }}</h3><IssuePhotos :photos="detail.photos.filter(photo => photo.phase === phase.key)" :label="phase.label" /></div></template><IssuePhotos v-else :photos="detail.photos" label="活动" /></div></section>
        <section v-if="detail.activity.notes" class="panel"><div class="panel-head"><h2 class="section-title">备注</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.activity.notes }}</p></section>
      </template>
    </PageState>
    <UModal v-model:open="deleteOpen" :title="`删除活动「${detail?.activity.name ?? ''}」？`" description="记录及照片将被删除，无法恢复" :dismissible="!deleting" :close="!deleting" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
      <template #body><UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" /></template>
      <template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="deleting" @click="deleteOpen = false" /><UButton label="删除" color="error" :loading="deleting" data-testid="activity-delete-confirm" @click="remove" /></template>
    </UModal>
  </div>
</template>
