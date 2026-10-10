<script setup lang="ts">
import type { InspectionDetailDto } from '~/types/api'
import { errorMessage, queryText, statusColor, statusLabel } from '~/utils/format'

const route = useRoute()
const detail = ref<InspectionDetailDto | null>(null)
const loading = ref(true), error = ref('')
const id = computed(() => queryText(route.params.id))
const back = computed(() => { const value = queryText(route.query.returnTo); return value.startsWith('/points/') || value.startsWith('/inspections?') ? value : '/inspections' })
const sitePhotos = computed(() => detail.value?.photos.filter(photo => photo.phase === 'site') ?? [])
useHead({ title: computed(() => `${detail.value?.inspection.pointName || '排查记录'} · 排查详情`) })
async function load() {
  if (!id.value) return
  loading.value = true; error.value = ''
  try { detail.value = await $fetch<InspectionDetailDto>(`/api/inspections/${id.value}`) }
  catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
}
watch(id, load, { immediate: true })
</script>

<template>
  <div class="page-stack">
    <PageHeader title="排查记录详情" :description="detail ? `${detail.inspection.pointName} · ${detail.inspection.date}` : ''" :back="back">
      <template v-if="detail" #actions>
        <UButton :to="`/api/inspections/${id}/export/record`" external :prefetch="false" label="导出排查记录单" icon="i-lucide-download" color="neutral" variant="outline" data-testid="export-record" />
        <UButton :to="`/api/inspections/${id}/export/notice`" external :prefetch="false" label="导出整改通知单" icon="i-lucide-file-down" :disabled="!detail.issues.length" data-testid="export-notice" />
      </template>
    </PageHeader>
    <PageState :loading="loading" :error="error" @retry="load">
      <template v-if="detail">
        <UCard>
          <template #header><div class="flex flex-wrap items-center justify-between gap-3"><h2 class="section-title">排查基本信息</h2><UBadge :color="detail.inspection.result === 'normal' ? 'success' : 'warning'" variant="subtle">{{ detail.inspection.result === 'normal' ? '排查正常' : `发现 ${detail.issues.length} 项问题` }}</UBadge></div></template>
          <dl class="detail-grid"><div class="col-span-2 md:col-span-1"><dt>点位名称</dt><dd><NuxtLink :to="`/points/${detail.inspection.pointId}`" class="text-primary">{{ detail.inspection.pointName }}</NuxtLink></dd></div><div><dt>所属社区</dt><dd>{{ detail.inspection.communityName }}</dd></div><div><dt>点位类型</dt><dd>{{ detail.inspection.typeName }}</dd></div><div class="col-span-2 md:col-span-3"><dt>详细地址</dt><dd>{{ detail.inspection.address }}</dd></div><div><dt>排查日期</dt><dd>{{ detail.inspection.date }}</dd></div><div><dt>排查人</dt><dd>{{ detail.inspection.inspector }}</dd></div><div><dt>通知单编号</dt><dd>{{ detail.inspection.noticeNumber || '未填写' }}</dd></div></dl>
          <p class="mt-6 border-t border-default pt-4 text-xs leading-5 text-muted">这里保存的是排查当时的点位与社区信息，后续修改点位不会改变这份记录。</p>
        </UCard>

        <UCard v-if="detail.issues.length">
          <template #header><h2 class="section-title">问题清单</h2></template>
          <div class="space-y-5"><section v-for="(issue, index) in detail.issues" :key="issue.id" class="rounded-xl border border-default p-4 sm:p-5"><div class="flex items-start gap-3"><span class="flex size-7 shrink-0 items-center justify-center rounded-lg bg-muted text-xs font-semibold text-muted">{{ index + 1 }}</span><div class="min-w-0 flex-1"><h3 class="whitespace-pre-wrap text-sm font-medium leading-7 text-highlighted">{{ issue.description }}</h3></div><UBadge :color="statusColor(issue.status)" variant="subtle">{{ statusLabel(issue.status) }}</UBadge></div><dl class="mt-4 grid gap-4 text-sm sm:ml-10 sm:grid-cols-2"><div class="sm:col-span-2"><dt class="mb-1 text-xs text-muted">整改要求</dt><dd class="whitespace-pre-wrap leading-6">{{ issue.requirement || '未另行填写' }}</dd></div><div><dt class="mb-1 text-xs text-muted">整改期限</dt><dd :class="issue.overdue ? 'font-medium text-error' : ''">{{ issue.deadline }}<span v-if="issue.overdue" class="ml-2 text-xs">已逾期</span></dd></div><div><dt class="mb-1 text-xs text-muted">责任单位</dt><dd>{{ issue.responsibleUnit }}</dd></div></dl><div class="mt-4 flex justify-end"><UButton :to="`/issues/${issue.id}`" label="查看整改进展" trailing-icon="i-lucide-arrow-right" variant="soft" /></div></section></div>
        </UCard>
        <UAlert v-else title="本次排查结果正常" description="已保存完整排查记录，可直接导出排查记录单留存。" color="success" variant="subtle" icon="i-lucide-circle-check-big" />

        <UCard>
          <template #header><div class="flex items-center gap-3"><h2 class="section-title">现场照片</h2><span class="text-xs text-muted">{{ sitePhotos.length }} 张</span></div></template>
          <div v-if="sitePhotos.length" class="photo-grid"><figure v-for="(photo, index) in sitePhotos" :key="photo.id"><a :href="photo.url" target="_blank" rel="noopener" :aria-label="`查看现场照片 ${index + 1} 原图`"><img :src="photo.url" :width="photo.width" :height="photo.height" :alt="`${detail.inspection.pointName} 现场照片 ${index + 1}`" loading="lazy"></a><figcaption class="mt-2 text-xs text-muted">现场 {{ index + 1 }}</figcaption></figure></div>
          <p v-else class="py-5 text-center text-sm text-muted">本次排查没有上传现场照片。</p>
        </UCard>
        <UCard><template #header><h2 class="section-title">排查备注</h2></template><p class="whitespace-pre-wrap text-sm leading-7" :class="detail.inspection.notes ? 'text-default' : 'text-muted'">{{ detail.inspection.notes || '本次排查未填写备注。' }}</p></UCard>
      </template>
    </PageState>
  </div>
</template>
