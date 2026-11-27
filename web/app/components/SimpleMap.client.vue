<script setup lang="ts">
import Map from 'ol/Map'
import Overlay from 'ol/Overlay'
import Feature from 'ol/Feature'
import View from 'ol/View'
import Point from 'ol/geom/Point'
import Polygon from 'ol/geom/Polygon'
import VectorSource from 'ol/source/Vector'
import VectorLayer from 'ol/layer/Vector'
import TileLayer from 'ol/layer/Tile'
import VectorTileLayer from 'ol/layer/VectorTile'
import VectorTileSource from 'ol/source/VectorTile'
import MVT from 'ol/format/MVT'
import type VectorTile from 'ol/VectorTile'
import type RenderFeature from 'ol/render/Feature'
import type BaseLayer from 'ol/layer/Base'
import XYZ from 'ol/source/XYZ'
import { createXYZ } from 'ol/tilegrid'
import TileState from 'ol/TileState'
type MapArchive = { tileType: 'mvt' | 'raster' | null; minZoom: number; maxZoom: number; version: string; error?: string | null }
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
import { polygonVertexCount, removePolygonVertex } from '~/utils/polygonVertices'
import { gcj02ToWgs84, wgs84ToGcj02 } from '~/utils/gcj02'
import { DEFAULT_TILE_PRELOAD, MAX_TILE_ZOOM, installDeepRasterTiles, makeDeepTilePrefetch } from '~/utils/deepTiles'
import 'ol/ol.css'

const props = withDefaults(defineProps<{ records?: MapRecord[]; shape?: GeoShape | null; mode?: 'view' | 'point' | 'polygon' | 'edit'; selected?: string[]; showZoomControls?: boolean }>(), { records: () => [], shape: null, mode: 'view', selected: () => [], showZoomControls: true })
const emit = defineEmits<{ select: [ids: string[]]; change: [shape: GeoShape]; drawing: [active: boolean] }>()
const host = ref<HTMLElement>()
const locateError = ref('')
let layerRequest: AbortController | undefined
const layerProblems = ref<Record<string, string>>({})
const baseLayers: BaseLayer[] = []
const baseCleanups: Array<() => void> = []
let retryLayers: () => void = () => {}
const sketchVertices = ref(0)
const rotation = ref(0)
const selectedVertex = ref<number | null>(null)
const vertexCount = ref(0)
const vertexPopup = ref<HTMLElement>()
const isRotated = computed(() => Math.abs(Math.sin(rotation.value / 2)) > 0.01)
let map: Map | undefined, draw: Draw | undefined, modify: Modify | undefined, observer: ResizeObserver | undefined, vertexOverlay: Overlay | undefined
const depthPrefetchers: Array<{ schedule: () => void; dispose: () => void }> = []
function currentMapExtent() {
  const size = map?.getSize()
  return map && size ? map.getView().calculateExtent(size) : undefined
}
function scheduleDepthPrefetch() {
  depthPrefetchers.forEach(item => item.schedule())
}
const recordsSource = new VectorSource<Feature<Geometry>>(), editingSource = new VectorSource<Feature<Geometry>>()
const verticesSource = new VectorSource<Feature<Geometry>>()
const vertexAnchors: Overlay[] = []
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
function editingPolygon() {
  const feature = editingSource.getFeatures().find(item => item.getGeometry() instanceof Polygon)
  const polygon = feature?.getGeometry()
  return polygon instanceof Polygon ? { feature: feature!, polygon } : null
}
function closestVertex(pixel: readonly number[]): number | null {
  if (props.mode !== 'edit' || !map) return null
  const edit = editingPolygon()
  if (!edit) return null
  const ring = edit.polygon.getCoordinates()[0] ?? []
  let nearest: number | null = null
  let squaredDistance = 18 * 18
  for (let i = 0; i < polygonVertexCount(ring); i++) {
    const screen = map.getPixelFromCoordinate(ring[i]!)
    const distance = (screen[0]! - pixel[0]!) ** 2 + (screen[1]! - pixel[1]!) ** 2
    if (distance <= squaredDistance) { nearest = i; squaredDistance = distance }
  }
  return nearest
}
function refreshVertices() {
  // Noninteractive anchors follow the vertices through pan/zoom. Besides
  // providing precise hit-position diagnostics, they never intercept pointer
  // events or interfere with OpenLayers Modify dragging.
  if (map) for (const anchor of vertexAnchors) map.removeOverlay(anchor)
  vertexAnchors.length = 0
  verticesSource.clear()
  vertexCount.value = 0
  const edit = props.mode === 'edit' ? editingPolygon() : null
  const ring = edit?.polygon.getCoordinates()[0]
  if (!ring) { selectedVertex.value = null; vertexOverlay?.setPosition(undefined); return }
  vertexCount.value = polygonVertexCount(ring)
  for (let i = 0; i < vertexCount.value; i++) {
    verticesSource.addFeature(new Feature({ geometry: new Point([...ring[i]!]), vertexIndex: i }))
    if (map) {
      const marker = document.createElement('span')
      marker.className = 'map-vertex-anchor'
      marker.dataset.testid = 'polygon-vertex-anchor'
      marker.dataset.vertexIndex = String(i)
      marker.setAttribute('aria-hidden', 'true')
      const anchor = new Overlay({ element: marker, positioning: 'center-center', stopEvent: false })
      map.addOverlay(anchor)
      anchor.setPosition(ring[i]!)
      vertexAnchors.push(anchor)
    }
  }
  if (selectedVertex.value !== null && selectedVertex.value < vertexCount.value)
    vertexOverlay?.setPosition(ring[selectedVertex.value]!)
  else { selectedVertex.value = null; vertexOverlay?.setPosition(undefined) }
}
function selectVertex(index: number | null) {
  const ring = editingPolygon()?.polygon.getCoordinates()[0]
  selectedVertex.value = ring && index !== null && index >= 0 && index < polygonVertexCount(ring) ? index : null
  vertexOverlay?.setPosition(selectedVertex.value === null ? undefined : ring![selectedVertex.value]!)
  vertexLayer.changed()
}
let vertexPointerStart: [number, number] | null = null
function vertexPointerDown(event: PointerEvent) {
  const target = event.target
  if (props.mode !== 'edit' || event.button !== 0 ||
    event.altKey || event.ctrlKey || event.metaKey || event.shiftKey ||
    (target instanceof Element && target.closest('.ol-overlaycontainer-stopevent'))) {
    vertexPointerStart = null
    return
  }
  vertexPointerStart = [event.clientX, event.clientY]
}
function vertexPointerUp(event: PointerEvent) {
  const start = vertexPointerStart
  vertexPointerStart = null
  if (!start || props.mode !== 'edit' || event.button !== 0 || !host.value ||
    (event.clientX - start[0]) ** 2 + (event.clientY - start[1]) ** 2 > 36) return
  const bounds = host.value.getBoundingClientRect()
  selectVertex(closestVertex([event.clientX - bounds.left, event.clientY - bounds.top]))
}
function deleteSelectedVertex() {
  const edit = editingPolygon()
  if (props.mode !== 'edit' || selectedVertex.value === null || !edit) return
  const next = removePolygonVertex(edit.polygon.getCoordinates()[0] ?? [], selectedVertex.value)
  if (!next) return
  // Never delete a vertex by directly mutating props or the saved DB shape.
  // Only the updated, closed polygon is emitted through the normal editor flow.
  selectVertex(null)
  edit.polygon.setCoordinates([next])
  refreshVertices()
  report(edit.feature)
}
function handleVertexKeydown(event: KeyboardEvent) {
  if (event.key !== 'Delete' || selectedVertex.value === null || props.mode !== 'edit') return
  const target = event.target
  if (target instanceof HTMLElement &&
    (target.isContentEditable || target.closest('input,textarea,select,[contenteditable],[role="combobox"]'))) return
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
  event.preventDefault()
  deleteSelectedVertex()
}
function vertexStyle(feature: Feature<Geometry>) {
  const active = feature.get('vertexIndex') === selectedVertex.value
  const point = new Style({
    image: new CircleStyle({
      radius: active ? 8 : 7,
      fill: new Fill({ color: active ? mapColors.danger : mapColors.halo }),
      stroke: new Stroke({ color: active ? mapColors.danger : mapColors.editing, width: 3 }),
    }),
    zIndex: active ? 10 : 1,
  })
  return active
    ? [new Style({ image: new CircleStyle({ radius: 16, stroke: new Stroke({ color: mapColors.danger, width: 3, lineDash: [4, 4] }) }), zIndex: 9 }), point]
    : [point]
}
const vertexLayer = new VectorLayer({ source: verticesSource, style: feature => vertexStyle(feature as Feature<Geometry>), declutter: false })

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
  selectedVertex.value = null
  if (props.shape) editingSource.addFeature(new Feature(geometry(props.shape)))
  refreshVertices()
}
function report(feature: Feature<Geometry>) {
  reporting = true; emit('change', toShape(feature)); void nextTick(() => { reporting = false })
}
function interactions() {
  if (!map) return
  if (draw) map.removeInteraction(draw)
  if (modify) map.removeInteraction(modify)
  draw = undefined; modify = undefined; sketchVertices.value = 0; emit('drawing', false)
  selectedVertex.value = null
  refreshVertices()
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
    modify = new Modify({
      source: editingSource,
      condition: event => primaryAction(event) && !(event.originalEvent instanceof PointerEvent && event.originalEvent.ctrlKey),
      // Remove vertices only through the confirmed popup or Delete key: the
      // native Alt+click shortcut can otherwise create invalid triangles.
      deleteCondition: () => false,
    })
    modify.on('modifyend', event => {
      refreshVertices()
      const feature = event.features.item(0)
      if (feature) report(feature)
    })
    map.addInteraction(modify)
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
    layers: [recordsLayer, new VectorLayer({ source: editingSource, style: feature => style(feature as Feature<Geometry>, true) }), vertexLayer],
    view,
    controls: defaultControls({ rotate: false, attribution: false, zoom: props.showZoomControls }).extend([new ScaleLine()]),
    interactions: defaultInteractions().extend([new DragRotate({
      condition: event => event.originalEvent instanceof PointerEvent &&
        event.originalEvent.pointerType === 'mouse' && event.originalEvent.button === 0 &&
        event.originalEvent.ctrlKey && !event.originalEvent.altKey &&
        !event.originalEvent.shiftKey && !event.originalEvent.metaKey,
    })]),
  })
  if (vertexPopup.value) {
    vertexOverlay = new Overlay({ element: vertexPopup.value, positioning: 'bottom-center', offset: [0, -22], stopEvent: true })
    map.addOverlay(vertexOverlay)
  }
  document.addEventListener('keydown', handleVertexKeydown)
  // Modify consumes the synthetic OpenLayers singleclick when an editable
  // vertex is hit. Capture the native pointer-up instead, so taps select but
  // drags keep moving vertices as before.
  host.value?.addEventListener('pointerdown', vertexPointerDown, true)
  host.value?.addEventListener('pointerup', vertexPointerUp, true)
  map.on('moveend', () => {
    const coordinate = map?.getView().getCenter()
    if (!coordinate) return
    const [longitude, latitude] = wgs84ToGcj02(toLonLat(coordinate))
    try { localStorage.setItem(mapViewKey, JSON.stringify({ longitude, latitude, zoom: map!.getView().getZoom() ?? 16 })) } catch { /* Optional map preference. */ }
    scheduleDepthPrefetch()
  })
  function clearBaseLayers() {
    layerRequest?.abort()
    depthPrefetchers.splice(0).forEach(item => item.dispose())
    for (const layer of baseLayers.splice(0)) {
      try { map?.removeLayer(layer); layer.dispose() } catch { /* A failed renderer must not block teardown. */ }
    }
    baseCleanups.splice(0).forEach(release => release())
  }
  const zoom = () => map?.getView().getZoom() ?? 0
  const pixelRatio = () => map?.getPixelRatio() ?? 1
  const defaultVectorStyle = new Style({
    fill: new Fill({ color: 'rgba(40,123,164,0.22)' }),
    stroke: new Stroke({ color: mapColors.vectorStroke, width: 1.3 }),
    image: new CircleStyle({ radius: 3, fill: new Fill({ color: mapColors.vectorStroke }) }),
  })
  function basicVectorLayer(url: string, signal: AbortSignal, header?: MapArchive) {
    const format = new MVT()
    const source = new VectorTileSource({
      format,
      tileGrid: createXYZ({ minZoom: header?.minZoom ?? 0, maxZoom: header?.maxZoom ?? MAX_TILE_ZOOM, tileSize: 512 }),
      tileUrlFunction: coordinate => coordinate ? coordinate.join('/') : undefined,
      tileLoadFunction: tile => {
        const vectorTile = tile as VectorTile<RenderFeature>
        vectorTile.setLoader((extent, _resolution, projection) => {
          const coordinate = vectorTile.getTileCoord()
          const [z, x, y] = coordinate
          const load = async () => {
            const response = await fetch(url.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y)), { signal })
            if (response.status === 404) return undefined
            if (!response.ok) throw new Error('Vector tile HTTP ' + response.status)
            return await response.arrayBuffer()
          }
          void load().then(data => {
            if (signal.aborted) return
            vectorTile.setFeatures(data ? format.readFeatures(data, { extent, featureProjection: projection }) : [])
          }).catch(() => { if (!signal.aborted) vectorTile.setState(TileState.ERROR) })
        })
      },
    })
    return new VectorTileLayer({ source, style: feature => {
      const name = feature.get('name:zh') || feature.get('name')
      // Basic rendering keeps all geometry visible, including unlabeled
      // polygons and lines. Place labels are optional, not a prerequisite.
      if (feature.getGeometry()?.getType() !== 'Point' || typeof name !== 'string' || !name.trim()) return defaultVectorStyle
      return new Style({
        image: defaultVectorStyle.getImage() ?? undefined,
        text: new Text({ text: name, font: '12px sans-serif', offsetY: -10,
          fill: new Fill({ color: mapColors.vectorLabel }), stroke: new Stroke({ color: mapColors.halo, width: 3 }) }),
      })
    }, declutter: true })
  }
  async function loadBaseLayers() {
    clearBaseLayers()
    layerProblems.value = {}
    const request = new AbortController()
    layerRequest = request
    const { signal } = request
    const target = map
    const active = () => !signal.aborted && !!target && map === target
    const problem = (id: string, message: string) => {
      if (active()) layerProblems.value = { ...layerProblems.value, [id]: message }
    }
    const registerPrefetch = (options: Parameters<typeof makeDeepTilePrefetch>[0]) => {
      if (!active()) return
      const prefetch = makeDeepTilePrefetch(options)
      depthPrefetchers.push(prefetch)
      prefetch.schedule()
    }
    function install(layer: BaseLayer, order: number) {
      if (!active()) { layer.dispose(); return false }
      // Every layer installs independently; negative z-indices keep the
      // configured bottom-to-top order and all record/edit overlays above.
      layer.setZIndex(order)
      try {
        target!.addLayer(layer)
        baseLayers.push(layer)
        target!.render()
        return true
      } catch (error) {
        try { target!.removeLayer(layer); layer.dispose() } catch { /* renderer failed */ }
        throw error
      }
    }
    async function vectorLayer(id: string, url: string, styleUrl: string | null | undefined,
      order: number, header?: MapArchive) {
      const fallback = () => install(basicVectorLayer(url, signal, header), order)
      if (!styleUrl) { fallback(); return }
      let styled: Awaited<ReturnType<typeof import('~/utils/mapStyle.client')['externalVectorStyle']>> | undefined
      try {
        const { externalVectorStyle } = await import('~/utils/mapStyle.client')
        styled = await externalVectorStyle(styleUrl, signal)
        if (!active()) { styled.release(); styled.layer.dispose(); return }
        const item = styled
        if (!install(item.layer, order)) { item.release(); return }
        baseCleanups.push(item.release)
        let replaced = false
        item.layer.mapLibreMap?.on('error', event => {
          if (!active() || replaced || item.layer.loaded) return
          replaced = true
          // Style or WebGL initialization errors should not leave a blank
          // vector map. Defer removal until the MapLibre event has returned.
          queueMicrotask(() => {
            if (!active()) return
            try {
              target!.removeLayer(item.layer)
              const index = baseLayers.indexOf(item.layer)
              if (index >= 0) baseLayers.splice(index, 1)
              item.release()
              fallback()
            } catch (error) { problem(id, String(error)) }
          })
          console.warn('Vector style initialization failed; using basic rendering', id, event.error)
        })
      } catch (error) {
        styled?.release()
        if (active()) {
          console.warn('Vector style unavailable; using basic rendering', id, error)
          fallback()
        }
      }
    }
    try {
      const layers = await $fetch<{ id: string; name?: string; kind?: 'raster' | 'mvt' | 'pmtiles'; preload?: number; styleUrl?: string | null; archive?: MapArchive | null }[]>('/api/map/layers', { signal, retry: 1 })
      if (!active()) return
      await Promise.allSettled(layers.map(async (layer, index) => {
        const url = '/api/map/tiles/' + encodeURIComponent(layer.id) + '/{z}/{x}/{y}?cache=server&v=' + encodeURIComponent(layer.archive?.version ?? '')
        const order = index - layers.length
        const depth = Number.isInteger(layer.preload) ? Math.max(0, Math.min(3, layer.preload!)) : 0
        try {
          const archive = layer.archive ?? undefined
          if (layer.kind === 'pmtiles' && (!archive || archive.error || !archive.tileType))
            throw new Error(archive?.error ?? 'PMTiles 元数据不可用')
          const maxZoom = archive?.maxZoom ?? MAX_TILE_ZOOM
          if ((archive?.tileType ?? layer.kind) === 'mvt') {
            await vectorLayer(layer.id, url, layer.styleUrl, order, archive)
            if (depth) {
              const grid = createXYZ({ minZoom: archive?.minZoom ?? 0, maxZoom })
              registerPrefetch({
                depth, maxZoom, getZoom: zoom, getPixelRatio: pixelRatio, getRenderedDepth: () => 0, getExtent: currentMapExtent,
                tileRange: (extent, z) => grid.getTileRangeForExtentAndZ(extent, z),
                fetchTile: async (z, x, y, prefetchSignal) => {
                  const response = await fetch(url.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y)), { cache: 'force-cache', signal: prefetchSignal })
                  if (!response.ok) throw new Error('PBF prefetch failed')
                  await response.arrayBuffer()
                },
              })
            }
            return
          }
          const source = new XYZ({ url, minZoom: archive?.minZoom ?? 0, maxZoom })
          installDeepRasterTiles(source, { depth, maxZoom, getZoom: zoom, getPixelRatio: pixelRatio })
          if (depth) {
            const grid = source.getTileGrid()
            if (grid) registerPrefetch({
              depth, maxZoom, getZoom: zoom, getPixelRatio: pixelRatio, getExtent: currentMapExtent,
              tileRange: (extent, z) => grid.getTileRangeForExtentAndZ(extent, z),
              fetchTile: async (z, x, y, prefetchSignal) => {
                const response = await fetch(url.replace('{z}', String(z)).replace('{x}', String(x)).replace('{y}', String(y)), { cache: 'force-cache', signal: prefetchSignal })
                if (!response.ok) throw new Error('Raster prefetch failed')
                await response.blob()
              },
            })
          }
          install(new TileLayer({ source, preload: DEFAULT_TILE_PRELOAD }), order)
        } catch (error) {
          problem(layer.id, (layer.name || layer.id) + ': ' + String(error))
        }
      }))
    } catch (error) { problem('layers', String(error)) }
  }
  retryLayers = () => { void loadBaseLayers() }
  void loadBaseLayers()
  map.on('singleclick', event => {
    if (props.mode === 'point') {
      const feature = new Feature(new Point(event.coordinate)); editingSource.clear(); editingSource.addFeature(feature); report(feature)
    } else if (props.mode === 'view') {
      const ids: string[] = []
      map!.forEachFeatureAtPixel(event.pixel, feature => { const id = String(feature.getId()); if (!ids.includes(id)) ids.push(id) }, { layerFilter: layer => layer === recordsLayer, hitTolerance: 10 })
      emit('select', ids)
    }
  })
  map.on('pointermove', event => {
    if (!host.value) return
    host.value.style.cursor = props.mode === 'view'
      ? (map!.hasFeatureAtPixel(event.pixel, { layerFilter: layer => layer === recordsLayer }) ? 'pointer' : '')
      : props.mode === 'edit' && closestVertex(event.pixel) !== null ? 'pointer' : 'crosshair'
  })
  renderRecords(); renderShape(); interactions()
  if (props.shape) fitAll()
  observer = new ResizeObserver(() => map?.updateSize()); if (host.value) observer.observe(host.value)
})
onBeforeUnmount(() => {
  document.removeEventListener('keydown', handleVertexKeydown)
  host.value?.removeEventListener('pointerdown', vertexPointerDown, true)
  host.value?.removeEventListener('pointerup', vertexPointerUp, true)
  if (map) for (const anchor of vertexAnchors) map.removeOverlay(anchor)
  vertexAnchors.length = 0
  layerRequest?.abort(); depthPrefetchers.splice(0).forEach(item => item.dispose())
  for (const layer of baseLayers.splice(0)) {
    try { map?.removeLayer(layer); layer.dispose() } catch { /* best effort renderer cleanup */ }
  }
  baseCleanups.splice(0).forEach(release => release())
  observer?.disconnect(); map?.setTarget(undefined); map?.dispose(); map = undefined
})
</script>
<template>
  <div class="simple-map" :class="{ 'no-zoom-controls': !showZoomControls }"><div ref="host" class="simple-map-canvas" aria-label="地图，按住 Ctrl 并拖动鼠标可以旋转" title="按住 Ctrl 并拖动鼠标可以旋转地图" />
    <div ref="vertexPopup" v-show="mode === 'edit' && selectedVertex !== null" class="map-vertex-popup" data-testid="polygon-vertex-popup">
      <span class="map-vertex-selected">已选中顶点 {{ selectedVertex === null ? '' : selectedVertex + 1 }} / {{ vertexCount }}</span>
      <button type="button" class="map-vertex-delete" aria-label="删除顶点" :disabled="vertexCount <= 3" @click.stop="deleteSelectedVertex"><UIcon name="i-lucide-trash-2" aria-hidden="true" /> 删除顶点</button>
      <span class="map-vertex-tip">{{ vertexCount <= 3 ? '至少保留 3 个顶点' : '也可按 Delete 删除' }}</span>
    </div>
    <div v-if="Object.keys(layerProblems).length" class="map-layer-hint" role="status" :title="Object.values(layerProblems).join('\n')"><span>{{ Object.keys(layerProblems).length }} 个图层加载失败</span><button type="button" @click="retryLayers">重试</button></div>
    <div v-if="locateError" class="map-location-hint" role="status">{{ locateError }}</div><button v-if="isRotated" type="button" class="map-north" aria-label="恢复正北方向" title="恢复正北方向" @click="resetNorth">N ↑</button><button type="button" class="map-locate" aria-label="定位当前位置" title="定位当前位置" @click="locateCurrent">◎</button><button type="button" class="map-fit" aria-label="查看全部位置" title="查看全部位置" @click="fitAll">⌖</button></div>
</template>
<style>
.map-layer-hint{position:absolute;left:12px;bottom:44px;max-width:calc(100% - 80px);display:flex;align-items:center;gap:8px;background:var(--surface);padding:4px 9px;border-radius:6px;font-size:12px;color:var(--text-2);z-index:2}.map-layer-hint button{color:var(--brand-600);text-decoration:underline;cursor:pointer}
.map-vertex-anchor{display:block;width:1px;height:1px;pointer-events:none}
.map-vertex-popup{display:flex;flex-direction:column;gap:7px;align-items:center;padding:9px 12px;border-radius:10px;background:var(--surface);color:var(--text);box-shadow:0 3px 18px var(--border-strong);min-width:158px;pointer-events:auto;white-space:nowrap}
.map-vertex-selected{font-weight:600;font-size:12px}
.map-vertex-delete{display:flex;align-items:center;justify-content:center;gap:8px;border-radius:7px;padding:7px 12px;background:var(--surface-2);font-weight:500;cursor:pointer}
.map-vertex-delete:disabled{opacity:.45;cursor:not-allowed}
.map-vertex-tip{font-size:11px;color:var(--text-3)}
.simple-map.no-zoom-controls .map-fit{bottom:48px}
.simple-map.no-zoom-controls .map-locate{bottom:94px}
.simple-map.no-zoom-controls .map-north{bottom:140px}
.simple-map{position:relative;width:100%;height:100%;min-height:220px;background:var(--surface-2);overflow:hidden}.simple-map-canvas{position:absolute;inset:0}.simple-map .ol-zoom{top:auto;left:auto;right:12px;bottom:48px;background:var(--surface);box-shadow:0 2px 10px var(--border);border-radius:8px;padding:3px}.simple-map .ol-control button{background:var(--surface);color:var(--text);font-size:22px;width:32px;height:32px}.simple-map .ol-control button:hover{background:var(--surface-2)}.simple-map .ol-scale-line{bottom:12px;left:12px}.map-north{position:absolute;right:12px;bottom:220px;width:38px;height:38px;border-radius:8px;background:var(--surface);color:var(--text);font-size:14px;font-weight:700;box-shadow:0 2px 10px var(--border);cursor:pointer}.map-locate{position:absolute;right:12px;bottom:174px;width:38px;height:38px;border-radius:8px;background:white;color:var(--text);font-size:24px;box-shadow:0 2px 10px var(--border);cursor:pointer}.map-location-hint{position:absolute;left:12px;bottom:12px;max-width:calc(100% - 78px);background:var(--surface);padding:6px 9px;border-radius:6px;font-size:12px;z-index:2}.map-fit{position:absolute;right:12px;bottom:128px;width:38px;height:38px;border-radius:8px;background:white;color:var(--text);font-size:26px;box-shadow:0 2px 10px var(--border);cursor:pointer}
</style>
