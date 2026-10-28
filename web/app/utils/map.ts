import type { GeoShape } from '~/types/api'

export interface MapRecord { id: string; title: string; subtitle: string; shape: GeoShape | null; color?: string; href?: string }
export function recordShape(record: { shape?: GeoShape | null; longitude?: number | null; latitude?: number | null }): GeoShape | null {
  if (record.shape) return record.shape
  return record.longitude != null && record.latitude != null ? { type: 'Point', coordinates: [[record.longitude, record.latitude]] } : null
}
export function shapeLabel(shape?: GeoShape | null) { return !shape ? '尚未标记位置' : shape.type === 'Point' ? '已标记点位' : `已画区域 · ${shape.coordinates.length - 1} 个边界点` }
