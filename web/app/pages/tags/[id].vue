<script setup lang="ts">
import type { TagDto, PageDto } from '~/types/api'
import { errorMessage, queryText } from '~/utils/format'
const route = useRoute(), id = computed(() => String(route.params.id)), active = ref('matters'), name = ref('标签记录'), loading = ref(false), error = ref('')
const rows = ref<{ id: string; title: string; subtitle: string; path: string }[]>([]), counts = ref<Record<string, number>>({}), page = ref(1), total = ref(0)
const tabs = computed(() => [{ label: `事项 ${counts.value.matters ?? ''}`, value: 'matters' }, { label: `点位 ${counts.value.points ?? ''}`, value: 'points' }, { label: `排查 ${counts.value.inspections ?? ''}`, value: 'inspections' }, { label: `活动 ${counts.value.activities ?? ''}`, value: 'activities' }])
let version = 0
async function load() {
  const current = ++version; loading.value = true; error.value = ''
  try {
    const tags = await $fetch<TagDto[]>('/api/tags'), tag = tags.find(item => item.id === id.value)
    if (!tag) throw new Error('标签不存在')
    name.value = tag.name + (tag.disabled ? '（已停用）' : '')
    const query = { tagId: [id.value], temporary: 'all', disabled: 'all', page: page.value, pageSize: 30 }
    const result = await $fetch<PageDto<{ id: string; title?: string; name?: string; pointName?: string; date?: string; receivedDate?: string; communityName?: string }>>(`/api/${active.value}`, { query })
    if (current !== version) return
    rows.value = result.items.map(item => ({ id: item.id, title: item.title || item.name || item.pointName || '排查记录', subtitle: [item.date || item.receivedDate, item.communityName].filter(Boolean).join(' · '), path: `/${active.value}/${item.id}` })); total.value = result.total
    const summaries = await Promise.all(['matters', 'points', 'inspections', 'activities'].map(async kind => [kind, (await $fetch<PageDto<unknown>>(`/api/${kind}`, { query: { ...query, page: 1, pageSize: 1 } })).total] as const))
    if (current === version) counts.value = Object.fromEntries(summaries)
  } catch (cause) { if (current === version) error.value = errorMessage(cause) } finally { if (current === version) loading.value = false }
}
watch(active, () => { page.value = 1 }); watch([id, active, page], load, { immediate: true }); onBeforeUnmount(() => { ++version })
useHead({ title: () => name.value })
</script>
<template><div class="page-stack"><PageHeader :title="name" back="/settings?tab=tags" /><UTabs v-model="active" :items="tabs" :content="false" variant="link" /><PageState :loading="loading" :error="error" @retry="load"><section v-if="rows.length" class="group-list"><NuxtLink v-for="item in rows" :key="item.id" :to="item.path" class="list-row"><div class="row-body"><p class="row-title"><span>{{ item.title }}</span></p><p class="row-secondary">{{ item.subtitle }}</p></div></NuxtLink></section><EmptyState v-else title="暂无关联记录" /></PageState><UPagination v-if="total > 30" v-model:page="page" :total="total" :items-per-page="30" :sibling-count="0" /></div></template>
