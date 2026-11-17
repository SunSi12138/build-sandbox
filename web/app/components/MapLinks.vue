<script setup lang="ts">
import { mapLink } from '~/utils/format'
import type { GeoShape } from '~/types/api'
import { navigationCoordinate } from '~/utils/map'
const props = defineProps<{ name: string; longitude?: number | null; latitude?: number | null; labels?: boolean; shape?: GeoShape | null }>()
const anchor = computed(() => navigationCoordinate(props.shape) ?? (props.longitude != null && props.latitude != null ? [props.longitude, props.latitude] as [number, number] : null))
const chooseNavigationOpen = ref(false)
function navigateToSelection(shape: GeoShape | null) {
  if (!shape || shape.type !== 'Point' || !shape.coordinates[0]) return
  const [longitude, latitude] = shape.coordinates[0]
  if (longitude == null || latitude == null) return
  window.open(mapLink('navigation', props.name, longitude, latitude), '_blank', 'noopener,noreferrer')
}
</script>
<template><span v-if="anchor" class="inline-flex items-center gap-1 whitespace-nowrap"><UButton :to="mapLink('marker', name, anchor[0], anchor[1])" target="_blank" rel="noopener" icon="i-lucide-map" :label="labels ? '高德' : undefined" color="neutral" variant="ghost" aria-label="高德查看" @click.stop /><UButton :to="mapLink('navigation', name, anchor[0], anchor[1])" target="_blank" rel="noopener" icon="i-lucide-navigation" :label="labels ? '导航' : undefined" color="neutral" variant="ghost" aria-label="高德导航" @click.stop /><UButton v-if="shape?.type === 'Polygon'" type="button" color="neutral" variant="link" size="xs" label="指定导航点" @click.stop="chooseNavigationOpen = true" /></span><MapLocationPicker v-if="anchor && shape?.type === 'Polygon'" v-model:open="chooseNavigationOpen" :shape="{ type: 'Point', coordinates: [anchor] }" @confirm="navigateToSelection" /></template>
