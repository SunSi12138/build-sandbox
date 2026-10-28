<script setup lang="ts">
import type { GeoShape } from '~/types/api'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ shape?: GeoShape | null }>()
const emit = defineEmits<{ confirm: [shape: GeoShape | null] }>()
const selection = ref<GeoShape | null>(null), mode = ref<'point' | 'polygon' | 'edit'>('point'), drawing = ref(false)
const map = ref<{ finish: () => void }>()
watch(open, value => { if (value) { selection.value = props.shape ? structuredClone(toRaw(props.shape)) : null; mode.value = selection.value ? 'edit' : 'point'; drawing.value = false } })
function changed(shape: GeoShape) { selection.value = shape; if (shape.type === 'Polygon') mode.value = 'edit' }
</script>
<template>
  <UModal v-model:open="open" title="标记位置" :ui="{ content: 'w-[100vw] max-w-[1100px]', body: 'p-0 sm:p-0', footer: 'justify-between' }">
    <template #body>
      <div class="map-picker-toolbar"><div class="flex flex-wrap gap-2"><UButton type="button" :variant="mode === 'point' ? 'solid' : 'outline'" label="选点" @click="mode = 'point'" /><UButton type="button" :variant="mode === 'polygon' ? 'solid' : 'outline'" label="画区域" @click="mode = 'polygon'" /><UButton v-if="selection" type="button" :variant="mode === 'edit' ? 'solid' : 'outline'" label="调整位置" @click="mode = 'edit'" /><UButton v-if="selection || drawing" type="button" color="neutral" variant="ghost" label="清空" @click="mode = 'point'; selection = null" /></div><p class="text-sm text-muted">{{ mode === 'point' ? '点击地图选择位置。' : mode === 'polygon' ? '依次点选边界，至少3点；双击或点“完成区域”。' : '拖动标记或边界节点调整。' }}</p></div>
      <div class="location-picker-map"><SimpleMap v-if="open" ref="map" :shape="selection" :mode="mode" @change="changed" @drawing="drawing = $event" /><UButton v-if="drawing" type="button" class="finish-area" label="完成区域" @click="map?.finish()" /></div>
    </template>
    <template #footer><UButton type="button" color="neutral" variant="outline" label="取消" @click="open = false" /><UButton type="button" :label="selection ? '确定' : '不标记'" :disabled="drawing" @click="emit('confirm', selection); open = false" /></template>
  </UModal>
</template>
<style scoped>
.map-picker-toolbar{display:flex;flex-direction:column;gap:10px;padding:12px 16px;border-bottom:1px solid var(--divider)}.location-picker-map{height:min(60dvh,620px);min-height:280px;position:relative}.finish-area{position:absolute;left:50%;bottom:24px;transform:translateX(-50%);box-shadow:0 2px 12px var(--border-strong)}
</style>
