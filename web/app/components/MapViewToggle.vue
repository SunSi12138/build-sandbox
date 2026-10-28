<script setup lang="ts">
const view = defineModel<string>({ default: 'list' })
</script>
<template><div class="flex w-fit gap-1 rounded-lg border border-[var(--divider)] bg-[var(--surface)] p-1" aria-label="展示方式"><UButton type="button" label="列表" size="sm" :variant="view === 'list' ? 'solid' : 'ghost'" :color="view === 'list' ? 'primary' : 'neutral'" :aria-pressed="view === 'list'" @click="view = 'list'" /><UButton type="button" icon="i-lucide-map" label="地图" size="sm" :variant="view === 'map' ? 'solid' : 'ghost'" :color="view === 'map' ? 'primary' : 'neutral'" :aria-pressed="view === 'map'" @click="view = 'map'" /></div></template>
