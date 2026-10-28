<script setup lang="ts">
import type { TagDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const route = useRoute(), id = computed(() => String(route.params.id)), active = ref('matters'), name = ref('标签记录')
const rows = ref<{ id: string; title: string; subtitle: string; path: string }[]>([]), counts = ref<Record<string, number>>({}), page = ref(1), total = ref(0)
const metadataBusy = ref(false), rowsBusy = ref(false), metadataError = ref(''), rowsError = ref('')
const metadataRequest = useLatestRequest(), rowsRequest = useLatestRequest()
const kinds = ['matters', 'points', 'inspections', 'activities'] as const
const tabs = computed(() => [{ label: `事项 ${counts.value.matters ?? ''}`, value: 'matters' }, { label: `点位 ${counts.value.points ?? ''}`, value: 'points' }, { label: `排查 ${counts.value.inspections ?? ''}`, value: 'inspections' }, { label: `活动 ${counts.value.activities ?? ''}`, value: 'activities' }])
function filters(kind: string, tagId: string) { return { tagId: [tagId], ...(kind === 'points' ? { temporary: 'all', disabled: 'all' } : {}) } }
async function loadMetadata() {
  const request = metadataRequest.start(), tagId = id.value
  metadataBusy.value = true; metadataError.value = ''; counts.value = {}; name.value = '标签记录'
  try {
    const [tags, summaries] = await Promise.all([
      $fetch<TagDto[]>('/api/tags', { signal: request.signal }),
      Promise.all(kinds.map(async kind => [kind, (await $fetch<PageDto<unknown>>(`/api/${kind}`, { signal: request.signal, query: { ...filters(kind, tagId), pageSize: 1 } })).total] as const))
    ])
    if (!request.isCurrent()) return
    const tag = tags.find(item => item.id === tagId)
    if (!tag) throw new Error('标签不存在')
    name.value = tag.name + (tag.disabled ? '（已停用）' : '')
    counts.value = Object.fromEntries(summaries)
  } catch (cause) { if (request.isCurrent()) metadataError.value = errorMessage(cause) }
  finally { if (request.isCurrent()) metadataBusy.value = false }
}
async function loadRows() {
  const request = rowsRequest.start(), kind = active.value, tagId = id.value, currentPage = page.value
  rowsBusy.value = true; rowsError.value = ''
  try {
    const result = await $fetch<PageDto<{ id: string; title?: string; name?: string; pointName?: string; date?: string; receivedDate?: string; communityName?: string }>>(`/api/${kind}`, { signal: request.signal, query: { ...filters(kind, tagId), page: currentPage, pageSize: 30 } })
    if (!request.isCurrent()) return
    rows.value = result.items.map(item => ({ id: item.id, title: item.title || item.name || item.pointName || '排查记录', subtitle: [item.date || item.receivedDate, item.communityName].filter(Boolean).join(' · '), path: `/${kind}/${item.id}` }))
    total.value = result.total
  } catch (cause) { if (request.isCurrent()) rowsError.value = errorMessage(cause) }
  finally { if (request.isCurrent()) rowsBusy.value = false }
}
function refresh() { void loadMetadata(); void loadRows() }
watch([id, active], () => { page.value = 1 })
watch(id, loadMetadata, { immediate: true })
watch([id, active, page], loadRows, { immediate: true })
useHead({ title: () => name.value })
</script>
<template><div class="page-stack"><PageHeader :title="name" back="/settings?tab=tags" /><UTabs v-model="active" :items="tabs" :content="false" variant="link" /><PageState :loading="metadataBusy || rowsBusy" :error="metadataError || rowsError" @retry="refresh"><section v-if="rows.length" class="group-list"><NuxtLink v-for="item in rows" :key="item.id" :to="item.path" class="list-row"><div class="row-body"><p class="row-title"><span>{{ item.title }}</span></p><p class="row-secondary">{{ item.subtitle }}</p></div></NuxtLink></section><EmptyState v-else title="暂无关联记录" /></PageState><UPagination v-if="total > 30" v-model:page="page" :total="total" :items-per-page="30" :sibling-count="0" /></div></template>
