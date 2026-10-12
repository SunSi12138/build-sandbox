<script setup lang="ts">
import type { CampaignDetailDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
import { downloadWord } from '~/utils/wordDownload'
const route = useRoute()
const id = computed(() => String(route.params.id))
const detail = ref<CampaignDetailDto | null>(null), loading = ref(true), error = ref(''), deleteOpen = ref(false), deleting = ref(false), actionError = ref(''), downloading = ref(false)
const back = computed(() => { const target = queryText(route.query.returnTo); return target.startsWith('/campaigns?') || target === '/campaigns' ? target : '/campaigns' })
const toast = useToast()
useHead({ title: () => `${detail.value?.campaign.name ?? '整治详情'} · 街道生态环保` })
const menu = computed(() => [[{ label: '编辑', icon: 'i-lucide-pencil', to: `/campaigns/new?edit=${id.value}` }, { label: '删除整治', icon: 'i-lucide-trash-2', color: 'error' as const, onSelect: () => { actionError.value = ''; deleteOpen.value = true } }]])
const photoGroups = computed(() => [{ label: '整治前', photos: detail.value?.photos.filter(photo => photo.phase === 'before') ?? [] }, { label: '整治中', photos: detail.value?.photos.filter(photo => photo.phase === 'process') ?? [] }, { label: '整治后', photos: detail.value?.photos.filter(photo => photo.phase === 'after') ?? [] }])
async function load() { loading.value = true; error.value = ''; try { detail.value = await $fetch<CampaignDetailDto>(`/api/campaigns/${id.value}`) } catch (value) { error.value = errorMessage(value) } finally { loading.value = false } }
async function remove() {
  deleting.value = true; actionError.value = ''
  try { await $fetch(`/api/campaigns/${id.value}`, { method: 'DELETE', query: { confirm: true }, retry: 0 }); deleteOpen.value = false; toast.add({ title: '已删除', color: 'success', duration: 2000 }); await navigateTo('/campaigns') }
  catch (value) { actionError.value = errorMessage(value) }
  finally { deleting.value = false }
}
async function exportRecord() {
  downloading.value = true; actionError.value = ''
  try { await downloadWord(`/api/campaigns/${id.value}/export/record`, '整治记录单.docx') }
  catch (value) { actionError.value = errorMessage(value) }
  finally { downloading.value = false }
}
watch(id, load, { immediate: true })
</script>
<template>
  <div class="page-stack"><PageHeader :title="detail?.campaign.name ?? '整治详情'" :back="back"><template v-if="detail" #actions><UButton color="neutral" variant="outline" :loading="downloading" data-testid="export-campaign-record" @click="exportRecord"><span class="desktop-only">导出整治记录单</span><span class="mobile-only">导出</span></UButton><UDropdownMenu :items="menu"><UButton icon="i-lucide-ellipsis" color="neutral" variant="ghost" aria-label="更多" data-testid="campaign-more" /></UDropdownMenu></template></PageHeader>
    <UAlert v-if="actionError && !deleteOpen" :description="actionError" color="error" variant="subtle" />
    <PageState :loading="loading" :error="error" @retry="load"><template v-if="detail">
      <section class="panel"><div class="panel-head"><h2 class="section-title">基本信息</h2></div><div class="panel-body"><dl class="detail-grid"><div class="detail-wide"><dt>整治名称</dt><dd>{{ detail.campaign.name }}</dd></div><div><dt>整治类型</dt><dd>{{ detail.campaign.typeName }}</dd></div><div><dt>所属社区</dt><dd>{{ detail.campaign.communityName }}</dd></div><div class="detail-wide"><dt>整治地点</dt><dd>{{ detail.campaign.location }}</dd></div><div><dt>整治日期</dt><dd>{{ detail.campaign.date }}<template v-if="detail.campaign.endDate && detail.campaign.endDate !== detail.campaign.date"> 至 {{ detail.campaign.endDate }}</template></dd></div><div><dt>组织单位</dt><dd>{{ detail.campaign.organizer || '—' }}</dd></div><div class="detail-wide"><dt>参加单位及人员</dt><dd class="whitespace-pre-wrap">{{ detail.campaign.participants || '—' }}</dd></div><div v-if="detail.campaign.participantCount !== null"><dt>参加人数（人）</dt><dd>{{ detail.campaign.participantCount }}</dd></div><div v-if="detail.campaign.vehicleCount !== null"><dt>出动车辆（辆）</dt><dd>{{ detail.campaign.vehicleCount }}</dd></div><div v-if="detail.campaign.garbageTons !== null"><dt>清运垃圾（吨）</dt><dd>{{ detail.campaign.garbageTons.toFixed(2) }}</dd></div><div v-if="detail.campaign.areaSqm !== null"><dt>清理面积（㎡）</dt><dd>{{ detail.campaign.areaSqm.toFixed(2) }}</dd></div></dl></div></section>
      <section class="panel"><div class="panel-head"><h2 class="section-title">整治内容</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.campaign.content }}</p></section>
      <section v-if="detail.campaign.outcome" class="panel"><div class="panel-head"><h2 class="section-title">整治成效</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.campaign.outcome }}</p></section>
      <section v-for="group in photoGroups" :key="group.label" class="panel"><div class="panel-head"><h2 class="section-title">{{ group.label }}照片</h2></div><div class="panel-body"><IssuePhotos :photos="group.photos" :label="group.label" /></div></section>
      <section v-if="detail.campaign.notes" class="panel"><div class="panel-head"><h2 class="section-title">备注</h2></div><p class="panel-body whitespace-pre-wrap">{{ detail.campaign.notes }}</p></section>
    </template></PageState>
    <UModal v-model:open="deleteOpen" :title="`删除整治「${detail?.campaign.name ?? ''}」？`" description="整治记录和照片将被删除，无法恢复" :dismissible="!deleting" :close="!deleting" :ui="{ content: 'mobile-dialog max-w-[560px]' }"><template #body><UAlert v-if="actionError" :description="actionError" color="error" variant="subtle" /></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="deleting" @click="deleteOpen = false" /><UButton label="删除" color="error" :loading="deleting" data-testid="campaign-delete-confirm" @click="remove" /></template></UModal>
  </div>
</template>
