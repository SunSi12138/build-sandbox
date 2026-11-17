import type { GeoShape } from '~/types/api'

export interface MapRecord { id: string; title: string; subtitle: string; shape: GeoShape | null; color?: string; href?: string }
export function recordShape(record: { shape?: GeoShape | null; longitude?: number | null; latitude?: number | null }): GeoShape | null {
  if (record.shape) return record.shape
  return record.longitude != null && record.latitude != null ? { type: 'Point', coordinates: [[record.longitude, record.latitude]] } : null
}
/**
 * An area stores its boundary, not a separate navigation destination.
 * Pick an interior representative point rather than its arbitrary first vertex.
 * No schema changes or stored-coordinate mutations are required.
 */
export function navigationCoordinate(shape?: GeoShape | null): [number, number] | null {
  if (!shape?.coordinates?.length) return null
  if (shape.type === 'Point') return [shape.coordinates[0]![0]!, shape.coordinates[0]![1]!]
  const points = shape.coordinates
  const latitudes = points.map(point => point[1]!)
  const middle = (Math.min(...latitudes) + Math.max(...latitudes)) / 2 + 1e-10
  const crossings: number[] = []
  for (let index = 0; index < points.length - 1; index++) {
    const from = points[index]!, to = points[index + 1]!
    if ((from[1]! <= middle && to[1]! > middle) || (to[1]! <= middle && from[1]! > middle))
      crossings.push(from[0]! + (middle - from[1]!) * (to[0]! - from[0]!) / (to[1]! - from[1]!))
  }
  crossings.sort((a, b) => a - b)
  let width = -1, middleLongitude = points[0]![0]!
  for (let index = 0; index + 1 < crossings.length; index += 2) {
    const nextWidth = crossings[index + 1]! - crossings[index]!
    if (nextWidth > width) { width = nextWidth; middleLongitude = (crossings[index]! + crossings[index + 1]!) / 2 }
  }
  return [middleLongitude, width >= 0 ? middle : points[0]![1]!]
}
export function shapeLabel(shape?: GeoShape | null) { return !shape ? '未标记位置' : shape.type === 'Point' ? '已标记位置' : '已标记区域' }

export const mapColors = {"danger": "#dc2626", "warning": "#d97706", "normal": "#16796f", "disabled": "#64748b", "editing": "#0d9488", "halo": "#fff", "label": "#162f2c"} as const
