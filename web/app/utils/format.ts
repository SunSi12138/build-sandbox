import type { Frequency, IssueStatus, PhotoPhase } from '~/types/api'

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
  return ({ site: '现场', after: '整改后', review: '复查' } as Record<PhotoPhase, string>)[phase as PhotoPhase] ?? phase
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
