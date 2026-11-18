<script setup lang="ts">
import type { MapLayer } from '~/types/community'
import { errorMessage } from '~/utils/format'
import { clientId } from '~/utils/clientId'
const layers = ref<MapLayer[]>([]), busy = ref(false), error = ref('')
const editorOpen = ref(false), deleting = ref<MapLayer | null>(null)
const draft = ref<MapLayer | null>(null)
const newLayer = (): MapLayer => ({
  id: clientId('layer-'), name: '', folder: '', scheme: 'xyz',
  enabled: true, isDefault: false, opacity: 1, minZoom: 0, maxZoom: 20,
  sortOrder: 0, annotation: false, tileType: 'raster', styleJson: '',
  urlTemplate: '', apiKey: '', subdomains: '', updateCache: false, mapKey: '', cacheVersion: 0,
})
async function load() { try { layers.value = await $fetch('/api/map/layers') } catch (cause) { error.value = errorMessage(cause) } }
function edit(layer?: MapLayer) { draft.value = layer ? structuredClone(toRaw(layer)) : newLayer(); editorOpen.value = true; error.value = '' }
async function save() {
  if (!draft.value?.name.trim() || !draft.value.folder.trim()) return
  busy.value = true; error.value = ''
  try { await $fetch('/api/map/layers', { method: 'POST', body: draft.value, retry: 0 }); editorOpen.value = false; await load() }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
async function remove() {
  if (!deleting.value) return
  busy.value = true; error.value = ''
  try { await $fetch(`/api/map/layers/${deleting.value.id}`, { method: 'DELETE', query: { confirm: 'true' }, retry: 0 }); deleting.value = null; await load() }
  catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <section class="space-y-4">
    <div class="section-heading"><h2 class="section-title">地图图层</h2><UButton label="添加图层" icon="i-lucide-plus" variant="outline" @click="edit()" /></div>
    <UAlert v-if="error && !editorOpen" :description="error" color="error" />
    <div class="group-list">
      <div v-for="layer in layers" :key="layer.id" class="list-row">
        <div class="row-body"><p class="font-medium">{{ layer.name }}</p><p class="text-xs text-muted break-all">{{ layer.folder }}</p></div>
        <UButton icon="i-lucide-pencil" :aria-label="`编辑${layer.name}`" color="neutral" variant="ghost" @click="edit(layer)" />
        <UButton icon="i-lucide-trash-2" :aria-label="`删除${layer.name}`" color="error" variant="ghost" @click="deleting=layer" />
      </div>
    </div>
    <UModal v-model:open="editorOpen" :title="draft && layers.some(x=>x.id===draft?.id) ? '编辑图层' : '添加图层'" :dismissible="!busy" :close="!busy" :ui="{content:'max-w-2xl'}">
      <template #body><div v-if="draft" class="space-y-3">
        <div class="field-grid">
          <UFormField label="图层名称" required><UInput v-model="draft.name" class="w-full" :disabled="busy" /></UFormField>
          <UFormField label="图层类型"><USelect v-model="draft.tileType" :items="[{label:'卫星/栅格图',value:'raster'},{label:'MVT / PBF',value:'mvt'}]" class="w-full" :disabled="busy" /></UFormField>
          <UFormField label="本地缓存目录或 PMTiles 文件" class="field-full" required><UInput v-model="draft.folder" class="w-full" :disabled="busy" /></UFormField>
          <UFormField label="行号"><USelect v-model="draft.scheme" :items="[{label:'XYZ',value:'xyz'},{label:'TMS',value:'tms'}]" class="w-full" /></UFormField>
          <UFormField label="最小级别"><UInput v-model.number="draft.minZoom" type="number" min="0" max="22" class="w-full" /></UFormField>
          <UFormField label="最大级别"><UInput v-model.number="draft.maxZoom" type="number" min="0" max="22" class="w-full" /></UFormField>
          <UFormField label="透明度"><UInput v-model.number="draft.opacity" type="number" min="0" max="1" step="0.1" class="w-full" /></UFormField>
          <UFormField label="叠放顺序"><UInput v-model.number="draft.sortOrder" type="number" class="w-full" /></UFormField>
        </div>
        <UCheckbox v-model="draft.annotation" label="标注图层" />
        <UCheckbox v-model="draft.enabled" label="默认显示" />
        <UFormField v-if="draft.tileType==='mvt'" label="MVT 自定义样式（JSON）"><UTextarea v-model="draft.styleJson" :rows="7" class="w-full font-mono" /></UFormField>
        <UAlert v-if="error" :description="error" color="error" />
      </div></template>
      <template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="editorOpen=false" /><UButton label="保存" :disabled="busy||!draft?.name.trim()||!draft?.folder.trim()" :loading="busy" @click="save" /></template>
    </UModal>
    <UModal :open="!!deleting" title="删除图层" :dismissible="!busy" @update:open="value=>{if(!value)deleting=null}">
      <template #body><p>删除「{{ deleting?.name }}」？</p><UAlert v-if="error" :description="error" color="error" /></template>
      <template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="busy" @click="deleting=null" /><UButton label="删除" color="error" :loading="busy" @click="remove" /></template>
    </UModal>
  </section>
</template>