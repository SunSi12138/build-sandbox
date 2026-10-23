<script setup lang="ts">
import type { PageDto, PointDto, PointDetailDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const model = defineModel<string>({ default: '' })
defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ selected: [point: PointDto | null] }>()
const search = ref(''), points = ref<PointDto[]>([]), selected = ref<PointDto | null>(null), error = ref(''), loading = ref(false)
const value = computed({ get: () => model.value || 'none', set: (value: string) => { model.value = value === 'none' ? '' : value } })
const items = computed(() => [{ label: '不关联点位', value: 'none' }, ...[...new Map([...(selected.value ? [selected.value] : []), ...points.value].map(point => [point.id, point])).values()].map(point => ({ label: `${point.name} · ${point.communityName}`, value: point.id }))])
let timer: ReturnType<typeof setTimeout> | undefined, version = 0
async function load() { const current = ++version; loading.value = true; try { const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { q: search.value, temporary: 'all', disabled: 'all', pageSize: 30 } }); if (current === version) points.value = result.items } catch (cause) { error.value = errorMessage(cause) } finally { if (current === version) loading.value = false } }
watch(model, async id => { if (!id) { selected.value = null; emit('selected', null); return } try { const detail = await $fetch<PointDetailDto>(`/api/points/${id}`); if (model.value === id) { selected.value = detail.point; emit('selected', detail.point) } } catch (cause) { error.value = errorMessage(cause) } }, { immediate: true })
watch(search, () => { clearTimeout(timer); timer = setTimeout(load, 250) })
onMounted(load); onBeforeUnmount(() => { ++version; clearTimeout(timer) })
</script>
<template><div><USelectMenu v-model="value" v-model:search-term="search" :items="items" value-key="value" ignore-filter :loading="loading" :disabled="disabled" class="w-full" aria-label="关联点位" :search-input="{ placeholder: '搜索点位' }" /><p v-if="error" class="text-xs text-error">{{ error }}</p></div></template>
