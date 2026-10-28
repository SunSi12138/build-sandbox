<script setup lang="ts">
import type { GeoShape } from '~/types/api'
import { shapeLabel } from '~/utils/map'
defineProps<{ shape?: GeoShape | null }>()
const open = ref(false)
</script>
<template><div v-if="shape" class="border-t border-[var(--divider)] px-4 py-3"><UButton type="button" :label="`${shapeLabel(shape)} · 查看地图`" icon="i-lucide-map" color="neutral" variant="outline" @click="open = true" /><UModal v-model:open="open" title="地图位置" :ui="{ content: 'w-[100vw] max-w-[1000px]', body: 'p-0 sm:p-0' }"><template #body><div class="h-[60dvh] min-h-[300px]"><SimpleMap v-if="open" :shape="shape" /></div></template></UModal></div></template>
