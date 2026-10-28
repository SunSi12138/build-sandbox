<script setup lang="ts">
import type { PointDetailDto } from '~/types/api'
import {
  coordinatesText,
  errorMessage,
  frequencyLabel,
  queryText,
  statusLabel,
  statusColor,
} from '~/utils/format'
const route = useRoute(),
  toast = useToast()
const detail = ref<PointDetailDto | null>(null)
const photos = computed(() => detail.value?.photos ?? [])
const loading = ref(true),
  error = ref(''),
  editorOpen = ref(false),
  deleteOpen = ref(false),
  deleting = ref(false),
  deleteError = ref(''),
  changing = ref(false),
  promoteOpen = ref(false)
const id = computed(() => queryText(route.params.id))
const back = computed(() => {
  const value = queryText(route.query.returnTo)
  return /^\/points\/?(?:\?|$)/.test(value) ? value : '/points'
})
useHead({ title: computed(() => `${detail.value?.point.name || '点位详情'} · 街道生态环保`) })
async function load() {
  if (!id.value) return
  loading.value = true
  error.value = ''
  try {
    detail.value = await $fetch<PointDetailDto>(`/api/points/${id.value}`)
  } catch (value) {
    error.value = errorMessage(value)
  } finally {
    loading.value = false
  }
}
async function saved() {
  toast.add({ title: '已保存', color: 'success', duration: 2000 })
  await load()
}
async function toggleDisabled() {
  if (!detail.value || changing.value) return
  changing.value = true
  try {
    await $fetch(`/api/points/${id.value}/status`, {
      method: 'PUT',
      body: { disabled: !detail.value.point.disabled },
      retry: 0,
    })
    await saved()
  } catch (value) {
    toast.add({ title: errorMessage(value), color: 'error', duration: 0 })
  } finally {
    changing.value = false
  }
}
async function remove() {
  deleting.value = true
  deleteError.value = ''
  try {
    await $fetch(`/api/points/${id.value}`, {
      method: 'DELETE',
      query: { confirm: true },
      retry: 0,
    })
    deleteOpen.value = false
    toast.add({ title: '已删除', duration: 2000 })
    await navigateTo(back.value)
  } catch (value) {
    deleteError.value = errorMessage(value)
  } finally {
    deleting.value = false
  }
}
const menu = computed(() => [
  ...(detail.value?.point.temporary
    ? [
        {
          label: '转为常态点位',
          onSelect: () => {
            promoteOpen.value = true
          },
        },
      ]
    : []),
  {
    label: detail.value?.point.disabled ? '启用点位' : '停用点位',
    onSelect: () => {
      void toggleDisabled()
    },
  },
  {
    label: '删除点位',
    color: 'error' as const,
    onSelect: () => {
      deleteError.value = ''
      deleteOpen.value = true
    },
  },
])
watch(id, load, { immediate: true })
</script>
<template>
  <div class="page-stack">
    <PageHeader :title="detail?.point.name || '点位详情'" :back="back"
      ><template v-if="detail" #badge
        ><StatusBadge v-if="detail.point.temporary" tone="outline">临时</StatusBadge
        ><StatusBadge v-if="detail.point.disabled" tone="neutral">已停用</StatusBadge></template
      ><template v-if="detail" #actions
        ><UButton
          label="编辑"
          color="neutral"
          variant="outline"
          @click="editorOpen = true" /><UButton
          :to="{ path: '/inspections/new', query: { pointId: id } }"
          :disabled="detail.point.disabled"
          class="header-action"
          ><span class="mobile-only">排查</span><span class="desktop-only">新增排查</span></UButton
        ><UButton :to="{ path: '/campaigns/new', query: { pointId: id } }" :disabled="detail.point.disabled" color="neutral" variant="outline" class="header-action" data-testid="point-new-campaign"><span class="mobile-only">整治</span><span class="desktop-only">新增整治</span></UButton><UDropdownMenu :items="menu"
          ><UButton
            icon="i-lucide-ellipsis"
            color="neutral"
            variant="ghost"
            aria-label="更多" /></UDropdownMenu></template
    ></PageHeader>
    <PageState :loading="loading" :error="error" @retry="load"
      ><template v-if="detail">
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">现场图片</h2>
            <span class="text-xs text-muted">{{ photos.length }}/9</span>
          </div>
          <div class="panel-body">
            <IssuePhotos :photos="photos" label="现场" />
          </div>
        </section>
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">基本信息</h2>
            <UButton
              v-if="detail.point.temporary"
              label="转为常态点位"
              variant="link"
              @click="promoteOpen = true"
            />
          </div>
          <dl class="detail-grid panel-body">
            <div>
              <dt>所属社区</dt>
              <dd>{{ detail.point.communityName }}</dd>
            </div>
            <div>
              <dt>点位类型</dt>
              <dd>{{ detail.point.typeName }}</dd>
            </div>
            <div>
              <dt>排查频次</dt>
              <dd>{{ frequencyLabel(detail.point.frequency) }}</dd>
            </div>
            <div class="detail-wide">
              <dt>详细地址</dt>
              <dd class="flex items-center justify-between gap-2">
                <span>{{ detail.point.address }}</span
                ><MapLinks
                  :name="detail.point.name"
                  :longitude="detail.point.longitude"
                  :latitude="detail.point.latitude"
                  labels
                />
              </dd>
            </div>
            <div v-if="detail.point.longitude != null" class="detail-wide">
              <dt>经纬度</dt>
              <dd>{{ coordinatesText(detail.point.longitude, detail.point.latitude) }}</dd>
            </div>
            <div>
              <dt>联系人</dt>
              <dd><NameTags :value="detail.point.contact" /></dd>
            </div>
            <div>
              <dt>电话</dt>
              <dd>
                <a
                  v-if="detail.point.phone"
                  :href="`tel:${detail.point.phone}`"
                  class="inline-flex items-center text-primary"
                  >{{ detail.point.phone }}</a
                ><span v-else>—</span>
              </dd>
            </div>
            <div v-if="detail.point.notes" class="detail-wide">
              <dt>备注</dt>
              <dd class="whitespace-pre-wrap">{{ detail.point.notes }}</dd>
            </div>
          </dl>
          <LocationPreview :shape="detail.point.shape" />
        </section>
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">未闭环问题</h2>
            <span class="text-xs text-muted">{{ detail.openIssues.length }} 项</span>
          </div>
          <NuxtLink
            v-for="issue in detail.openIssues"
            :key="issue.id"
            :to="`/inspections/${issue.inspectionId}?issue=${issue.id}`"
            class="list-row"
            ><div class="row-body">
              <p class="row-title">
                <span>{{ issue.description }}</span>
              </p>
              <p class="row-secondary" :class="{ 'text-error': issue.overdue }">
                {{ issue.deadline }}
              </p>
            </div>
            <StatusBadge :tone="statusColor(issue.status)">{{
              statusLabel(issue.status)
            }}</StatusBadge></NuxtLink
          ><EmptyState v-if="!detail.openIssues.length" title="暂无未闭环问题" />
        </section>
        <section class="panel">
          <div class="panel-head">
            <h2 class="section-title">历史排查</h2>
            <UButton
              :to="`/inspections?pointId=${id}`"
              label="查看全部"
              color="neutral"
              variant="link"
            />
          </div>
          <InspectionRow
            v-for="record in detail.inspections"
            :key="record.id"
            :record="record"
            :return-to="route.fullPath"
          /><EmptyState v-if="!detail.inspections.length" title="暂无排查记录" />
        </section>
        <section class="panel" data-testid="point-campaign-history">
          <div class="panel-head"><h2 class="section-title">历史整治</h2><UButton :to="`/campaigns?pointId=${id}`" label="查看全部" color="neutral" variant="link" /></div>
          <NuxtLink v-for="record in detail.campaigns ?? []" :key="record.id" :to="`/campaigns/${record.id}`" class="list-row"><div class="row-body"><p class="row-title"><span>{{ record.name }}</span></p><p class="row-secondary">{{ record.date }} · {{ record.typeName }}</p></div></NuxtLink>
          <EmptyState v-if="!detail.campaigns?.length" title="暂无整治记录" />
        </section> </template
    ></PageState>
    <PointEditor v-model:open="editorOpen" :point="detail?.point" @saved="saved" /><PointPromote
      v-model:open="promoteOpen"
      :point-id="id"
      @saved="saved"
    />
    <UModal
      v-model:open="deleteOpen"
      :title="`删除点位「${detail?.point.name || ''}」？`"
      description="删除后无法恢复"
      :dismissible="!deleting"
      :close="!deleting"
      ><template #body
        ><UAlert
          v-if="deleteError"
          :description="deleteError"
          color="error"
          variant="subtle" /></template
      ><template #footer
        ><UButton
          label="取消"
          color="neutral"
          variant="outline"
          :disabled="deleting"
          @click="deleteOpen = false" /><UButton
          label="删除"
          color="error"
          :loading="deleting"
          @click="remove" /></template
    ></UModal>
  </div>
</template>
