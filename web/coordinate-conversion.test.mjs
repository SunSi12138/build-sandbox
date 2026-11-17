import test from 'node:test'
import assert from 'node:assert/strict'
import { gcj02ToWgs84, wgs84ToGcj02 } from './app/utils/gcj02.ts'

function near(actual, expected, tolerance = 1e-6) {
  assert(Math.abs(actual[0] - expected[0]) < tolerance, `longitude: ${actual[0]} vs ${expected[0]}`)
  assert(Math.abs(actual[1] - expected[1]) < tolerance, `latitude: ${actual[1]} vs ${expected[1]}`)
}

test('known WGS84 Beijing coordinate converts to GCJ-02', () => {
  near(wgs84ToGcj02([116.397389, 39.908722]), [116.4036325533, 39.9101254757])
})

test('existing GCJ-02 coordinates remain the source of truth after map projection roundtrip', () => {
  for (const gcj of [[116.403633, 39.910125], [88.893426, 29.274331], [121.478223, 31.228458]]) {
    const wgs = gcj02ToWgs84(gcj)
    near(wgs84ToGcj02(wgs), gcj, 1e-7)
    assert(Math.abs(wgs[0] - gcj[0]) > 0.001, 'stored GCJ-02 must not be treated as WGS84')
  }
})

test('WGS84 and GCJ-02 are unchanged outside China', () => {
  const tokyo = [139.6917, 35.6895]
  assert.deepEqual(wgs84ToGcj02(tokyo), tokyo)
  assert.deepEqual(gcj02ToWgs84(tokyo), tokyo)
})

test('rounding converted polygon coordinates preserves ring closure', () => {
  const ring = [
    [88.893426, 29.274331], [88.894426, 29.274331],
    [88.894426, 29.275331], [88.893426, 29.274331]
  ]
  const result = ring.map(point => wgs84ToGcj02(gcj02ToWgs84(point)).map(n => Math.round(n * 1e6) / 1e6))
  assert.deepEqual(result[0], result.at(-1))
  result.forEach((point, i) => near(point, ring[i], 1e-6))
})
