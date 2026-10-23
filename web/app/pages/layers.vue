<script setup lang="ts">
import type { MapLayer } from '~/types/community'
import { errorMessage } from '~/utils/format'
const layers = ref<MapLayer[]>([]), error = ref(''), busy = ref(false), adding = ref(false)
const fresh = (): MapLayer => ({ id: '', name: '', folder: '', scheme: 'xyz', enabled: true, isDefault: false, opacity: 1, minZoom: 0, maxZoom: 18, sortOrder: 3 })
const draft = reactive(fresh())
async function load() { try { layers.value = await $fetch<MapLayer[]>('/api/map/layers') } catch(e) { error.value = errorMessage(e) } }
async function save(layer: MapLayer) { busy.value = true; error.value = ''; try { await $fetch('/api/map/layers', { method: 'POST', body: layer, retry: 0 }); adding.value = false; await load() } catch(e) { error.value = errorMessage(e) } finally { busy.value = false } }
onMounted(load)
</script>
<template>
  <div class="page-stack"><PageHeader title="地图图层" back="/map"><UButton label="新增图层" icon="i-lucide-plus" @click="Object.assign(draft, fresh()); adding = true" /></PageHeader><UAlert v-if="error" :description="error" color="error" />
    <section v-for="layer in layers" :key="layer.id" class="panel panel-body space-y-4"><div class="flex items-center gap-3"><UInput v-model="layer.name" aria-label="图层名称" class="flex-1" /><UBadge v-if="layer.isDefault" label="默认" variant="subtle" /><UCheckbox v-model="layer.enabled" label="显示" /></div><div class="field-grid"><UFormField label="透明度"><UInput v-model.number="layer.opacity" type="number" min="0" max="1" step="0.1" /></UFormField><UFormField label="显示顺序"><UInput v-model.number="layer.sortOrder" type="number" /></UFormField><UFormField label="缓存目录"><UInput v-model="layer.folder" /></UFormField><UFormField label="瓦片编号"><USelect v-model="layer.scheme" :items="['xyz','tms']" /></UFormField><UFormField label="最小级别"><UInput v-model.number="layer.minZoom" type="number" min="0" max="22" /></UFormField><UFormField label="最大级别"><UInput v-model.number="layer.maxZoom" type="number" min="0" max="22" /></UFormField></div><div class="flex gap-2"><UButton label="保存" :loading="busy" @click="save(layer)" /><UButton v-if="!layer.isDefault" label="设为默认" variant="outline" :disabled="busy" @click="save({ ...layer, isDefault: true })" /></div></section>
    <UModal v-model:open="adding" title="新增图层"><template #body><div class="space-y-3"><UFormField label="名称"><UInput v-model="draft.name" class="w-full" /></UFormField><UFormField label="目录名称"><UInput v-model="draft.folder" class="w-full" @update:model-value="draft.id = String($event)" /></UFormField><UFormField label="瓦片编号"><USelect v-model="draft.scheme" :items="['xyz','tms']" /></UFormField><UButton label="添加" :loading="busy" @click="save(draft)" /></div></template></UModal>
  </div>
</template>
