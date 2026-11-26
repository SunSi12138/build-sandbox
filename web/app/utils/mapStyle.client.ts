import { addProtocol, setWorkerUrl, type StyleSpecification } from 'maplibre-gl'
import { MapLibreLayer } from '@geoblocks/ol-maplibre-layer'
import type { PMTiles, Header } from 'pmtiles'
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?url'
import 'maplibre-gl/dist/maplibre-gl.css'

setWorkerUrl(workerUrl)
// Each mounted map owns its archive. Do not reuse a stale header/directory
// cache after a file is replaced, a layer is edited, or a map is reopened.
const archives = new Map<string, PMTiles>()
let archiveSequence = 0
addProtocol('hbtiles', async ({ url }, controller) => {
  const match = /^hbtiles:\/\/([^/]+)\/(\d+)\/(\d+)\/(\d+)$/.exec(url)
  if (!match) throw new Error('Invalid PMTiles tile URL')
  const [, key, z, x, y] = match
  const archive = archives.get(key!)
  if (!archive) throw new Error('PMTiles archive has been released')
  const result = await archive.getZxy(Number(z), Number(x), Number(y), controller.signal)
  return { data: result?.data ?? new ArrayBuffer(0) }
})

export async function externalVectorStyle(styleUrl: string, signal: AbortSignal, pmtiles?: { archive: PMTiles; header: Header }) {
  // Fetch and validate the local style independently so failure cannot stop
  // other base layers. An inline style also lets us apply archive zoom bounds.
  const response = await fetch(styleUrl, { signal, cache: 'no-store' })
  if (!response.ok) throw new Error('Style HTTP ' + response.status)
  const style = await response.json() as StyleSpecification
  if (style.version !== 8 || !style.sources || !Array.isArray(style.layers))
    throw new Error('Invalid vector style')
  signal.throwIfAborted()
  let key: string | undefined
  const release = () => { if (key) archives.delete(key) }
  try {
    if (pmtiles) {
      key = 'archive-' + (++archiveSequence)
      archives.set(key, pmtiles.archive)
      const { header } = pmtiles
      for (const source of Object.values(style.sources)) {
        if (source.type !== 'vector') continue
        // PMTiles always uses XYZ; an unrelated style's default maxzoom 22
        // otherwise requests tiles that cannot exist in lower-zoom archives.
        delete source.url
        source.tiles = ['hbtiles://' + key + '/{z}/{x}/{y}']
        source.scheme = 'xyz'
        source.minzoom = header.minZoom
        source.maxzoom = header.maxZoom
      }
    }
    return {
      layer: new MapLibreLayer({
        mapLibreOptions: { style, localIdeographFontFamily: false },
      }),
      release,
    }
  } catch (error) {
    release()
    throw error
  }
}
