<script setup lang="ts">
import type { IssueDto, PageDto } from '~/types/api'
import { errorMessage, statusLabel } from '~/utils/format'
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ selected?: IssueDto[]; pointId?: string; disabled?: boolean }>(), { selected: () => [] })
const search = ref(''), records = ref<IssueDto[]>([]), known = ref<IssueDto[]>([...props.selected]), loading = ref(false), error = ref('')
const items = computed(() => [...new Map([...known.value, ...records.value].map(item => [item.id, { label: `${item.pointName} · ${item.description}（${statusLabel(item.status)}）`, value: item.id }])).values()])
let timer: ReturnType<typeof setTimeout> | undefined, version = 0
async function load() { const current = ++version; loading.value = true; error.value = ''; try { const result = await $fetch<PageDto<IssueDto>>('/api/issues', { query: { q: search.value || undefined, pointId: props.pointId || undefined, pageSize: 30 } }); if (current === version) { known.value = [...new Map([...known.value, ...records.value].filter(item => model.value.includes(item.id)).map(item => [item.id, item])).values()]; records.value = result.items } } catch (cause) { if (current === version) error.value = errorMessage(cause) } finally { if (current === version) loading.value = false } }
watch([search, () => props.pointId], () => { clearTimeout(timer); timer = setTimeout(load, 250) }); onMounted(load); onBeforeUnmount(() => { ++version; clearTimeout(timer) })
</script>
<template><div><USelectMenu v-model="model" v-model:search-term="search" :items="items" value-key="value" multiple ignore-filter :loading="loading" :disabled="disabled" placeholder="选择相关整改问题" :search-input="{ placeholder: '搜索点位或问题内容' }" aria-label="关联整改问题" class="w-full" /><p v-if="error" class="text-xs text-error">{{ error }}</p></div></template>
