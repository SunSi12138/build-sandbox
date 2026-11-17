import type XYZ from 'ol/source/XYZ'
import type { Extent } from 'ol/extent'
import type { TileRange } from 'ol/TileRange'

// The persisted API property is still named "preload" for database compatibility,
// but now denotes requested higher-zoom depth (0..3), NOT OpenLayers' preload.
export const DEFAULT_TILE_PRELOAD = 1
export const MAX_TILE_ZOOM = 18
export const MIN_TILE_PIXEL_RATIO = 0.25
export const MAX_PREFETCH_TILES = 300

export function effectiveRenderDepth(requested: number, zoom: number, pixelRatio: number, maxZoom = MAX_TILE_ZOOM): number {
  if (![requested, zoom, pixelRatio, maxZoom].every(Number.isFinite) || pixelRatio <= 0) return 0
  const depth = Math.max(0, Math.min(3, Math.trunc(requested)))
  const available = Math.max(0, maxZoom - zoom)
  const canvasLimit = Math.max(0, Math.floor(Math.log2(pixelRatio / MIN_TILE_PIXEL_RATIO)))
  return Math.min(depth, available, canvasLimit)
}

export function installDeepRasterTiles(source: XYZ, options: {
  depth: number; maxZoom: number; getZoom: () => number; getPixelRatio: () => number
}): void {
  const grid = source.getTileGrid()
  if (!grid) return
  const getZoom = grid.getZForResolution.bind(grid)
  const getPixelRatio = source.getTilePixelRatio.bind(source)
  const currentDepth = () => effectiveRenderDepth(options.depth, options.getZoom(), options.getPixelRatio(), options.maxZoom)
  // Match mappreview: choose child tiles at a finer resolution, then compensate
  // the source pixel ratio so the tiles retain their geographic alignment.
  grid.getZForResolution = (resolution, direction) => {
    const depth = currentDepth()
    return getZoom(depth ? resolution / 2 ** depth : resolution, direction)
  }
  source.getTilePixelRatio = pixelRatio => {
    const depth = currentDepth()
    return depth ? Math.max(MIN_TILE_PIXEL_RATIO, pixelRatio / 2 ** depth) : getPixelRatio(pixelRatio)
  }
}

export function deeperTileCoordinates(
  depth: number, zoom: number, pixelRatio: number, maxZoom: number,
  extent: Extent, tileRange: (extent: Extent, zoom: number) => TileRange,
  limit = MAX_PREFETCH_TILES,
): Array<[number, number, number]> {
  const rendered = effectiveRenderDepth(depth, zoom, pixelRatio, maxZoom)
  if (depth <= rendered || limit <= 0) return []
  const start = Math.floor(zoom) + Math.ceil(rendered) + 1
  const end = Math.min(Math.trunc(maxZoom), Math.floor(zoom) + Math.trunc(depth))
  if (start > end) return []
  const perLevel = Math.max(1, Math.floor(limit / (end - start + 1)))
  const candidates: Array<[number, number, number]> = []
  for (let z = start; z <= end; z++) {
    const range = tileRange(extent, z)
    const cx = (range.minX + range.maxX) / 2
    const cy = (range.minY + range.maxY) / 2
    const coords: Array<[number, number, number]> = []
    for (let x = range.minX; x <= range.maxX; x++)
      for (let y = range.minY; y <= range.maxY; y++)
        coords.push([x, y, Math.abs(x - cx) + Math.abs(y - cy)])
    coords.sort((a, b) => a[2] - b[2])
    for (const [x, y] of coords.slice(0, perLevel))
      candidates.push([z, x, y])
    if (candidates.length >= limit) break
  }
  return candidates.slice(0, limit)
}

export function makeDeepTilePrefetch(options: {
  depth: number
  maxZoom: number
  getZoom: () => number
  getPixelRatio: () => number
  getExtent: () => Extent | undefined
  tileRange: (extent: Extent, zoom: number) => TileRange
  fetchTile: (z: number, x: number, y: number, signal: AbortSignal) => Promise<unknown>
}): { schedule: () => void; dispose: () => void } {
  let timer: ReturnType<typeof setTimeout> | undefined
  let request: AbortController | undefined
  const visited = new Set<string>()
  function cancel() {
    if (timer) clearTimeout(timer)
    timer = undefined
    request?.abort()
    request = undefined
  }
  async function prefetch(signal: AbortSignal) {
    const extent = options.getExtent()
    if (!extent) return
    let coords: Array<[number, number, number]>
    try {
      coords = deeperTileCoordinates(options.depth, options.getZoom(), options.getPixelRatio(), options.maxZoom, extent, options.tileRange)
    } catch { return }
    const pending = coords.filter(([z, x, y]) => !visited.has(`${z}/${x}/${y}`))
    if (visited.size > 20000) visited.clear()
    let index = 0
    // Limit concurrent requests; a map move cancels the previous viewport.
    await Promise.all(Array.from({ length: Math.min(6, pending.length) }, async () => {
      while (!signal.aborted && index < pending.length) {
        const coord = pending[index++]!
        const [z, x, y] = coord
        const key = `${z}/${x}/${y}`
        if (visited.has(key)) continue
        visited.add(key)
        try { await options.fetchTile(z, x, y, signal) }
        catch { visited.delete(key) }
      }
    }))
  }
  return {
    schedule() {
      cancel()
      if (!options.depth) return
      timer = setTimeout(() => {
        timer = undefined
        request = new AbortController()
        void prefetch(request.signal)
      }, 800)
    },
    dispose: cancel,
  }
}
