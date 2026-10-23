import type { Frequency, IssueStatus, PhotoPhase, InspectionDto } from '~/types/api'

export function errorMessage(error: unknown): string {
  if (error && typeof error === 'object') {
    const value = error as { data?: { message?: unknown }; status?: number; statusCode?: number; message?: unknown }
    if (typeof value.data?.message === 'string') return value.data.message
    if (value.status === 404 || value.statusCode === 404) return '这条记录不存在，可能已被删除。'
    if (typeof value.message === 'string' && !value.message.includes('fetch') && !value.message.includes('[GET]') && !value.message.includes('[POST]')) return value.message
  }
  return '暂时无法连接工具，请确认电脑上的程序正在运行，然后重试。'
}

export function statusLabel(status: string): string {
  return ({ pending: '待整改', feedback: '社区已反馈', closed: '已复查闭环' } as Record<string, string>)[status] ?? status
}
export function statusColor(status: string): 'warning' | 'info' | 'success' | 'neutral' {
  return ({ pending: 'warning', feedback: 'info', closed: 'success' } as const)[status as IssueStatus] ?? 'neutral'
}
export function frequencyLabel(frequency: string): string {
  return ({ weekly: '每周', monthly: '每月', quarterly: '每季', irregular: '不定期' } as Record<string, string>)[frequency] ?? frequency
}
export const frequencyOptions: { label: string; value: Frequency }[] = [
  { label: '每周', value: 'weekly' }, { label: '每月', value: 'monthly' },
  { label: '每季', value: 'quarterly' }, { label: '不定期', value: 'irregular' }
]
export function phaseLabel(phase: string): string {
  return ({ site: '现场', after: '整改后', review: '复查', point: '现场', before: '整治前', process: '整治中', activity: '活动' } as Record<PhotoPhase, string>)[phase as PhotoPhase] ?? phase
}
export function todayLocal(): string {
  const date = new Date()
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}
export function queryText(value: unknown): string {
  return typeof value === 'string' ? value : Array.isArray(value) && typeof value[0] === 'string' ? value[0] : ''
}
export function displayDate(value: string | null | undefined): string { return value || '暂无' }
export function formatBytes(bytes: number): string {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
}

export function rectifyLabel(record: Pick<InspectionDto, 'rectifyStatus' | 'issueCount' | 'closedCount'>, compact = false): string {
  if (record.rectifyStatus === 'none') return compact ? '正常' : '无需整改'
  if (record.rectifyStatus === 'closed') return '已闭环'
  const label = record.rectifyStatus === 'feedback' ? '待复查' : '待整改'
  return compact ? `${label} ${record.closedCount}/${record.issueCount}` : label
}
export function rectifyTone(status: string): 'warning' | 'info' | 'success' {
  return status === 'pending' ? 'warning' : status === 'feedback' ? 'info' : 'success'
}
export function overdueDays(deadline: string | null | undefined, today = todayLocal()): number {
  if (!deadline || deadline >= today) return 0
  return Math.max(0, Math.round((Date.parse(`${today}T00:00:00Z`) - Date.parse(`${deadline}T00:00:00Z`)) / 86400000))
}
export function parseCoordinates(value: string): { longitude: number | null; latitude: number | null; error: string } {
  if (!value.trim()) return { longitude: null, latitude: null, error: '' }
  const parts = value.trim().split(/[,，\s]+/)
  const longitude = Number(parts[0]), latitude = Number(parts[1])
  const invalid = parts.length !== 2 || !parts.every(part => /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(part)) || !Number.isFinite(longitude) || !Number.isFinite(latitude)
  if (!invalid && longitude >= 3 && longitude <= 54 && latitude >= 73 && latitude <= 136) return { longitude: null, latitude: null, error: '看起来顺序反了，应为 经度,纬度' }
  if (invalid || longitude < -180 || longitude > 180 || latitude < -90 || latitude > 90) return { longitude: null, latitude: null, error: '请输入「经度,纬度」，如 88.893426,29.274331' }
  return { longitude: Number(longitude.toFixed(6)), latitude: Number(latitude.toFixed(6)), error: '' }
}
export function coordinatesText(longitude: number | null, latitude: number | null): string {
  return longitude === null || latitude === null ? '' : `${longitude.toFixed(6)},${latitude.toFixed(6)}`
}
export function mapLink(kind: 'marker' | 'navigation', name: string, longitude: number, latitude: number): string {
  const title = encodeURIComponent(name.replace(/[,，]/g, ''))
  const position = `${longitude.toFixed(6)},${latitude.toFixed(6)}`
  return kind === 'marker'
    ? `https://uri.amap.com/marker?position=${position}&name=${title}&src=hbtool&coordinate=gaode&callnative=0`
    : `https://uri.amap.com/navigation?to=${position},${title}&mode=car&policy=0&src=hbtool&callnative=0`
}
