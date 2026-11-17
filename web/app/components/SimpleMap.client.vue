<script setup lang="ts">
import Map from 'ol/Map'
import View from 'ol/View'
import Feature, { type FeatureLike } from 'ol/Feature'
import Point from 'ol/geom/Point'
import Polygon from 'ol/geom/Polygon'
import VectorSource from 'ol/source/Vector'
import VectorLayer from 'ol/layer/Vector'
import TileLayer from 'ol/layer/Tile'
import XYZ from 'ol/source/XYZ'
import MVT from 'ol/format/MVT'
import VectorTileSource from 'ol/source/VectorTile'
import VectorTileLayer from 'ol/layer/VectorTile'
import TileState from 'ol/TileState'
import type VectorTile from 'ol/VectorTile'
import type ImageTile from 'ol/ImageTile'
import { PMTiles, TileType } from 'pmtiles'
import Draw from 'ol/interaction/Draw'
import Modify from 'ol/interaction/Modify'
import DragRotate from 'ol/interaction/DragRotate'
import { defaults as defaultInteractions } from 'ol/interaction/defaults'
import { primaryAction } from 'ol/events/condition'
import { defaults as defaultControls, ScaleLine } from 'ol/control'
import { fromLonLat, toLonLat } from 'ol/proj'
import { isEmpty } from 'ol/extent'
import { Style, Stroke, Fill, Circle as CircleStyle, Text } from 'ol/style'
import type { Geometry } from 'ol/geom'
import type { GeoShape } from '~/types/api'
import { mapColors, type MapRecord } from '~/utils/map'
import { gcj02ToWgs84, wgs84ToGcj02 } from '~/utils/gcj02'
import 'ol/ol.css'

const props = withDefaults(defineProps<{ records?: MapRecord[]; shape?: GeoShape | null; mode?: 'view' | 'point' | 'polygon' | 'edit'; selected?: string[] }>(), { records: () => [], shape: null, mode: 'view', selected: () => [] })
const emit = defineEmits<{ select: [ids: string[]]; change: [shape: GeoShape]; drawing: [active: boolean] }>()
const host = ref<HTMLElement>()
const locateError = ref('')
const layerRequest = new AbortController()
const sketchVertices = ref(0)
const rotation = ref(0)
const isRotated = computed(() => Math.abs(Math.sin(rotation.value / 2)) > 0.01)
let map: Map | undefined, draw: Draw | undefined, modify: Modify | undefined, observer: ResizeObserver | undefined
const recordsSource = new VectorSource<Feature<Geometry>>(), editingSource = new VectorSource<Feature<Geometry>>()
const mvtLabelCache = new globalThis.Map<string, Style>()
function mvtLabel(text: string, line: boolean): Style {
  const key = `${line ? 'line:' : 'point:'}${text}`
  let style = mvtLabelCache.get(key)
  if (!style) {
    if (mvtLabelCache.size > 1500) mvtLabelCache.clear()
    style = new Style({
      text: new Text({
        text,
        placement: line ? 'line' : 'point',
        font: '600 13px "Microsoft YaHei","Noto Sans CJK SC",sans-serif',
        fill: new Fill({ color: getComputedStyle(document.documentElement).getPropertyValue('--text').trim() || 'rgb(38,52,66)' }),
        stroke: new Stroke({ color: 'rgba(255,255,255,0.94)', width: 3 }),
        overflow: false,
      }),
    })
    mvtLabelCache.set(key, style)
  }
  return style
}
let reporting = false
function geometry(shape: GeoShape) { return shape.type === 'Point' ? new Point(fromLonLat(gcj02ToWgs84(shape.coordinates[0]!))) : new Polygon([shape.coordinates.map(c => fromLonLat(gcj02ToWgs84(c)))]) }
function toShape(feature: Feature<Geometry>): GeoShape {
  const item = feature.getGeometry()!
  const coordinates = item instanceof Point ? [wgs84ToGcj02(toLonLat(item.getCoordinates()))] : (item as Polygon).getCoordinates()[0]!.map(c => wgs84ToGcj02(toLonLat(c)))
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
  if (recordsSource.getFeatures().length) fitAll()
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
    modify = new Modify({ source: editingSource, condition: event => primaryAction(event) && !(event.originalEvent instanceof PointerEvent && event.originalEvent.ctrlKey) }); modify.on('modifyend', event => { const feature = event.features.item(0); if (feature) report(feature) }); map.addInteraction(modify)
  }
}
function fitAll() {
  if (!map) return
  const source = editingSource.getFeatures().length ? editingSource : recordsSource
  const extent = source.getExtent()
  if (extent && !isEmpty(extent)) map.getView().fit(extent, { padding: [60, 60, 70, 60], maxZoom: 17, duration: 200 })
}
// Persist the user's last map view in GCJ-02. No existing point coordinates change.
const mapViewKey = 'street-eco.last-map-view.v1'
function initialMapView() {
  try {
    const saved = JSON.parse(localStorage.getItem(mapViewKey) || 'null') as { longitude?: number; latitude?: number; zoom?: number } | null
    if (saved && typeof saved.longitude === 'number' && Number.isFinite(saved.longitude) &&
      typeof saved.latitude === 'number' && Number.isFinite(saved.latitude) &&
      Math.abs(saved.longitude) <= 180 && Math.abs(saved.latitude) <= 85 &&
      typeof saved.zoom === 'number' && saved.zoom >= 3 && saved.zoom <= 21)
      return { coordinate: [saved.longitude, saved.latitude], zoom: saved.zoom }
  } catch { /* Local storage may be unavailable in private browsing. */ }
  return { coordinate: [88.893426, 29.274331], zoom: 16 }
}
function resetNorth() { map?.getView().animate({ rotation: 0, duration: 250 }) }
function locateCurrent() {
  locateError.value = ''
  if (!navigator.geolocation) { locateError.value = '当前浏览器不支持定位'; return }
  navigator.geolocation.getCurrentPosition(
    position => { map?.getView().animate({ center: fromLonLat([position.coords.longitude, position.coords.latitude]), zoom: 17, duration: 300 }) },
    () => { locateError.value = '无法获取当前位置，请检查浏览器定位权限' },
    { enableHighAccuracy: true, timeout: 12000, maximumAge: 60000 },
  )
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
  const lastView = initialMapView()
  const view = new View({ center: fromLonLat(gcj02ToWgs84(lastView.coordinate)), zoom: lastView.zoom, minZoom: 3, maxZoom: 21 })
  view.on('change:rotation', () => { rotation.value = view.getRotation() })
  map = new Map({
    target: host.value,
    layers: [recordsLayer, new VectorLayer({ source: editingSource, style: feature => style(feature as Feature<Geometry>, true) })],
    view,
    controls: defaultControls({ rotate: false, attribution: false }).extend([new ScaleLine()]),
    interactions: defaultInteractions().extend([new DragRotate({
      condition: event => event.originalEvent instanceof PointerEvent &&
        event.originalEvent.pointerType === 'mouse' && event.originalEvent.button === 0 &&
        event.originalEvent.ctrlKey && !event.originalEvent.altKey &&
        !event.originalEvent.shiftKey && !event.originalEvent.metaKey,
    })]),
  })
  map.on('moveend', () => {
    const coordinate = map?.getView().getCenter()
    if (!coordinate) return
    const [longitude, latitude] = wgs84ToGcj02(toLonLat(coordinate))
    try { localStorage.setItem(mapViewKey, JSON.stringify({ longitude, latitude, zoom: map!.getView().getZoom() ?? 16 })) } catch { /* Optional map preference. */ }
  })
  const vectorLabels = (source: VectorTileSource) => new VectorTileLayer({
    source,
    declutter: true,
    style: feature => {
      const name = ['name:zh', 'name_zh', 'name_zh_cn', 'NAME_CHN', 'name', 'NAME', 'Name', 'label', 'text', 'name_en']
        .map(field => feature.get(field))
        .find(value => typeof value === 'string' && value.trim().length > 0 && value.length <= 80)
      if (typeof name !== 'string') return undefined
      const kind = 'getType' in feature ? feature.getType() : feature.getGeometry()?.getType()
      return mvtLabel(name.trim(), kind === 'LineString' || kind === 'MultiLineString')
    },
  })
  void $fetch<{ id: string; kind?: 'raster' | 'mvt' | 'pmtiles' }[]>('/api/map/layers', { signal: layerRequest.signal }).then(async layers => {
    const created = await Promise.all(layers.map(async layer => {
      const url = `/api/map/tiles/${encodeURIComponent(layer.id)}/{z}/{x}/{y}?cache=server`
      if (layer.kind === 'pmtiles') {
        try {
          const archive = new PMTiles(`/api/map/archive/${encodeURIComponent(layer.id)}`)
          const header = await archive.getHeader()
          const maxZoom = Math.min(header.maxZoom, 18)
          if (header.tileType === TileType.Mvt) {
            const source = new VectorTileSource({
              format: new MVT(),
              maxZoom,
              tileUrlFunction: coordinate => coordinate ? coordinate.join('/') : undefined,
              tileLoadFunction: tile => {
                const vectorTile = tile as VectorTile<FeatureLike>
                vectorTile.setLoader((extent, _resolution, projection) => {
                  const coord = vectorTile.getTileCoord()
                  if (!coord) { vectorTile.setFeatures([]); return }
                  void archive.getZxy(coord[0]!, coord[1]!, coord[2]!).then(result => {
                    if (!result) { vectorTile.setFeatures([]); return }
                    vectorTile.setFeatures(vectorTile.getFormat().readFeatures(result.data, { extent, featureProjection: projection }))
                  }).catch(() => vectorTile.setState(TileState.ERROR))
                })
              },
            })
            return vectorLabels(source)
          }
          const contentType = header.tileType === TileType.Png ? 'image/png'
            : header.tileType === TileType.Jpeg ? 'image/jpeg'
            : header.tileType === TileType.Webp ? 'image/webp' : null
          if (!contentType) return null
          const source = new XYZ({
            maxZoom,
            tileUrlFunction: coordinate => coordinate ? coordinate.join('/') : undefined,
            tileLoadFunction: tile => {
              const imageTile = tile as ImageTile
              const coordinate = imageTile.getTileCoord()
              if (!coordinate) { imageTile.setState(TileState.EMPTY); return }
              void archive.getZxy(coordinate[0]!, coordinate[1]!, coordinate[2]!).then(result => {
                if (!result) { imageTile.setState(TileState.EMPTY); return }
                const image = imageTile.getImage() as HTMLImageElement
                const imageUrl = URL.createObjectURL(new Blob([result.data], { type: contentType }))
                const cleanup = () => URL.revokeObjectURL(imageUrl)
                image.addEventListener('load', cleanup, { once: true })
                image.addEventListener('error', cleanup, { once: true })
                image.src = imageUrl
              }).catch(() => imageTile.setState(TileState.ERROR))
            },
          })
          return new TileLayer({ source })
        } catch { return null }
      }
      if (layer.kind === 'mvt')
        return vectorLabels(new VectorTileSource({ format: new MVT(), url, maxZoom: 18 }))
      return new TileLayer({ source: new XYZ({ url, maxZoom: 18 }) })
    }))
    if (layerRequest.signal.aborted || !map) return
    created.forEach((layer, index) => { if (layer) map!.getLayers().insertAt(index, layer) })
  }).catch(() => { /* No base layers is a supported offline state. */ })
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
onBeforeUnmount(() => { layerRequest.abort(); observer?.disconnect(); map?.setTarget(undefined); map?.dispose() })
</script>
<template>
  <div class="simple-map"><div ref="host" class="simple-map-canvas" aria-label="地图，按住 Ctrl 并拖动鼠标可以旋转" title="按住 Ctrl 并拖动鼠标可以旋转地图" /><div v-if="locateError" class="map-location-hint" role="status">{{ locateError }}</div><button v-if="isRotated" type="button" class="map-north" aria-label="恢复正北方向" title="恢复正北方向" @click="resetNorth">N ↑</button><button type="button" class="map-locate" aria-label="定位当前位置" title="定位当前位置" @click="locateCurrent">◎</button><button type="button" class="map-fit" aria-label="查看全部位置" title="查看全部位置" @click="fitAll">⌖</button></div>
</template>
<style>
.simple-map{position:relative;width:100%;height:100%;min-height:220px;background:var(--surface-2);overflow:hidden}.simple-map-canvas{position:absolute;inset:0}.simple-map .ol-zoom{top:auto;left:auto;right:12px;bottom:48px;background:var(--surface);box-shadow:0 2px 10px var(--border);border-radius:8px;padding:3px}.simple-map .ol-control button{background:var(--surface);color:var(--text);font-size:22px;width:32px;height:32px}.simple-map .ol-control button:hover{background:var(--surface-2)}.simple-map .ol-scale-line{bottom:12px;left:12px}.map-north{position:absolute;right:12px;bottom:220px;width:38px;height:38px;border-radius:8px;background:var(--surface);color:var(--text);font-size:14px;font-weight:700;box-shadow:0 2px 10px var(--border);cursor:pointer}.map-locate{position:absolute;right:12px;bottom:174px;width:38px;height:38px;border-radius:8px;background:white;color:var(--text);font-size:24px;box-shadow:0 2px 10px var(--border);cursor:pointer}.map-location-hint{position:absolute;left:12px;bottom:12px;max-width:calc(100% - 78px);background:var(--surface);padding:6px 9px;border-radius:6px;font-size:12px;z-index:2}.map-fit{position:absolute;right:12px;bottom:128px;width:38px;height:38px;border-radius:8px;background:white;color:var(--text);font-size:26px;box-shadow:0 2px 10px var(--border);cursor:pointer}
</style>
