import test from 'node:test'
import assert from 'node:assert/strict'
import {
  DEFAULT_TILE_PRELOAD, MIN_TILE_PIXEL_RATIO,
  effectiveRenderDepth, installDeepRasterTiles, deeperTileCoordinates, makeDeepTilePrefetch,
} from './app/utils/deepTiles.ts'

test('preload remains one while requested higher-resolution depth is independent', () => {
  assert.equal(DEFAULT_TILE_PRELOAD, 1)
  assert.equal(MIN_TILE_PIXEL_RATIO, 0.25)
  assert.equal(effectiveRenderDepth(0, 14, 1), 0)
  assert.equal(effectiveRenderDepth(1, 14, 1), 1)
  assert.equal(effectiveRenderDepth(2, 14, 1), 2)
  assert.equal(effectiveRenderDepth(3, 14, 1), 2, 'normal DPR 1 caps rendered depth to 2')
  assert.equal(effectiveRenderDepth(3, 14, 2), 3, 'DPR 2 can render one additional zoom level')
  assert.equal(effectiveRenderDepth(3, 17, 1), 1, 'data max z18 caps rendered depth')
  assert.equal(effectiveRenderDepth(3, 18, 1), 0)
  assert.equal(effectiveRenderDepth(3, 20, 1), 0)
})

test('mappreview resolution shift is compensated by reciprocal tile-pixel ratio', () => {
  let receivedResolution
  const grid = {
    getZForResolution(resolution) { receivedResolution = resolution; return Math.round(Math.log2(1024 / resolution)) },
  }
  let zoom = 14
  const source = {
    getTileGrid: () => grid,
    getTilePixelRatio: pixelRatio => pixelRatio,
  }
  installDeepRasterTiles(source, { depth: 2, maxZoom: 18, getZoom: () => zoom, getPixelRatio: () => 1 })
  const z = grid.getZForResolution(64)
  assert.equal(receivedResolution, 16)
  assert.equal(z, 6)
  assert.equal(source.getTilePixelRatio(1), 0.25)
  zoom = 18
  grid.getZForResolution(64)
  assert.equal(receivedResolution, 64, 'at source max zoom no extra resolution is requested')
  assert.equal(source.getTilePixelRatio(1), 1)
})

test('only unrenderable higher zooms are prefetched, center first, bounded by 300', () => {
  const area = [0, 0, 100, 100]
  const range = () => ({ minX: 0, maxX: 30, minY: 0, maxY: 30 })
  const coords = deeperTileCoordinates(3, 14, 1, 18, area, range)
  assert.equal(coords.length, 300)
  assert(coords.every(([z]) => z === 17), 'z14 +3 is the only unrendered level at DPR1')
  assert.deepEqual(coords[0], [17, 15, 15])
  assert.equal(deeperTileCoordinates(2, 14, 1, 18, area, range).length, 0)
  assert.equal(deeperTileCoordinates(3, 18, 1, 18, area, range).length, 0)
  const vector = deeperTileCoordinates(2, 14, 1, 18, area, range, 12, 0)
  assert.deepEqual([...new Set(vector.map(([z]) => z))], [15, 16])
  assert.equal(vector.length, 12)
})

test('prefetch scheduler does not request current level or leak work after disposal', async () => {
  const requested = []
  const prefetch = makeDeepTilePrefetch({
    depth: 2, maxZoom: 18, getZoom: () => 16, getPixelRatio: () => 1,
    getRenderedDepth: () => 0, getExtent: () => [0, 0, 1, 1],
    tileRange: () => ({ minX: 0, maxX: 0, minY: 0, maxY: 0 }),
    fetchTile: async (...args) => { requested.push(args.slice(0, 3)) },
  })
  prefetch.schedule()
  prefetch.dispose()
  await new Promise(resolve => setTimeout(resolve, 850))
  assert.deepEqual(requested, [])
})
