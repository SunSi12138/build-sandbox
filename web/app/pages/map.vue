<script setup lang="ts">
import type { MapState, MapFeature, GeoShape } from '~/types/community'
import type { PointDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const { bootstrap, load: loadBootstrap } = useAppData()
const route = useRoute()
const mapState = ref<MapState>({ layers: [], features: [] })
const loading = ref(true), error = ref(''), query = ref(''), selected = ref<MapFeature | null>(null)
const pending = ref<GeoShape | null>(null), saving = ref(false), editor = ref(false), gridId = ref(''), coordinates = ref('')
const canvas = useTemplateRef('canvas')
const grids = computed(() => (bootstrap.value?.communities ?? []).map(x => ({ label: x.name, value: x.id })))
const filtered = computed(() => mapState.value.features.filter(x => !query.value || x.name.includes(query.value)))
const pendingCoordinates = computed(() => pending.value?.coordinates[0]?.join(',') ?? '')
async function load() { loading.value = true; error.value = ''; try { await loadBootstrap(); mapState.value = await $fetch<MapState>('/api/map') } catch (e) { error.value = errorMessage(e) } finally { loading.value = false } }
function choose(item: MapFeature) { selected.value = item; canvas.value?.focus(item.shape) }
async function saveShape(kind: string, id: string, revision: number) {
  if (!pending.value) return
  saving.value = true; error.value = ''
  try { await $fetch(`/api/map/shapes/${kind}/${id}`, { method: 'PUT', body: { shape: pending.value, revision }, retry: 0 }); canvas.value?.cancel(); await load(); selected.value = mapState.value.features.find(x => x.kind === kind && x.id === id) ?? null }
  catch (e) { error.value = errorMessage(e) } finally { saving.value = false }
}
async function created(point: PointDto) { canvas.value?.cancel(); await load(); selected.value = mapState.value.features.find(x => x.kind === 'point' && x.id === point.id) ?? null }
function locate() {
  const xy = coordinates.value.trim().split(/[,，\s]+/).map(Number)
  if (xy.length !== 2 || !xy.every(Number.isFinite) || Math.abs(xy[0]!) > 180 || Math.abs(xy[1]!) > 85) { error.value = '请填写正确的经度、纬度'; return }
  error.value = ''; canvas.value?.locate(xy[0]!, xy[1]!)
}
onMounted(async () => { await load(); await nextTick(); const lon = Number(route.query.lon), lat = Number(route.query.lat); if (route.query.lon && route.query.lat && Number.isFinite(lon) && Number.isFinite(lat)) canvas.value?.locate(lon, lat) })
useHead({ title: '社区地图' })
</script>
<template>
  <div class="page-stack">
    <PageHeader title="社区地图"><UButton to="/layers" label="图层" icon="i-lucide-sliders-horizontal" color="neutral" variant="outline" /></PageHeader>
    <UAlert v-if="error" :description="error" color="error" />
    <div class="map-page-grid">
      <aside class="panel map-sidebar">
        <div class="panel-body space-y-3"><UInput v-model="query" placeholder="找场所、网格" icon="i-lucide-search" class="w-full" /><div class="flex gap-1"><UInput v-model="coordinates" placeholder="经度,纬度" class="min-w-0 flex-1" @keydown.enter="locate" /><UButton label="定位" color="neutral" variant="outline" @click="locate" /></div></div>
        <div class="map-place-list"><button v-for="item in filtered" :key="`${item.kind}:${item.id}`" class="map-place-row" :class="{ active: selected?.id === item.id }" @click="choose(item)"><strong>{{ item.name }}</strong><span>{{ item.kind === 'grid' ? '网格' : item.openIssues ? `${item.openIssues}项未完成` : '场所' }}</span></button><p v-if="!loading && !filtered.length" class="p-4 text-muted">暂无标记</p></div>
      </aside>
      <div class="space-y-3 min-w-0">
        <OfflineMap ref="canvas" v-model="pending" :features="mapState.features" :layers="mapState.layers" :selected-id="selected?.id" @choose="choose" />
        <div v-if="pending" class="panel panel-body flex flex-wrap gap-3 items-center">
          <UButton label="保存为新场所" icon="i-lucide-plus" :disabled="saving" @click="editor = true" />
          <UButton v-if="selected" :label="`保存到${selected.name}`" :loading="saving" variant="outline" @click="saveShape(selected.kind, selected.id, selected.revision)" />
          <template v-if="pending.type === 'Polygon'"><USelect v-model="gridId" :items="grids" placeholder="选择网格" /><UButton label="保存网格边界" :disabled="!gridId || saving" variant="outline" @click="saveShape('grid', gridId, mapState.features.find(x => x.kind === 'grid' && x.id === gridId)?.revision ?? 0)" /></template>
        </div>
        <div v-else-if="selected" class="panel panel-body flex flex-wrap gap-3 items-center"><strong class="mr-auto">{{ selected.name }}</strong><UButton label="修改位置" icon="i-lucide-pencil" color="neutral" variant="outline" @click="canvas?.edit(selected.shape)" /><UButton v-if="selected.kind === 'point'" :to="`/points/${selected.id}`" label="场所档案" /><UButton v-if="selected.kind === 'point'" :to="`/inspections/new?pointId=${selected.id}`" label="登记检查" variant="outline" /></div>
      </div>
    </div>
    <PointEditor v-model:open="editor" :initial-coordinates="pendingCoordinates" :initial-shape="pending" @saved="created" />
  </div>
</template>
<style scoped>
.map-page-grid{display:grid;grid-template-columns:240px minmax(0,1fr);gap:12px}.map-place-list{max-height:65vh;overflow:auto}.map-place-row{display:flex;flex-direction:column;align-items:flex-start;gap:3px;width:100%;padding:12px;text-align:left;border-bottom:1px solid var(--border)}.map-place-row span{font-size:12px;color:var(--text-3)}.map-place-row.active{background:var(--brand-50)}@media(max-width:767px){.map-page-grid{display:block}.map-sidebar{margin-bottom:12px}.map-place-list{max-height:140px;display:flex;overflow:auto}.map-place-row{flex:0 0 150px;border-right:1px solid var(--border)}}
</style>
