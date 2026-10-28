<script setup lang="ts">
import type { IssueDto, IssueDetailDto, PageDto } from '~/types/api'
import { errorMessage, statusLabel } from '~/utils/format'
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ selected?: IssueDto[]; pointId?: string; disabled?: boolean }>(), { selected: () => [] })
const search = ref(''), records = ref<IssueDto[]>([]), known = ref<IssueDto[]>([...props.selected]), loading = ref(false), error = ref('')
const requestOwner = useLatestRequest()
const items = computed(() => [...new Map([...known.value.filter(item => model.value.includes(item.id)), ...records.value].map(item => [item.id, { label: `${item.pointName} · ${item.description}（${statusLabel(item.status)}）`, value: item.id }])).values()])
let timer: ReturnType<typeof setTimeout> | undefined
function rememberSelected() { known.value = [...new Map([...known.value, ...records.value, ...props.selected].filter(item => model.value.includes(item.id)).map(item => [item.id, item])).values()] }
async function load() {
  const request = requestOwner.start(); loading.value = true; error.value = ''; rememberSelected()
  try {
    const result = await $fetch<PageDto<IssueDto>>('/api/issues', { signal: request.signal, query: { q: search.value || undefined, pointId: props.pointId || undefined, pageSize: 30 } })
    if (!request.isCurrent()) return
    records.value = result.items
    const missing = model.value.filter(id => ![...known.value, ...records.value].some(item => item.id === id))
    const resolved = await Promise.all(missing.map(async id => (await $fetch<IssueDetailDto>(`/api/issues/${id}`, { signal: request.signal })).issue))
    if (request.isCurrent()) known.value = [...known.value, ...resolved].filter(item => model.value.includes(item.id))
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
watch([search, () => props.pointId], () => { requestOwner.cancel(); clearTimeout(timer); timer = setTimeout(load, 250) })
watch([model, () => props.selected], () => { rememberSelected(); if (model.value.some(id => !known.value.some(item => item.id === id))) { clearTimeout(timer); void load() } }, { deep: true })
onMounted(load); onBeforeUnmount(() => clearTimeout(timer))
</script>
<template><div><USelectMenu v-model="model" v-model:search-term="search" :items="items" value-key="value" multiple ignore-filter :loading="loading" :disabled="disabled" placeholder="选择相关整改问题" :search-input="{ placeholder: '搜索点位或问题内容' }" aria-label="关联整改问题" class="w-full" /><p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p></div></template>
