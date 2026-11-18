import assert from 'node:assert/strict'
import { test } from 'node:test'
import { polygonVertexCount, removePolygonVertex } from './app/utils/polygonVertices.ts'

const ring = [[0,0], [5,0], [8,4], [5,8], [0,4], [0,0]]
test('closed polygon excludes duplicate closing vertex from selectable count', () => {
  assert.equal(polygonVertexCount(ring), 5)
  assert.equal(polygonVertexCount(ring.slice(0,-1)), 5)
})
test('removing first, middle or last vertex closes the polygon correctly', () => {
  for (const index of [0, 2, 4]) {
    const reduced = removePolygonVertex(ring, index)
    assert.equal(reduced?.length, 5)
    assert.deepEqual(reduced?.[0], reduced?.at(-1))
    assert.equal(reduced?.slice(0,-1).some(point => point[0] === ring[index][0] && point[1] === ring[index][1]), false)
  }
  assert.deepEqual(removePolygonVertex(ring, 0)?.[0], [5,0])
})
test('rejects removal that would leave fewer than three corners or invalid index', () => {
  const triangle = [[0,0],[3,0],[3,3],[0,0]]
  assert.equal(removePolygonVertex(triangle, 1), null)
  assert.equal(removePolygonVertex(ring, -1), null)
  assert.equal(removePolygonVertex(ring, 5), null)
  assert.equal(removePolygonVertex(ring, 1.5), null)
  const fourCorners = removePolygonVertex(ring, 1)
  assert(fourCorners)
  assert.equal(polygonVertexCount(fourCorners), 4)
  const threeCorners = removePolygonVertex(fourCorners, 1)
  assert(threeCorners)
  assert.equal(polygonVertexCount(threeCorners), 3)
  assert.equal(removePolygonVertex(threeCorners, 1), null)
})
