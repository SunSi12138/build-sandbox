<script setup lang="ts">
import type { PageDto, PointDto, PointDetailDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const model = defineModel<string>({ default: '' })
const props = defineProps<{ disabled?: boolean; activeOnly?: boolean }>()
const emit = defineEmits<{ selected: [point: PointDto | null] }>()
const search = ref(''), points = ref<PointDto[]>([]), selected = ref<PointDto | null>(null), searchError = ref(''), selectedError = ref(''), loading = ref(false)
const listRequest = useLatestRequest(), selectedRequest = useLatestRequest()
const value = computed({ get: () => model.value || 'none', set: (value: string) => { model.value = value === 'none' ? '' : value } })
const items = computed(() => [{ label: '不关联点位', value: 'none' }, ...[...new Map([...(selected.value ? [selected.value] : []), ...points.value].map(point => [point.id, point])).values()].map(point => ({ label: `${point.name} · ${point.communityName}${point.disabled ? '（已停用）' : ''}`, value: point.id }))])
let timer: ReturnType<typeof setTimeout> | undefined
async function load() {
  const request = listRequest.start(); loading.value = true; searchError.value = ''
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', { signal: request.signal, query: { q: search.value, temporary: 'all', disabled: props.activeOnly ? 'false' : 'all', pageSize: 30 } })
    if (request.isCurrent()) points.value = result.items
  } catch (cause) { if (request.isCurrent()) searchError.value = errorMessage(cause) }
  finally { if (request.isCurrent()) loading.value = false }
}
watch(model, async id => {
  const request = selectedRequest.start(); selectedError.value = ''; selected.value = null
  if (!id) { emit('selected', null); return }
  try {
    const detail = await $fetch<PointDetailDto>(`/api/points/${id}`, { signal: request.signal })
    if (request.isCurrent()) { selected.value = detail.point; emit('selected', detail.point) }
  } catch (cause) { if (request.isCurrent()) selectedError.value = errorMessage(cause) }
}, { immediate: true })
watch([search, () => props.activeOnly], () => { listRequest.cancel(); clearTimeout(timer); timer = setTimeout(load, 250) })
onMounted(load); onBeforeUnmount(() => clearTimeout(timer))
</script>
<template><div><USelectMenu v-model="value" v-model:search-term="search" :items="items" value-key="value" ignore-filter :loading="loading" :disabled="disabled" class="w-full" aria-label="关联点位" :search-input="{ placeholder: '搜索点位' }" /><p v-if="selectedError || searchError" class="text-xs text-error" role="alert">{{ selectedError || searchError }}</p></div></template>
