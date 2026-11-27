import type XYZ from 'ol/source/XYZ.js'

export const MIN_TILE_PIXEL_RATIO = 0.25
export function normalizeDepth(requested: number): number {
  return Number.isFinite(requested) ? Math.max(0, Math.min(3, Math.trunc(requested))) : 0
}
export function effectiveRenderDepth(requested: number, baseZoom: number, pixelRatio: number, maxZoom: number): number {
  if (![baseZoom, pixelRatio, maxZoom].every(Number.isFinite) || pixelRatio <= 0) return 0
  return Math.min(normalizeDepth(requested), Math.max(0, Math.floor(maxZoom - baseZoom)),
    Math.max(0, Math.floor(Math.log2(pixelRatio / MIN_TILE_PIXEL_RATIO))))
}
// Warm lower-resolution ancestors for fallback when a local cache has sparse
// high-resolution coverage. This is separate from the displayed render depth.
export function rasterPreload(requested: number): number {
  return normalizeDepth(requested) + 1
}

export function configureDeepRaster(source: XYZ, options: {
  getDepth: () => number
  getResolution: () => number | undefined
  getPixelRatio: () => number
}) {
  const grid = source.getTileGrid()
  if (!grid) return
  const nativeZoom = grid.getZForResolution.bind(grid)
  const nativeRatio = source.getTilePixelRatio.bind(source)
  const depthAt = (resolution: number, z: number, pixelRatio: number) => {
    // Do not amplify the minimum-zoom tile set when viewing a far larger area.
    if (resolution > grid.getResolution(grid.getMinZoom())) return 0
    return effectiveRenderDepth(options.getDepth(), z, pixelRatio, grid.getMaxZoom())
  }
  grid.getZForResolution = function (resolution, direction) {
    const z = nativeZoom(resolution, direction)
    // OpenLayers supplies zDirection when choosing the rendered tile level.
    // Internal coordinate queries retain the original tile-grid behavior.
    return arguments.length >= 2 ? z + depthAt(resolution, z, options.getPixelRatio()) : z
  }
  source.getTilePixelRatio = pixelRatio => {
    const resolution = options.getResolution()
    if (!resolution || !Number.isFinite(resolution)) return nativeRatio(pixelRatio)
    const z = nativeZoom(resolution, source.zDirection)
    const depth = depthAt(resolution, z, pixelRatio)
    // Compensate for the additional tile detail without changing geographic
    // resolution, map container size, marker sizes or pointer coordinates.
    return depth ? Math.max(MIN_TILE_PIXEL_RATIO, pixelRatio / 2 ** depth) : nativeRatio(pixelRatio)
  }
}

export interface SavedMapView { longitude: number; latitude: number; zoom: number }
export function readMapView(raw: string | null): SavedMapView | null {
  try {
    const value = JSON.parse(raw ?? 'null') as Partial<SavedMapView> | null
    if (!value || typeof value.longitude !== 'number' || typeof value.latitude !== 'number' || typeof value.zoom !== 'number' ||
      ![value.longitude, value.latitude, value.zoom].every(Number.isFinite) ||
      Math.abs(value.longitude) > 180 || Math.abs(value.latitude) > 85 || value.zoom < 3 || value.zoom > 22) return null
    return { longitude: value.longitude, latitude: value.latitude, zoom: value.zoom }
  } catch { return null }
}
