<script setup lang="ts">
defineProps<{ pointId: string; longitude?: number | null; latitude?: number | null; labels?: boolean }>()
</script>
<template><UButton v-if="longitude != null && latitude != null" :to="{ path: '/map', query: { place: pointId } }" icon="i-lucide-map" :label="labels ? '地图查看' : undefined" color="neutral" variant="ghost" aria-label="地图查看" @click.stop /></template>
