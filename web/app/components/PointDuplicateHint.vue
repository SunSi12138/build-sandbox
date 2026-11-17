<script setup lang="ts">
import type { PageDto, PointDto } from '~/types/api'
const props = withDefaults(defineProps<{ name: string; address?: string; communityId?: string }>(), { address: '', communityId: '' })
const matches = ref<PointDto[]>([])
let timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | undefined
let sequence = 0
async function check() {
  const version = ++sequence
  controller?.abort()
  const name = props.name.trim(), address = props.address.trim()
  const query = name.length >= 2 ? name : address.length >= 4 ? address : ''
  if (!query) { matches.value = []; return }
  controller = new AbortController()
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', {
      query: { q: query, temporary: 'all', disabled: 'all', communityId: props.communityId || undefined, pageSize: 10 },
      signal: controller.signal,
    })
    if (version !== sequence) return
    matches.value = result.items.filter(point => point.name.includes(name) || (name.length >= 2 && name.includes(point.name)) || (address.length >= 4 && point.address.includes(address))).slice(0, 4)
  } catch { if (version === sequence) matches.value = [] }
}
watch(() => [props.name, props.address, props.communityId], () => {
  clearTimeout(timer)
  timer = setTimeout(() => { void check() }, 350)
}, { immediate: true })
onBeforeUnmount(() => { clearTimeout(timer); controller?.abort(); ++sequence })
</script>
<template>
  <div v-if="matches.length" class="field-full rounded-lg border border-[var(--divider)] p-3 space-y-2" role="status">
    <p class="text-sm font-medium">可能已存在相似点位，请先核对，避免重复建立</p>
    <NuxtLink v-for="point in matches" :key="point.id" :to="`/points/${point.id}`" target="_blank" class="block text-sm text-primary">
      {{ point.name }} · {{ point.communityName }} · {{ point.address }}{{ point.temporary ? '（临时）' : '' }}
    </NuxtLink>
    <p class="text-xs text-muted">仅作提醒，核实后仍可继续新建。</p>
  </div>
</template>
