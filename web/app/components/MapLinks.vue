<script setup lang="ts">
import { mapLink } from '~/utils/format'
defineProps<{ name: string; longitude?: number | null; latitude?: number | null; labels?: boolean }>()
</script>
<template><span v-if="longitude != null && latitude != null" class="inline-flex gap-1"><UButton :to="mapLink('marker', name, longitude, latitude)" target="_blank" rel="noopener" icon="i-lucide-map" :label="labels ? '高德' : undefined" color="neutral" variant="ghost" aria-label="高德查看" @click.stop /><UButton :to="mapLink('navigation', name, longitude, latitude)" target="_blank" rel="noopener" icon="i-lucide-navigation" :label="labels ? '导航' : undefined" color="neutral" variant="ghost" aria-label="高德导航" @click.stop /></span></template>
