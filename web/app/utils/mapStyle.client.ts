import { addProtocol, setWorkerUrl } from 'maplibre-gl'
import { MapLibreLayer } from '@geoblocks/ol-maplibre-layer'
import { PMTiles } from 'pmtiles'
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?url'

// MapLibre is only loaded when an MVT layer has a configured style directory.
// OpenLayers continues to own navigation, drawing, editing and record overlays.
setWorkerUrl(workerUrl)
const archives = new Map<string, PMTiles>()
addProtocol('hbtiles', async ({ url }) => {
  const match = /^hbtiles:\/\/([^/]+)\/(\d+)\/(\d+)\/(\d+)$/.exec(url)
  if (!match) throw new Error('Invalid PMTiles tile URL')
  const [, id, z, x, y] = match
  let archive = archives.get(id!)
  if (!archive) {
    archive = new PMTiles(`/api/map/archive/${encodeURIComponent(id!)}`)
    archives.set(id!, archive)
  }
  const result = await archive.getZxy(Number(z), Number(x), Number(y))
  if (!result) return { data: new ArrayBuffer(0) }
  const bytes = result.data
  return { data: bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer }
})

export function externalVectorStyle(styleUrl: string): MapLibreLayer {
  return new MapLibreLayer({
    mapLibreOptions: { style: styleUrl },
  })
}
