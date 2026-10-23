<script setup lang="ts">
import OLMap from 'ol/Map'
import View from 'ol/View'
import Feature from 'ol/Feature'
import Point from 'ol/geom/Point'
import Polygon from 'ol/geom/Polygon'
import Geometry from 'ol/geom/Geometry'
import TileLayer from 'ol/layer/Tile'
import VectorLayer from 'ol/layer/Vector'
import VectorSource from 'ol/source/Vector'
import XYZ from 'ol/source/XYZ'
import { Draw, Modify, Snap } from 'ol/interaction'
import { fromLonLat, toLonLat } from 'ol/proj'
import { Fill, Stroke, Style, Circle, Text } from 'ol/style'
import { ScaleLine, defaults as defaultControls } from 'ol/control'
import 'ol/ol.css'
import type { GeoShape, MapFeature, MapLayer } from '~/types/community'

const props = withDefaults(defineProps<{ features?: MapFeature[]; layers?: MapLayer[]; editable?: boolean; selectedId?: string; height?: string }>(), { features: () => [], layers: () => [], editable: true, height: '65vh' })
const shape = defineModel<GeoShape | null>({ default: null })
const emit = defineEmits<{ choose: [feature: MapFeature]; coordinate: [coordinate: number[]] }>()
const element = useTemplateRef<HTMLElement>('mapElement')
const mode = ref<'view' | 'Point' | 'Polygon' | 'edit'>('view')
const coordinate = ref(''), missing = ref(false), snapEnabled = ref(true)
let map: OLMap | undefined, draw: Draw | undefined, modify: Modify | undefined, snaps: Snap[] = []
const source = new VectorSource<Feature<Geometry>>()
const editing = new VectorSource<Feature<Geometry>>()
let tiles: TileLayer<XYZ>[] = []
let resizing: ResizeObserver | undefined
function geometry(value: GeoShape) { return value.type === 'Point' ? new Point(fromLonLat(value.coordinates[0]!)) : new Polygon([value.coordinates.map(x => fromLonLat(x))]) }
function readShape(g: Geometry): GeoShape {
  const xy = (coordinate: number[]) => toLonLat(coordinate).map(x => Number(x.toFixed(8)))
  return g instanceof Point ? { type: 'Point', coordinates: [xy(g.getCoordinates())] } : { type: 'Polygon', coordinates: (g as Polygon).getCoordinates()[0]!.map(xy) }
}
function featureStyle(feature: { get: (key: string) => unknown }) {
  const item = feature.get('record') as MapFeature | undefined
  const color = item?.kind === 'grid' ? '#64748b' : item?.openIssues ? '#c03b32' : '#087f70'
  return new Style({ fill: new Fill({ color: item?.kind === 'grid' ? '#64748b10' : `${color}25` }), stroke: new Stroke({ color, width: props.selectedId === item?.id ? 4 : 2, lineDash: item?.kind === 'grid' ? [6, 5] : undefined }), image: new Circle({ radius: props.selectedId === item?.id ? 9 : 6, fill: new Fill({ color }), stroke: new Stroke({ color: '#fff', width: 2 }) }), text: new Text({ text: item?.name, font: '13px "Microsoft YaHei",sans-serif', offsetY: -17, fill: new Fill({ color: '#17211e' }), stroke: new Stroke({ color: '#fff', width: 4 }) }) })
}
function refreshFeatures() {
  source.clear()
  source.addFeatures(props.features.filter(x => !x.disabled).map(item => { const f = new Feature({ geometry: geometry(item.shape), record: item }); f.setId(`${item.kind}:${item.id}`); return f }))
}
function refreshLayers() {
  if (!map) return
  tiles.forEach(layer => map!.removeLayer(layer)); tiles = []; missing.value = false
  for (const item of [...props.layers].filter(x => x.enabled).sort((a,b) => a.sortOrder - b.sortOrder)) {
    const tileSource = new XYZ({ url: `/api/map/tiles/${encodeURIComponent(item.id)}/{z}/{x}/{y}`, minZoom: item.minZoom, maxZoom: item.maxZoom, transition: 0, crossOrigin: 'anonymous' })
    tileSource.on('tileloaderror', () => { missing.value = true })
    const layer = new TileLayer({ source: tileSource, opacity: item.opacity, zIndex: item.sortOrder })
    tiles.push(layer); map.addLayer(layer)
  }
}
function clearInteractions() {
  if (!map) return
  if (draw) map.removeInteraction(draw); if (modify) map.removeInteraction(modify)
  snaps.forEach(x => map!.removeInteraction(x)); snaps = []; draw = undefined; modify = undefined
}
function addSnap() {
  if (!map || !snapEnabled.value) return
  for (const s of [source, editing]) { const snap = new Snap({ source: s, pixelTolerance: 14 }); snaps.push(snap); map.addInteraction(snap) }
}
function start(type: 'Point' | 'Polygon') {
  if (!map) return
  clearInteractions(); editing.clear(); shape.value = null; mode.value = type
  draw = new Draw({ source: editing, type })
  draw.on('drawend', event => {
    shape.value = readShape(event.feature.getGeometry()!)
    window.setTimeout(() => startEdit(), 0)
  })
  map.addInteraction(draw); addSnap()
}
function startEdit() {
  if (!map) return
  clearInteractions(); mode.value = 'edit'
  modify = new Modify({ source: editing, pixelTolerance: 14 })
  modify.on('modifyend', () => { const feature = editing.getFeatures()[0]; if (feature) shape.value = readShape(feature.getGeometry()!) })
  map.addInteraction(modify); addSnap()
}
function cancel() { clearInteractions(); editing.clear(); shape.value = null; mode.value = 'view' }
function edit(value: GeoShape) { const copy: GeoShape = { type: value.type, coordinates: value.coordinates.map(x => [...x]) }; cancel(); shape.value = copy; editing.addFeature(new Feature(geometry(copy))); startEdit() }
function locate(lon: number, lat: number) { map?.getView().animate({ center: fromLonLat([lon, lat]), zoom: Math.max(map.getView().getZoom() ?? 16, 17), duration: 250 }) }
function focus(value: GeoShape) { if (!map) return; map.getView().fit(geometry(value).getExtent(), { padding: [60,60,60,60], maxZoom: 18, duration: 200 }) }
defineExpose({ start, cancel, edit, locate, focus })
watch(() => props.features, refreshFeatures, { deep: true })
watch(() => props.layers, refreshLayers, { deep: true })
watch(() => props.selectedId, () => source.changed())
watch(snapEnabled, () => { snaps.forEach(x => map?.removeInteraction(x)); snaps = []; if (mode.value !== 'view') addSnap() })
watch(element, target => {
  if (!target || map) return
  const editStyle = new Style({
    fill: new Fill({ color: '#f59e0b30' }), stroke: new Stroke({ color: '#d97706', width: 3 }),
    image: new Circle({ radius: 7, fill: new Fill({ color: '#d97706' }), stroke: new Stroke({ color: '#fff', width: 2 }) })
  })
  map = new OLMap({
    target,
    view: new View({ center: fromLonLat([88.893426,29.274331]), zoom: 16, minZoom: 3, maxZoom: 22 }),
    controls: defaultControls({ attribution: false }).extend([new ScaleLine()]),
    layers: [new VectorLayer({ source, style: featureStyle, zIndex: 100 }), new VectorLayer({ source: editing, zIndex: 101, style: editStyle })]
  })
  map.on('pointermove', event => { coordinate.value = toLonLat(event.coordinate).map(x => x.toFixed(6)).join(', ') })
  map.on('singleclick', event => {
    if (mode.value !== 'view') return
    const f = map!.forEachFeatureAtPixel(event.pixel, f => f, { hitTolerance: 8 })
    const record = f?.get('record') as MapFeature | undefined
    if (record) emit('choose', record)
    emit('coordinate', toLonLat(event.coordinate))
  })
  map.on('movestart', () => { missing.value = false })
  refreshFeatures(); refreshLayers()
  if (!props.editable && props.features.length === 1) focus(props.features[0]!.shape)
  resizing = new ResizeObserver(() => map?.updateSize()); if (element.value) resizing.observe(element.value)
}, { flush: 'post' })
onBeforeUnmount(() => { resizing?.disconnect(); map?.setTarget(undefined); map?.dispose() })
</script>
<template>
  <div class="offline-map-wrap">
    <div v-if="editable" class="map-tools">
      <UButton label="标点" icon="i-lucide-map-pin" :variant="mode === 'Point' ? 'solid' : 'outline'" @click="start('Point')" />
      <UButton label="画区域" icon="i-lucide-square" :variant="mode === 'Polygon' ? 'solid' : 'outline'" @click="start('Polygon')" />
      <template v-if="mode !== 'view'">
        <UButton v-if="mode === 'Polygon'" label="撤回一点" color="neutral" variant="outline" @click="draw?.removeLastPoint()" />
        <UButton v-if="mode === 'Polygon'" label="画好了" color="neutral" variant="outline" @click="draw?.finishDrawing()" />
        <UButton label="取消" color="neutral" variant="ghost" @click="cancel" />
        <UCheckbox v-model="snapEnabled" label="吸附边界" />
      </template>
      <span v-if="mode === 'edit'" class="text-sm text-muted">拖动节点调整</span>
    </div>
    <div ref="mapElement" class="offline-map" :style="{ height }" tabindex="0" aria-label="社区地图" />
    <div class="map-footer"><span>{{ coordinate }} WGS84</span><span v-if="missing">部分区域暂无缓存</span><span v-else>本地地图</span></div>
  </div>
</template>
<style scoped>
.offline-map-wrap{position:relative;background:#e6e9e5;border:1px solid var(--border);border-radius:10px;overflow:hidden}.map-tools{display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:10px;background:white}.offline-map{width:100%;min-height:300px;background:repeating-linear-gradient(45deg,#e5e8e4,#e5e8e4 10px,#e0e4df 10px,#e0e4df 20px)}.map-footer{display:flex;justify-content:space-between;gap:8px;padding:5px 10px;background:#fff;font-size:12px;color:var(--text-3)}:deep(.ol-zoom button){width:34px;height:34px;font-size:23px;background:white;color:#17211e}:deep(.ol-control){background:#ffffffaa}
</style>
