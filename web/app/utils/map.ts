import type { GeoShape } from '~/types/api'

export interface MapRecord { id: string; title: string; subtitle: string; shape: GeoShape | null; color?: string; href?: string }
export function recordShape(record: { shape?: GeoShape | null; longitude?: number | null; latitude?: number | null }): GeoShape | null {
  if (record.shape) return record.shape
  return record.longitude != null && record.latitude != null ? { type: 'Point', coordinates: [[record.longitude, record.latitude]] } : null
}
export function shapeLabel(shape?: GeoShape | null) { return !shape ? '未标记位置' : shape.type === 'Point' ? '已标记位置' : '已标记区域' }

export const mapColors = {"danger": "#dc2626", "warning": "#d97706", "normal": "#16796f", "disabled": "#64748b", "editing": "#0d9488", "halo": "#fff", "label": "#162f2c"} as const
