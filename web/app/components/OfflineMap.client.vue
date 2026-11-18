<script setup lang="ts">
import OLMap from 'ol/Map'
import View from 'ol/View'
import { unByKey } from 'ol/Observable'
import Feature from 'ol/Feature'
import Point from 'ol/geom/Point'
import Polygon from 'ol/geom/Polygon'
import MultiPolygon from 'ol/geom/MultiPolygon'
import Geometry from 'ol/geom/Geometry'
import TileLayer from 'ol/layer/Tile'
import VectorTileLayer from 'ol/layer/VectorTile'
import VectorLayer from 'ol/layer/Vector'
import VectorSource from 'ol/source/Vector'
import XYZ from 'ol/source/XYZ'
import VectorTileSource from 'ol/source/VectorTile'
import MVT from 'ol/format/MVT'
import { createMvtStyle } from '~/utils/mvtStyle'
import { Draw, Modify, Snap, DragRotate, defaults as defaultInteractions } from 'ol/interaction'
import { fromLonLat, toLonLat } from 'ol/proj'
import { Fill, Stroke, Style, Circle, Text } from 'ol/style'
import { defaults as defaultControls } from 'ol/control'
import 'ol/ol.css'
import type { GeoShape, MapFeature, MapLayer } from '~/types/community'

const props = withDefaults(defineProps<{ features?: MapFeature[]; layers?: MapLayer[]; editable?: boolean; selectedId?: string; height?: string; immersive?: boolean; highDefinition?: boolean; focusPadding?: number[]; loadDepth?: number; exportFeatures?: MapFeature[] }>(), { features: () => [], layers: () => [], editable: true, height: '65vh' })
const shape = defineModel<GeoShape | null>({ default: null })
const emit = defineEmits<{ choose: [feature: MapFeature]; coordinate: [coordinate: number[]]; edit: [feature: MapFeature]; begin: []; ready: []; sketching: [active: boolean] }>()
const element = useTemplateRef<HTMLElement>('mapElement')
const mode = ref<'view' | 'Point' | 'Polygon' | 'edit'>('view')
const snapEnabled = ref(true), sketching = ref(false), vertices = ref(0)
const rotation = ref(0)
const isRotated = computed(() => Math.abs(Math.sin(rotation.value / 2)) > 0.01)
function resetNorth() { map?.getView().animate({ rotation: 0, duration: 250 }) }
watch(sketching, value => emit('sketching', value), { flush: 'sync' })
let map: OLMap | undefined, draw: Draw | undefined, modify: Modify | undefined, snaps: Snap[] = []
const source = new VectorSource<Feature<Geometry>>()
const editing = new VectorSource<Feature<Geometry>>()
const tiles = new Map<string, { layer: TileLayer<XYZ> | VectorTileLayer; signature: string }>()
let resizing: ResizeObserver | undefined
function geometry(value: GeoShape) { return value.type === 'Point' ? new Point(fromLonLat(value.coordinates[0]!)) : value.type === 'MultiPolygon' ? new MultiPolygon((value.areas ?? []).map(area => [area.map(x => fromLonLat(x))])) : new Polygon([value.coordinates.map(x => fromLonLat(x))]) }
function readShape(g: Geometry): GeoShape {
  const xy = (coordinate: number[]) => toLonLat(coordinate).map(x => Number(x.toFixed(8)))
  if (g instanceof Point) return { type: 'Point', coordinates: [xy(g.getCoordinates())] }
  if (g instanceof MultiPolygon) { const areas = g.getCoordinates().map(p => p[0]!.map(xy)); return { type: 'MultiPolygon', coordinates: areas[0]!, areas } }
  return { type: 'Polygon', coordinates: (g as Polygon).getCoordinates()[0]!.map(xy) }
}
function featureStyle(feature: { get: (key: string) => unknown }) {
  const item = feature.get('record') as MapFeature | undefined
  const scale = props.highDefinition ? 2 : 1
  const color=item?.color||(item?.kind==='grid'?'#64748b':'#087f70')
  const selected=props.selectedId===item?.id
  const base=new Style({zIndex:item?.kind==='grid'?0:10,fill:new Fill({color:`${color}${item?.kind==='grid'?'15':'30'}`}),stroke:new Stroke({color,width:(selected?4:2)*scale,lineDash:item?.kind==='grid'?[6*scale,5*scale]:undefined}),image:new Circle({radius:(selected?7:5)*scale,fill:new Fill({color}),stroke:new Stroke({color:'#fff',width:2*scale})})})
  const text=(value:string,offsetY:number,bold=false)=>new Style({zIndex:item?.kind==='grid'?1:11,text:new Text({text:value,font:`${bold?'600 ':''}${14*scale}px Microsoft YaHei,Segoe UI Emoji,sans-serif`,offsetY:offsetY*scale,fill:new Fill({color:'#17211e'}),stroke:new Stroke({color:'#fff',width:4*scale}),overflow:true,textAlign:'center'})})
  if(item?.kind==='grid'){const extent=geometry(item.shape).getExtent();const label=text(item.name,12,true);label.setGeometry(new Point([(extent[0]!+extent[2]!)/2,extent[3]!]));return [base,label]}
  const labels=[text(item?.name??'',-18,true)]
  for(const [index,use] of (item?.uses??[]).entries())labels.push(text(`${use.icon} ${use.purposeName}${use.name===use.purposeName?'':' · '+use.name}`,8+index*23))
  return [base,...labels]
}
function refreshFeatures() {
  source.clear()
  source.addFeatures(props.features.filter(x => !x.disabled).map(item => { const f = new Feature({ geometry: geometry(item.shape), record: item }); f.setId(`${item.kind}:${item.id}`); return f }))
}
function tileLayer(item: MapLayer) {
  const url = `/api/map/tiles/${encodeURIComponent(item.id)}/{z}/{x}/{y}?v=${item.cacheVersion ?? 0}`
  const zIndex = item.annotation ? 1000 + item.sortOrder : Math.min(90, item.sortOrder)
  return item.tileType === 'mvt'
    ? new VectorTileLayer({ source: new VectorTileSource({ url, format: new MVT(), minZoom: item.minZoom, maxZoom: item.maxZoom }), style: createMvtStyle(item.styleJson), opacity: item.opacity, zIndex, preload: props.loadDepth ?? 0 })
    : new TileLayer({ source: new XYZ({ url, minZoom: item.minZoom, maxZoom: item.maxZoom, transition: 0, crossOrigin: 'anonymous' }), opacity: item.opacity, zIndex, preload: props.loadDepth ?? 0 })
}
function refreshLayers() {
  if (!map) return
  const validIds = new Set(props.layers.map(layer => layer.id))
  for (const [id, entry] of tiles) {
    if (validIds.has(id)) continue
    map.removeLayer(entry.layer)
    tiles.delete(id)
  }
  for (const item of [...props.layers].sort((a, b) => a.sortOrder - b.sortOrder)) {
    // A visibility toggle must never reconstruct the tile source/cache.
    // Reconstruct only when the actual source/style configuration changes.
    const signature = JSON.stringify({
      folder: item.folder, scheme: item.scheme, sortOrder: item.sortOrder, opacity: item.opacity,
      minZoom: item.minZoom, maxZoom: item.maxZoom, annotation: item.annotation,
      tileType: item.tileType, styleJson: item.styleJson, cacheVersion: item.cacheVersion,
      loadDepth: props.loadDepth ?? 0,
    })
    const existing = tiles.get(item.id)
    if (existing?.signature !== signature) {
      if (existing) map.removeLayer(existing.layer)
      const layer = tileLayer(item)
      layer.setVisible(item.enabled)
      map.addLayer(layer)
      tiles.set(item.id, { layer, signature })
    } else existing.layer.setVisible(item.enabled)
  }
}
function clearInteractions() {
  sketching.value = false; vertices.value = 0
  if (!map) return
  if (draw) map.removeInteraction(draw); if (modify) map.removeInteraction(modify)
  snaps.forEach(x => map!.removeInteraction(x)); snaps = []; draw = undefined; modify = undefined
}
function addSnap() {
  if (!map || !snapEnabled.value) return
  for (const s of [source, editing]) { const snap = new Snap({ source: s, pixelTolerance: 14 }); snaps.push(snap); map.addInteraction(snap) }
}
function publishShape() {
  const values=editing.getFeatures().map(f=>readShape(f.getGeometry()!))
  if(values.length===1){shape.value=values[0]!;return}
  const areas=values.flatMap(value=>value.type==='MultiPolygon'?value.areas??[]:value.type==='Polygon'?[value.coordinates]:[])
  if(areas.length)shape.value={type:'MultiPolygon',coordinates:areas[0]!,areas}
}
function start(type: 'Point' | 'Polygon') {
  if (!map) return
  if ((shape.value || sketching.value) && !window.confirm('重新标记会放弃当前未保存的位置，继续吗？')) return
  emit('begin');clearInteractions();editing.clear();shape.value=null;mode.value=type
  draw=new Draw({source:editing,type})
  draw.on('drawstart', event => { sketching.value = true; vertices.value = 0; event.feature.getGeometry()?.on('change', e => { const g = e.target as Polygon; if (type === 'Polygon') vertices.value = Math.max(0, g.getCoordinates()[0]!.length - 2) }) })
  draw.on('drawabort', () => { sketching.value = false; vertices.value = 0 })
  draw.on('drawend',()=>{sketching.value=false;vertices.value=0;window.setTimeout(()=>{publishShape();if(type==='Point')startEdit()},0)})
  map.addInteraction(draw);addSnap()
}
function startEdit() {
  if (!map) return
  clearInteractions(); mode.value = 'edit'
  modify = new Modify({ source: editing, pixelTolerance: 14, condition: event => !(event.originalEvent instanceof PointerEvent && event.originalEvent.ctrlKey) })
  modify.on('modifyend', publishShape)
  map.addInteraction(modify); addSnap()
}
function cancel() { clearInteractions(); editing.clear(); shape.value = null; mode.value = 'view' }
function edit(value: GeoShape) { const copy = structuredClone(toRaw(value)); cancel(); shape.value = copy; editing.addFeature(new Feature(geometry(copy))); startEdit() }
function locate(lon: number, lat: number) { map?.getView().animate({ center: fromLonLat([lon, lat]), zoom: Math.max(map.getView().getZoom() ?? 16, 17), duration: 250 }) }
function focus(value: GeoShape) { if (!map) return; map.getView().fit(geometry(value).getExtent(), { padding: (props.focusPadding ?? [60,60,60,60]).map(v=>v*(props.highDefinition?2:1)), maxZoom: 18, duration: 200 }) }

function getExportSize(): [number, number] {
  const size = map?.getSize() ?? [0, 0]
  return [size[0] ?? 0, size[1] ?? 0]
}
function composite(target: HTMLCanvasElement, host: HTMLElement) {
  const context = target.getContext('2d')
  if (!context) throw new Error('无法创建地图渲染画布')
  context.fillStyle = '#e6e9e5'
  context.fillRect(0, 0, target.width, target.height)
  const canvases = host.querySelectorAll<HTMLCanvasElement>('.ol-layer canvas, canvas.ol-layer')
  for (const canvas of canvases) {
    if (!canvas.width || !canvas.height) continue
    const opacity = canvas.parentElement?.style.opacity || canvas.style.opacity || '1'
    context.globalAlpha = Number(opacity)
    const matrix = canvas.style.transform.match(/^matrix\(([^)]+)\)$/)
    if (matrix) {
      const values = matrix[1]!.split(',').map(Number)
      if (values.length === 6 && values.every(Number.isFinite))
        context.setTransform(values[0]!, values[1]!, values[2]!, values[3]!, values[4]!, values[5]!)
      else context.setTransform(1, 0, 0, 1, 0, 0)
    } else context.setTransform(target.width / canvas.width, 0, 0, target.height / canvas.height, 0, 0)
    context.drawImage(canvas, 0, 0)
  }
  context.setTransform(1, 0, 0, 1, 0, 0)
  context.globalAlpha = 1
}
async function exportImage(options: { scale: number; partitions: number; layerIds: string[]; markers: boolean; grids: boolean }) {
  if (!map) throw new Error('地图尚未加载')
  const [width, height] = getExportSize()
  const scale = options.scale, partitions = options.partitions
  if (!Number.isInteger(scale) || scale < 1 || scale > 8 || !Number.isInteger(partitions) || partitions < 1 || partitions > 8)
    throw new Error('倍率或分区参数不正确')
  const resultWidth = Math.round(width * scale), resultHeight = Math.round(height * scale)
  if (!width || !height || resultWidth * resultHeight > 100_000_000 || resultWidth > 32767 || resultHeight > 32767)
    throw new Error('导出分辨率过大')
  const result = document.createElement('canvas')
  result.width = resultWidth; result.height = resultHeight
  const context = result.getContext('2d')
  if (!context) throw new Error('无法创建地图导出画布')
  const liveMap = map
  const view = liveMap.getView()
  if (view.getAnimating() || view.getInteracting()) {
    // Exporting in the middle of Ctrl-rotation mixes view transforms between
    // partitions. Wait for the same settled frame to seed every partition.
    await new Promise<void>((resolve, reject) => {
      let key: ReturnType<typeof liveMap.once> | undefined
      const timeout = window.setTimeout(() => { if (key) unByKey(key); reject(new Error('地图仍在移动，请停止拖动后重试导出')) }, 10000)
      key = liveMap.once('moveend', () => { window.clearTimeout(timeout); resolve() })
    })
  }
  liveMap.renderSync()
  const resolution = view.getResolution(), rotation = view.getRotation()
  if (!resolution) throw new Error('地图分辨率不可用')
  // Freeze the actual OpenLayers pixel-to-coordinate affine transform once.
  // Sampling basis pixels avoids drift when the display is rotated, the
  // browser resizes, or animation completes between partition renders.
  const midX = width / 2, midY = height / 2
  const origin = liveMap.getCoordinateFromPixel([midX, midY])
  const unitX = liveMap.getCoordinateFromPixel([midX + 1, midY])
  const unitY = liveMap.getCoordinateFromPixel([midX, midY + 1])
  if (!origin || !unitX || !unitY) throw new Error('地图变换不可用')
  const mapCenterForPixel = (x: number, y: number): [number, number] => {
    const dx = x - midX, dy = y - midY
    return [
      origin[0]! + dx * (unitX[0]! - origin[0]!) + dy * (unitY[0]! - origin[0]!),
      origin[1]! + dx * (unitX[1]! - origin[1]!) + dy * (unitY[1]! - origin[1]!),
    ]
  }
  const selectedLayers = props.layers.filter(x => options.layerIds.includes(x.id)).sort((a, b) => a.sortOrder - b.sortOrder)
  const tileLoads = new Map(selectedLayers.map(layer => [layer.id, { success: 0, error: 0, requested: 0 }]))
  const selectedFeatures = (props.exportFeatures ?? props.features).filter(x => !x.disabled && (x.kind === 'grid' ? options.grids : options.markers))
  for (let row = 0; row < partitions; row++) {
    for (let column = 0; column < partitions; column++) {
      const x = Math.round(resultWidth * column / partitions), y = Math.round(resultHeight * row / partitions)
      const nextX = Math.round(resultWidth * (column + 1) / partitions), nextY = Math.round(resultHeight * (row + 1) / partitions)
      const widthPart = nextX - x, heightPart = nextY - y, bleed = 64
      // Each partition center derives from the *same* frozen viewport and
      // integer output pixel grid, not a potentially moving live map.
      const center = mapCenterForPixel((x + widthPart / 2) / scale, (y + heightPart / 2) / scale)
      const host = document.createElement('div')
      host.style.cssText = `position:fixed;left:-100000px;top:0;width:${widthPart + 2 * bleed}px;height:${heightPart + 2 * bleed}px;pointer-events:none`
      document.body.appendChild(host)
      let exportMap: OLMap | undefined
      try {
        const source = new VectorSource<Feature<Geometry>>()
        source.addFeatures(selectedFeatures.map(item => new Feature({ geometry: geometry(item.shape), record: item })))
        const exportTiles = selectedLayers.map(item => {
          const layer = tileLayer(item)
          const stats = tileLoads.get(item.id)!
          const tileSource = layer.getSource()
          tileSource?.on('tileloadstart', () => { stats.requested++ })
          tileSource?.on('tileloadend', () => { stats.success++ })
          tileSource?.on('tileloaderror', () => { stats.error++ })
          return layer
        })
        exportMap = new OLMap({
          target: host, pixelRatio: 1, controls: [], interactions: [],
          view: new View({ projection: view.getProjection(), center, resolution: resolution / scale, rotation }),
          layers: [...exportTiles, new VectorLayer({ source, style: featureStyle, zIndex: 100, declutter: false })],
        })
        const current = exportMap
        await new Promise<void>((resolve, reject) => {
          const timeout = window.setTimeout(() => reject(new Error('地图渲染超时')), 30000)
          current.once('rendercomplete', () => { window.clearTimeout(timeout); resolve() })
          current.renderSync()
        })
        const part = document.createElement('canvas')
        part.width = widthPart + bleed * 2; part.height = heightPart + bleed * 2
        composite(part, host)
        context.drawImage(part, bleed, bleed, widthPart, heightPart, x, y, widthPart, heightPart)
      } finally {
        exportMap?.setTarget(undefined)
        exportMap?.dispose()
        host.remove()
      }
    }
  }
  // rendercomplete is not proof that tiles loaded successfully. Explicitly
  // identify empty coverage, failed requests, and partial layers before save.
  const missing = selectedLayers.flatMap(item => {
    const stats = tileLoads.get(item.id)!
    if(stats.success===0)return [`${item.name}：未成功加载瓦片（请求 ${stats.requested}，失败 ${stats.error}）`]
    if(stats.error>0)return [`${item.name}：部分瓦片缺失（成功 ${stats.success}，失败 ${stats.error}）`]
    return []
  })
  if(missing.length&&!window.confirm(`底图不完整：\\n${missing.join('\\n')}\\n是否仍导出已成功渲染的部分？`))
    throw new Error('已取消缺失底图的导出，请检查图层目录或覆盖范围')
  const blob = await new Promise<Blob>((resolve, reject) => result.toBlob(value => value ? resolve(value) : reject(new Error('地图图片生成失败')), 'image/png'))
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  try {
    anchor.href = url; anchor.download = '社区地图.png'; document.body.appendChild(anchor); anchor.click()
  } finally { anchor.remove(); window.setTimeout(() => URL.revokeObjectURL(url), 1000) }
}
function bounds() { if (!map) return; const ext = map.getView().calculateExtent(map.getSize()); const sw = toLonLat([ext[0]!,ext[1]!]), ne = toLonLat([ext[2]!,ext[3]!]); return { west: Math.max(-180,sw[0]!), south: Math.max(-85,sw[1]!), east: Math.min(180,ne[0]!), north: Math.min(85,ne[1]!), zoom: Math.round(map.getView().getZoom() ?? 16) } }

defineExpose({ start, cancel, edit, locate, focus, bounds, getExportSize, exportImage })
watch(() => props.features, refreshFeatures, { deep: true })
watch(() => props.layers, refreshLayers, { deep: true })
watch(() => props.loadDepth, refreshLayers)
watch(() => props.selectedId, () => source.changed())
watch(()=>props.highDefinition,async(value,previous)=>{if(!map||value===previous)return;const zoom=map.getView().getZoom()??16;await nextTick();map.updateSize();map.getView().setZoom(zoom+(value?1:-1));source.changed()})
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
    controls: defaultControls({ attribution: false, rotate: false }),
    interactions: defaultInteractions({ altShiftDragRotate: false }).extend([new DragRotate({
      condition: event => event.originalEvent instanceof PointerEvent &&
        event.originalEvent.pointerType === 'mouse' && event.originalEvent.button === 0 &&
        event.originalEvent.ctrlKey && !event.originalEvent.altKey &&
        !event.originalEvent.shiftKey && !event.originalEvent.metaKey,
    })]),
    layers: [new VectorLayer({ source, style: featureStyle, zIndex: 100 }), new VectorLayer({ source: editing, zIndex: 101, style: editStyle })]
  })
  map.getView().on('change:rotation', () => { rotation.value = map?.getView().getRotation() ?? 0 })
  map.on('singleclick', event => {
    if (mode.value !== 'view') return
    const f = map!.forEachFeatureAtPixel(event.pixel, f => f.get('record') ? f : undefined, { hitTolerance: 8 })
    const record = f?.get('record') as MapFeature | undefined
    if (record) emit('choose', record)
    emit('coordinate', toLonLat(event.coordinate))
  })
  map.on('dblclick',event=>{
    if(mode.value!=='view')return
    const feature=map!.forEachFeatureAtPixel(event.pixel,f=>{const record=f.get('record') as MapFeature|undefined;return record&&record.shape.type!=='Point'?f:undefined},{hitTolerance:8})
    const record=feature?.get('record') as MapFeature|undefined
    if(record){event.preventDefault();emit('edit',record)}
  })
  refreshFeatures(); refreshLayers()
  emit('ready')
  if (!props.editable && props.features.length === 1) focus(props.features[0]!.shape)
  resizing = new ResizeObserver(() => map?.updateSize()); if (element.value) resizing.observe(element.value)
}, { flush: 'post' })
onBeforeUnmount(() => { resizing?.disconnect(); map?.setTarget(undefined); map?.dispose() })
</script>
<template>
  <div class="offline-map-wrap" :class="{immersive,hd:highDefinition}">
    <div v-if="editable" class="map-tools">
      <UButton label="标点" icon="i-lucide-map-pin" :variant="mode === 'Point' ? 'solid' : 'outline'" :disabled="mode==='Point'" @click="start('Point')" />
      <UButton label="画区域" icon="i-lucide-square" :variant="mode === 'Polygon' ? 'solid' : 'outline'" :disabled="mode==='Polygon'" @click="start('Polygon')" />
      <template v-if="mode !== 'view'">
        <UButton v-if="mode === 'Polygon'" label="撤回一点" color="neutral" variant="outline" :disabled="!sketching" @click="draw?.removeLastPoint()" />
        <UButton v-if="mode === 'Polygon'" label="结束当前区域" color="neutral" variant="outline" :disabled="vertices < 3" @click="draw?.finishDrawing()" />
        <UButton label="放弃标记" color="neutral" variant="ghost" @click="cancel" />
        <UCheckbox v-model="snapEnabled" label="吸附边界" />
      </template>
      <span v-if="mode === 'edit'" class="text-sm text-muted">{{ shape?.type === 'MultiPolygon' ? `${shape.areas?.length} 个区域 · ` : '' }}拖动节点调整</span>
    </div>
    <div ref="mapElement" class="offline-map" :style="{ height }" tabindex="0" aria-label="社区地图（Ctrl + 拖动旋转）" />
    <button v-if="isRotated" class="map-north" type="button" aria-label="恢复正北方向" title="恢复正北方向" @click="resetNorth">N ↑</button>
  </div>
</template>
<style scoped>
.offline-map-wrap{position:relative;background:#e6e9e5;border:1px solid var(--border);border-radius:10px;overflow:hidden}.map-tools{display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:10px;background:white}.offline-map{width:100%;min-height:300px;background:repeating-linear-gradient(45deg,#e5e8e4,#e5e8e4 10px,#e0e4df 10px,#e0e4df 20px)}:deep(.ol-zoom button){width:34px;height:34px;font-size:23px;background:white;color:#17211e}:deep(.ol-control){background:#ffffffaa}
 .map-north{position:absolute;right:14px;bottom:150px;z-index:5;width:36px;height:36px;border-radius:8px;background:white;color:#17211e;border:1px solid #dfe5e0;font-size:13px;font-weight:700;box-shadow:0 3px 15px #17211e24;cursor:pointer}
.immersive{height:100%;border:0;border-radius:0}.immersive .offline-map{height:100%!important}.immersive .map-tools{position:absolute;z-index:4;top:14px;right:14px;max-width:min(670px,calc(100% - 400px));border-radius:12px;box-shadow:0 3px 18px #17211e24}.immersive :deep(.ol-zoom){left:auto;right:14px;top:auto;bottom:60px}@media(max-width:900px){.immersive .map-tools{top:68px;left:12px;right:auto;max-width:calc(100% - 24px);gap:5px;padding:7px}}
.immersive.hd .offline-map{width:200%;height:200%!important;transform:scale(.5);transform-origin:0 0}.immersive.hd :deep(.ol-zoom){transform:scale(2);transform-origin:bottom right;right:28px;bottom:120px}
</style>
