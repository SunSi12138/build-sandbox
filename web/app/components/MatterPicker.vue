<script setup lang="ts">
import type { MatterDto, MatterLinkDto, PageDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ selected?: MatterLinkDto[]; disabled?: boolean }>(), { selected: () => [] })
const search = ref(''), loading = ref(false), error = ref('')
const known = ref<MatterLinkDto[]>([...props.selected])
const candidates = ref<MatterLinkDto[]>([])
const items = computed(() => [...new Map([...known.value, ...candidates.value].map(item => [item.id, { label: `${item.title}${item.status === 'closed' ? '（已办结）' : ''}`, value: item.id }])).values()])
let version = 0, timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | undefined
async function load() {
  const current = ++version; controller?.abort(); controller = new AbortController(); loading.value = true; error.value = ''
  try {
    const result = await $fetch<PageDto<MatterDto>>('/api/matters', { signal: controller.signal, query: { q: search.value || undefined, pageSize: 30 } })
    if (current !== version) return
    known.value = [...new Map([...known.value, ...candidates.value].filter(item => model.value.includes(item.id)).map(item => [item.id, item])).values()]
    candidates.value = result.items
    for (const id of model.value) if (![...known.value, ...candidates.value].some(item => item.id === id)) {
      const result = await $fetch<{ matter: MatterDto }>(`/api/matters/${id}`)
      if (current === version) known.value.push(result.matter)
    }
  } catch (cause) { if (current === version && !controller.signal.aborted) error.value = errorMessage(cause) }
  finally { if (current === version) loading.value = false }
}
watch(search, () => { clearTimeout(timer); timer = setTimeout(load, 250) })
watch(() => props.selected, items => { known.value = [...items] }, { deep: true })
onMounted(load)
onBeforeUnmount(() => { ++version; clearTimeout(timer); controller?.abort() })
</script>
<template><div><USelectMenu v-model="model" v-model:search-term="search" :items="items" value-key="value" multiple ignore-filter :loading="loading" :disabled="disabled" placeholder="选择相关事项" :search-input="{ placeholder: '搜索事项标题、编号' }" class="w-full" aria-label="关联事项" /><p v-if="error" class="text-xs text-error">{{ error }}</p></div></template>
