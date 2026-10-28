<script setup lang="ts">
import type { MatterDto, MatterLinkDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ selected?: MatterLinkDto[]; disabled?: boolean }>(), { selected: () => [] })
const search = ref(''), loading = ref(false), error = ref('')
const known = ref<MatterLinkDto[]>([...props.selected]), candidates = ref<MatterLinkDto[]>([])
const requestOwner = useLatestRequest()
const items = computed(() => [...new Map([...known.value.filter(item => model.value.includes(item.id)), ...candidates.value].map(item => [item.id, { label: `${item.title}${item.status === 'closed' ? '（已办结）' : ''}`, value: item.id }])).values()])
let timer: ReturnType<typeof setTimeout> | undefined
function rememberSelected() { known.value = [...new Map([...known.value, ...candidates.value, ...props.selected].filter(item => model.value.includes(item.id)).map(item => [item.id, item])).values()] }
async function load() {
  const request = requestOwner.start(); loading.value = true; error.value = ''; rememberSelected()
  try {
    const result = await $fetch<PageDto<MatterDto>>('/api/matters', { signal: request.signal, query: { q: search.value || undefined, pageSize: 30 } })
    if (!request.isCurrent()) return
    candidates.value = result.items
    const missing = model.value.filter(id => ![...known.value, ...candidates.value].some(item => item.id === id))
    const resolved = await Promise.all(missing.map(async id => (await $fetch<{ matter: MatterDto }>(`/api/matters/${id}`, { signal: request.signal })).matter))
    if (request.isCurrent()) known.value = [...known.value, ...resolved].filter(item => model.value.includes(item.id))
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
watch(search, () => { requestOwner.cancel(); clearTimeout(timer); timer = setTimeout(load, 250) })
watch([model, () => props.selected], () => { rememberSelected(); if (model.value.some(id => !known.value.some(item => item.id === id))) { clearTimeout(timer); void load() } }, { deep: true })
onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))
</script>
<template><div><USelectMenu v-model="model" v-model:search-term="search" :items="items" value-key="value" multiple ignore-filter :loading="loading" :disabled="disabled" placeholder="选择相关事项" :search-input="{ placeholder: '搜索事项标题、编号' }" class="w-full" aria-label="关联事项" /><p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p></div></template>
