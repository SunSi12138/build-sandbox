<script setup lang="ts">
import type { GeoShape } from '~/types/api'
import { shapeLabel } from '~/utils/map'
const shape = defineModel<GeoShape | null>({ default: null })
withDefaults(defineProps<{ disabled?: boolean; clearable?: boolean; clearLabel?: string }>(), { clearable: true, clearLabel: '清除' })
const open = ref(false)
</script>
<template><div class="rounded-lg border border-[var(--divider)] p-3"><div class="flex flex-wrap items-center justify-between gap-2"><span class="text-sm" :class="shape ? 'text-primary' : 'text-muted'">{{ shapeLabel(shape) }}</span><div class="flex gap-1"><UButton type="button" icon="i-lucide-map-pin" :label="shape ? '编辑位置' : '标记位置'" variant="outline" :disabled="disabled" @click="open = true" /><UButton v-if="shape && clearable" type="button" :label="clearLabel" variant="ghost" color="neutral" :disabled="disabled" @click="shape = null" /></div></div><MapLocationPicker v-model:open="open" :shape="shape" @confirm="shape = $event" /></div></template>
