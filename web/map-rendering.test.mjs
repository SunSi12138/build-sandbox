import test from 'node:test'
import assert from 'node:assert/strict'
import XYZ from 'ol/source/XYZ.js'
import { configureDeepRaster, effectiveRenderDepth, normalizeDepth, rasterPreload, readMapView } from './app/utils/mapRendering.ts'

test('raster rendering depth is bounded by device and real tile levels', () => {
  assert.equal(normalizeDepth(4), 3)
  assert.equal(effectiveRenderDepth(3, 14, 1, 20), 2)
  assert.equal(effectiveRenderDepth(3, 14, 2, 20), 3)
  assert.equal(effectiveRenderDepth(3, 19, 1, 20), 1)
  assert.equal(effectiveRenderDepth(3, 20, 1, 20), 0)
  assert.equal(rasterPreload(2), 3)
})

test('actual OpenLayers grid selects finer tiles with reciprocal pixel ratio and no view mutation', () => {
  const source = new XYZ({ minZoom: 5, maxZoom: 20, url: '/tiles/{z}/{x}/{y}' })
  const grid = source.getTileGrid()
  let requested = 2
  let resolution = grid.getResolution(16)
  configureDeepRaster(source, { getDepth: () => requested, getResolution: () => resolution, getPixelRatio: () => 1 })
  assert.equal(grid.getZForResolution(resolution, source.zDirection), 18)
  assert.equal(grid.getZForResolution(resolution), 16)
  assert.equal(source.getTilePixelRatio(1), 0.25)
  assert.equal(grid.getResolution(16), resolution)
  requested = 0
  assert.equal(grid.getZForResolution(resolution, source.zDirection), 16)
  assert.equal(source.getTilePixelRatio(1), 1)
  requested = 3
  resolution = grid.getResolution(19)
  assert.equal(grid.getZForResolution(resolution, source.zDirection), 20)
  assert.equal(source.getTilePixelRatio(1), 0.5)
  resolution = grid.getResolution(5) * 4
  assert.equal(grid.getZForResolution(resolution, source.zDirection), 5)
  assert.equal(source.getTilePixelRatio(1), 1)
})

test('main map viewport restoration validates WGS84 coordinates and zoom', () => {
  const value = { longitude: 88.893426, latitude: 29.274331, zoom: 17.5 }
  assert.deepEqual(readMapView(JSON.stringify(value)), value)
  assert.equal(readMapView('not-json'), null)
  assert.equal(readMapView(JSON.stringify({ ...value, latitude: 99 })), null)
  assert.equal(readMapView(JSON.stringify({ ...value, zoom: 25 })), null)
  assert.equal(readMapView(null), null)
})
