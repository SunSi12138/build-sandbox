import type { BootstrapDto } from '~/types/api'
import { queryText } from '~/utils/format'

export type LedgerFilters = Record<string, string | string[]>
export function ledgerSearchParams(query: LedgerFilters) { const result = new URLSearchParams(); for (const [key, value] of Object.entries(query)) for (const item of Array.isArray(value) ? value : [value]) result.append(key, item); return result }
export type LedgerSource = 'inspection' | 'remediation' | 'activity'
export const ledgerRanges = [
  { label: '本月', value: 'month' }, { label: '上月', value: 'previous' },
  { label: '本季', value: 'quarter' }, { label: '本年', value: 'year' }, { label: '自定义', value: 'custom' }
]
export function ledgerDates(value: string, today: string) {
  const [year = 2026, month = 1] = today.split('-').map(Number)
  const first = value === 'year' ? 1 : value === 'quarter' ? Math.floor((month - 1) / 3) * 3 + 1 : value === 'previous' ? month - 1 : month
  const start = new Date(year, first - 1, 1)
  const end = new Date(year, first - 1 + (value === 'year' ? 12 : value === 'quarter' ? 3 : 1), 0)
  const format = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
  return { from: format(start), to: format(end) }
}
export function currentLedgerFilters(source: LedgerSource, query: Record<string, unknown> = {}) {
  const keys = ['q', 'communityId', 'typeId', 'from', 'to', 'pointId', 'matterId', 'template', 'remediationTypeId',
    ...(source === 'inspection' ? ['issueTypeId', 'rectify', 'overdue', 'dueSoon', 'temporary', 'awaitingReview', 'openOnly'] : [])]
  const result: LedgerFilters = {}
  for (const key of keys) {
    const value = queryText(query[key])
    if (typeof value === 'string' && value && value !== 'all') result[key] = value
  }
  const tags = query.tagId
  if (tags) result.tagId = (Array.isArray(tags) ? tags : [tags]).filter((value): value is string => typeof value === 'string' && !!value)
  return result
}
export function ledgerFilterSummary(source: LedgerSource, query: LedgerFilters, data: BootstrapDto | null) {
  const labels = [query.from || query.to ? `${query.from || '不限开始'} 至 ${query.to || '不限结束'}` : '全部日期']
  if (query.q) labels.push(`搜索“${query.q}”`)
  if (query.communityId) labels.push(data?.communities.find(item => item.id === query.communityId)?.name ?? '所选社区')
  else labels.push('全部社区')
  if (query.pointId) labels.push('所选点位')
  const types = source === 'inspection' ? data?.pointTypes : source === 'remediation' ? data?.remediationTypes : data?.activityTypes
  if (query.typeId) labels.push(types?.find(item => item.id === query.typeId)?.name ?? '所选类型')
  if (query.issueTypeId) labels.push(data?.issueTypes?.find(item => item.id === query.issueTypeId)?.name ?? '所选问题类型')
  const states: Record<string, string> = { pending: '待整改', feedback: '待复查', closed: '已闭环', none: '无需整改' }
  const rectify = queryText(query.rectify)
  if (rectify && states[rectify]) labels.push(states[rectify]!)
  for (const [key, label] of [['overdue', '已逾期'], ['dueSoon', '即将到期'], ['temporary', '临时点位'], ['awaitingReview', '待复查问题'], ['openOnly', '未闭环问题']]) {
    if (key && (query[key] === 'true' || query[key] === '1')) labels.push(label!)
  }
  if (query.remediationTypeId) labels.push(data?.remediationTypes.find(item => item.id === query.remediationTypeId)?.name ?? '所选整治类别')
  if (query.matterId) labels.push('所选事项')
  for (const id of Array.isArray(query.tagId) ? query.tagId : query.tagId ? [query.tagId] : []) labels.push(data?.tags.find(item => item.id === id)?.name ?? '所选标签')
  return labels.join(' · ')
}
