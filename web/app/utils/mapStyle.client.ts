import { addProtocol, setWorkerUrl } from 'maplibre-gl'
import { MapLibreLayer } from '@geoblocks/ol-maplibre-layer'
import { PMTiles } from 'pmtiles'
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?url'
import 'maplibre-gl/dist/maplibre-gl.css'

// MapLibre is only loaded when an MVT layer has a configured style directory.
// OpenLayers continues to own navigation, drawing, editing and record overlays.
setWorkerUrl(workerUrl)
const archives = new Map<string, PMTiles>()
function archiveFor(id: string) {
  let archive = archives.get(id)
  if (!archive) {
    archive = new PMTiles(`/api/map/archive/${encodeURIComponent(id)}`)
    archives.set(id, archive)
  }
  return archive
}
export async function prefetchArchiveVectorTile(id: string, z: number, x: number, y: number, signal: AbortSignal) {
  if (signal.aborted) return
  await archiveFor(id).getZxy(z, x, y)
}
addProtocol('hbtiles', async ({ url }) => {
  const match = /^hbtiles:\/\/([^/]+)\/(\d+)\/(\d+)\/(\d+)$/.exec(url)
  if (!match) throw new Error('Invalid PMTiles tile URL')
  const [, id, z, x, y] = match
  const result = await archiveFor(id!).getZxy(Number(z), Number(x), Number(y))
  if (!result) return { data: new ArrayBuffer(0) }
  return { data: result.data }
})

export function externalVectorStyle(styleUrl: string): MapLibreLayer {
  return new MapLibreLayer({
    mapLibreOptions: { style: styleUrl, localIdeographFontFamily: false },
  })
}
