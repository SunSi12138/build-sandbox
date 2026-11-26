import test from 'node:test'
import assert from 'node:assert/strict'
import Feature from 'ol/Feature.js'
import Point from 'ol/geom/Point.js'
import Polygon from 'ol/geom/Polygon.js'
import LineString from 'ol/geom/LineString.js'
import { createMvtStyle } from './app/utils/mvtStyle.ts'

test('unconfigured style renders polygons, lines and points', () => {
  const style = createMvtStyle('')
  assert(style(new Feature(new Polygon([[[0,0],[1,0],[1,1],[0,0]]])))[0].getFill())
  assert(style(new Feature(new LineString([[0,0],[1,1]])))[0].getStroke())
  assert(style(new Feature(new Point([0,0])))[0].getImage())
})

test('missing name and mismatched geometry cannot erase a vector feature', () => {
  const point = new Feature({ geometry: new Point([0,0]), layer: 'poi' })
  const styles = createMvtStyle(JSON.stringify({ layers: [{ type: 'symbol', 'source-layer': 'poi', layout: { 'text-field': '{name}' } }, { type: 'fill', 'source-layer': 'poi' }] }))(point)
  assert(styles.some(style => style.getImage()))
})

test('explicitly hidden layers remain hidden', () => {
  const feature = new Feature({ geometry: new Point([0,0]), layer: 'poi' })
  assert.deepEqual(createMvtStyle(JSON.stringify({ layers: [{ type: 'circle', 'source-layer': 'poi', layout: { visibility: 'none' } }] }))(feature), [])
})
