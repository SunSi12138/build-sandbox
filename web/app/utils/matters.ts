export const matterSources = [
  { label: '群众反映', value: 'public' }, { label: '热线转办', value: 'hotline' }, { label: '上级交办', value: 'assigned' },
  { label: '日常发现', value: 'discovered' }, { label: '其他', value: 'other' }
]
export const matterStatuses = [{ label: '待办理', value: 'pending' }, { label: '办理中', value: 'processing' }, { label: '已办结', value: 'closed' }]
export const matterOutcomes = [{ label: '已解决', value: 'resolved' }, { label: '未发现相关问题', value: 'no_issue' }, { label: '已移交', value: 'transferred' }, { label: '撤销或重复事项', value: 'withdrawn' }]
export const matterActions = [{ label: '办理记录', value: 'note' }, { label: '回复记录', value: 'reply' }, { label: '移交记录', value: 'transfer' }]
export const matterSourceLabel = (value: string) => matterSources.find(item => item.value === value)?.label ?? value
export const matterStatusLabel = (value: string) => matterStatuses.find(item => item.value === value)?.label ?? value
export const matterOutcomeLabel = (value: string) => matterOutcomes.find(item => item.value === value)?.label ?? value
export const matterActionLabel = (value: string) => ({ close: '办结', reopen: '重新办理' } as Record<string, string>)[value] ?? matterActions.find(item => item.value === value)?.label ?? value
