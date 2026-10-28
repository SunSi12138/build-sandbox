<script setup lang="ts">
import Map from 'ol/Map'
import View from 'ol/View'
import Feature from 'ol/Feature'
import Point from 'ol/geom/Point'
import Polygon from 'ol/geom/Polygon'
import VectorSource from 'ol/source/Vector'
import VectorLayer from 'ol/layer/Vector'
import TileLayer from 'ol/layer/Tile'
import XYZ from 'ol/source/XYZ'
import Draw from 'ol/interaction/Draw'
import Modify from 'ol/interaction/Modify'
import { defaults as defaultControls, ScaleLine, Attribution } from 'ol/control'
import { fromLonLat, toLonLat } from 'ol/proj'
import { isEmpty } from 'ol/extent'
import { Style, Stroke, Fill, Circle as CircleStyle, Text } from 'ol/style'
import type { Geometry } from 'ol/geom'
import type { GeoShape } from '~/types/api'
import { mapColors, type MapRecord } from '~/utils/map'
import 'ol/ol.css'

const props = withDefaults(defineProps<{ records?: MapRecord[]; shape?: GeoShape | null; mode?: 'view' | 'point' | 'polygon' | 'edit'; selected?: string[] }>(), { records: () => [], shape: null, mode: 'view', selected: () => [] })
const emit = defineEmits<{ select: [ids: string[]]; change: [shape: GeoShape]; drawing: [active: boolean] }>()
const host = ref<HTMLElement>(), missingTiles = ref(false), missingLabels = ref(false)
const sketchVertices = ref(0)
let map: Map | undefined, draw: Draw | undefined, modify: Modify | undefined, observer: ResizeObserver | undefined
const recordsSource = new VectorSource<Feature<Geometry>>(), editingSource = new VectorSource<Feature<Geometry>>()
let reporting = false, initialFit = false
function geometry(shape: GeoShape) { return shape.type === 'Point' ? new Point(fromLonLat(shape.coordinates[0]!)) : new Polygon([shape.coordinates.map(c => fromLonLat(c))]) }
function toShape(feature: Feature<Geometry>): GeoShape {
  const item = feature.getGeometry()!
  const coordinates = item instanceof Point ? [toLonLat(item.getCoordinates())] : (item as Polygon).getCoordinates()[0]!.map(c => toLonLat(c))
  return { type: item instanceof Point ? 'Point' : 'Polygon', coordinates: coordinates.map(c => c.map(n => Math.round(n * 1e6) / 1e6)) }
}
function style(feature: Feature<Geometry>, editing = false) {
  const color = editing ? mapColors.editing : feature.get('color') || mapColors.normal
  const active = editing || props.selected.includes(String(feature.getId()))
  return new Style({
    stroke: new Stroke({ color, width: active ? 4 : 2 }), fill: new Fill({ color: `${color}25` }),
    image: new CircleStyle({ radius: active ? 9 : 7, fill: new Fill({ color }), stroke: new Stroke({ color: mapColors.halo, width: 3 }) }),
    text: editing ? undefined : new Text({ text: feature.get('title'), font: '600 13px "Microsoft YaHei",sans-serif', offsetY: -22, overflow: true, fill: new Fill({ color: mapColors.label }), stroke: new Stroke({ color: mapColors.halo, width: 4 }) }),
  })
}
function renderRecords() {
  recordsSource.clear()
  for (const record of props.records) {
    if (!record.shape) continue
    const feature = new Feature({ geometry: geometry(record.shape), title: record.title, color: record.color })
    feature.setId(record.id); recordsSource.addFeature(feature)
  }
  if (!initialFit && recordsSource.getFeatures().length) { fitAll(); initialFit = true }
}
function renderShape() {
  if (reporting) return
  editingSource.clear()
  if (props.shape) editingSource.addFeature(new Feature(geometry(props.shape)))
}
function report(feature: Feature<Geometry>) {
  reporting = true; emit('change', toShape(feature)); void nextTick(() => { reporting = false })
}
function interactions() {
  if (!map) return
  if (draw) map.removeInteraction(draw)
  if (modify) map.removeInteraction(modify)
  draw = undefined; modify = undefined; sketchVertices.value = 0; emit('drawing', false)
  if (props.mode === 'polygon') {
    draw = new Draw({ source: editingSource, type: 'Polygon', stopClick: true })
    draw.on('drawstart', event => {
      editingSource.clear(); emit('drawing', true)
      const polygon = event.feature.getGeometry() as Polygon
      polygon.on('change', () => { sketchVertices.value = Math.max(0, polygon.getCoordinates()[0]!.length - 2) })
    })
    draw.on('drawend', event => { emit('drawing', false); report(event.feature) })
    map.addInteraction(draw)
  } else if (props.mode === 'edit') {
    modify = new Modify({ source: editingSource }); modify.on('modifyend', event => { const feature = event.features.item(0); if (feature) report(feature) }); map.addInteraction(modify)
  }
}
function fitAll() {
  if (!map) return
  const source = editingSource.getFeatures().length ? editingSource : recordsSource
  const extent = source.getExtent()
  if (extent && !isEmpty(extent)) map.getView().fit(extent, { padding: [60, 60, 70, 60], maxZoom: 17, duration: 200 })
}
function focus(id: string) {
  const feature = recordsSource.getFeatureById(id)
  if (feature?.getGeometry()) map?.getView().fit(feature.getGeometry()!.getExtent(), { padding: [60, 60, 70, 60], maxZoom: 18, duration: 200 })
}
function finish() { if (sketchVertices.value >= 3) draw?.finishDrawing() }
defineExpose({ fitAll, focus, finish })
watch(() => props.records, renderRecords, { deep: true })
watch(() => props.shape, renderShape, { deep: true })
watch(() => props.mode, interactions)
watch(() => props.selected, () => recordsLayer.changed(), { deep: true })
const recordsLayer = new VectorLayer({ source: recordsSource, style: feature => style(feature as Feature<Geometry>), declutter: false })
onMounted(() => {
  const satellite = new XYZ({ url: '/api/map/tiles/jlyh/{z}/{x}/{y}', maxZoom: 18, attributions: '卫星影像：吉林一号' })
  const labels = new XYZ({ url: '/api/map/tiles/labels/{z}/{x}/{y}', maxZoom: 18, attributions: '地名标注：天地图' })
  satellite.on('tileloaderror', () => { missingTiles.value = true })
  satellite.on('tileloadend', () => { missingTiles.value = false })
  labels.on('tileloaderror', () => { missingLabels.value = true })
  labels.on('tileloadend', () => { missingLabels.value = false })
  map = new Map({ target: host.value, layers: [new TileLayer({ source: satellite }), new TileLayer({ source: labels }), recordsLayer, new VectorLayer({ source: editingSource, style: feature => style(feature as Feature<Geometry>, true) })], view: new View({ center: fromLonLat([88.893426, 29.274331]), zoom: 16, minZoom: 3, maxZoom: 21 }), controls: defaultControls({ rotate: false, attribution: false }).extend([new ScaleLine(), new Attribution({ collapsible: true, collapsed: true })]) })
  map.on('singleclick', event => {
    if (props.mode === 'point') {
      const feature = new Feature(new Point(event.coordinate)); editingSource.clear(); editingSource.addFeature(feature); report(feature)
    } else if (props.mode === 'view') {
      const ids: string[] = []
      map!.forEachFeatureAtPixel(event.pixel, feature => { const id = String(feature.getId()); if (!ids.includes(id)) ids.push(id) }, { layerFilter: layer => layer === recordsLayer, hitTolerance: 10 })
      emit('select', ids)
    }
  })
  map.on('pointermove', event => { if (host.value) host.value.style.cursor = props.mode === 'view' ? (map!.hasFeatureAtPixel(event.pixel, { layerFilter: layer => layer === recordsLayer }) ? 'pointer' : '') : 'crosshair' })
  renderRecords(); renderShape(); interactions()
  if (props.shape) fitAll()
  observer = new ResizeObserver(() => map?.updateSize()); if (host.value) observer.observe(host.value)
})
onBeforeUnmount(() => { observer?.disconnect(); map?.setTarget(undefined); map?.dispose() })
</script>
<template>
  <div class="simple-map"><div ref="host" class="simple-map-canvas" aria-label="吉林一号地图" /><div v-if="missingTiles || missingLabels" class="map-cache-hint" role="status">{{ missingTiles ? '部分底图暂不可用' : '部分地名标注暂不可用' }}</div><button type="button" class="map-fit" aria-label="查看全部位置" title="查看全部位置" @click="fitAll">⌖</button></div>
</template>
<style>
.simple-map{position:relative;width:100%;height:100%;min-height:220px;background:var(--surface-2);overflow:hidden}.simple-map-canvas{position:absolute;inset:0}.simple-map .ol-zoom{top:auto;left:auto;right:12px;bottom:48px;background:var(--surface);box-shadow:0 2px 10px var(--border);border-radius:8px;padding:3px}.simple-map .ol-control button{background:var(--surface);color:var(--text);font-size:22px;width:32px;height:32px}.simple-map .ol-control button:hover{background:var(--surface-2)}.simple-map .ol-scale-line{bottom:12px;left:12px}.simple-map .ol-attribution{bottom:4px;right:4px;max-width:75%;font-size:10px}.map-fit{position:absolute;right:12px;bottom:128px;width:38px;height:38px;border-radius:8px;background:white;color:var(--text);font-size:26px;box-shadow:0 2px 10px var(--border);cursor:pointer}.map-cache-hint{position:absolute;bottom:12px;left:65px;right:60px;margin:auto;width:fit-content;max-width:calc(100% - 130px);background:var(--warning-bg);color:var(--warning);padding:6px 10px;border-radius:6px;font-size:12px;pointer-events:none}
</style>
