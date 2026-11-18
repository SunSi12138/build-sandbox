/**
 * Editing helpers for a single closed OpenLayers polygon ring.
 * Indexing excludes the repeated closing coordinate; closing it again keeps
 * the GeoShape and OpenLayers geometry valid after a vertex is removed.
 */
export function polygonVertexCount(ring: readonly (readonly number[])[]): number {
  if (ring.length < 2) return ring.length
  const first = ring[0]!, last = ring[ring.length - 1]!
  return ring.length - (first[0] === last[0] && first[1] === last[1] ? 1 : 0)
}

export function removePolygonVertex(ring: readonly (readonly number[])[], index: number): number[][] | null {
  const count = polygonVertexCount(ring)
  if (!Number.isInteger(index) || index < 0 || index >= count || count <= 3) return null
  const vertices = ring.slice(0, count).filter((_, i) => i !== index).map(point => [...point])
  return [...vertices, [...vertices[0]!]]
}
