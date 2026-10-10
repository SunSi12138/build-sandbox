export type IssueStatus = 'pending' | 'feedback' | 'closed'
export type Frequency = 'weekly' | 'monthly' | 'quarterly' | 'irregular'
export type PhotoPhase = 'site' | 'after' | 'review'
export type InspectionResult = 'normal' | 'issues'

export interface ErrorDto { message: string }
export interface OkDto { ok: boolean }
export interface PageDto<T> { items: T[]; total: number; page: number; pageSize: number }
export interface SettingsDto { streetName: string; issuingUnit: string; defaultInspector: string; noticeTemplate: string; jpegQuality: number }
export interface CommunityDto { id: string; name: string; title: string }
export interface CommunityWriteRequest { name: string; title: string }
export interface PointTypeDto { id: string; name: string }
export interface PointTypeWriteRequest { name: string }
export interface BootstrapDto { settings: SettingsDto; communities: CommunityDto[]; pointTypes: PointTypeDto[]; today: string }

export interface PointWriteRequest {
  name: string; communityId: string; typeId: string; address: string
  contact?: string | null; phone?: string | null; frequency: Frequency; notes?: string | null; disabled: boolean
}
export interface PointDto {
  id: string; name: string; communityId: string; communityName: string; typeId: string; typeName: string
  address: string; contact: string; phone: string; frequency: Frequency; notes: string; disabled: boolean
  lastInspectionDate: string | null; openIssueCount: number
}
export interface PointDetailDto { point: PointDto; inspections: InspectionDto[]; openIssues: IssueDto[] }
export interface InspectionCreateRequest {
  pointId: string; date: string; inspector: string; result: InspectionResult; notes?: string | null
  noticeNumber?: string | null; issues?: IssueCreateRequest[]; photoIds?: string[]
}
export interface IssueCreateRequest { description: string; requirement: string; deadline: string; responsibleUnit?: string | null; photoIds?: string[] }
export interface IssueUpdateRequest { description: string; requirement: string; deadline: string; responsibleUnit: string }
export interface InspectionDto {
  id: string; pointId: string; pointName: string; communityId: string; communityName: string; communityTitle: string
  typeId: string; typeName: string; address: string; contact: string; phone: string; date: string
  inspector: string; result: InspectionResult; notes: string; noticeNumber: string; issueCount: number; openIssueCount: number
}
export interface InspectionDetailDto { inspection: InspectionDto; issues: IssueDto[]; photos: PhotoDto[] }
export interface IssueDto {
  id: string; inspectionId: string; pointId: string; pointName: string; communityId: string; communityName: string
  inspectionDate: string; description: string; requirement: string; deadline: string; responsibleUnit: string; status: IssueStatus
  feedbackDate: string | null; feedbackNotes: string; reviewDate: string | null; reviewConclusion: string; closedDate: string | null; overdue: boolean
}
export interface IssueDetailDto { issue: IssueDto; inspection: InspectionDto; photos: PhotoDto[]; actions: IssueActionDto[] }
export interface IssueActionDto { id: string; kind: 'feedback' | 'review' | 'return'; date: string; notes: string; photos: PhotoDto[] }
export interface FeedbackRequest { date: string; notes: string; photoIds?: string[] }
export interface ReviewRequest { date: string; conclusion: string; photoIds?: string[] }
export interface ReturnIssueRequest { reason?: string }
export interface PhotoDto {
  id: string; width: number; height: number; phase: PhotoPhase; url: string; uploadedAt: string
  inspectionId: string | null; issueId: string | null; actionId: string | null
}
export interface PhotoUploadDto { id: string; width: number; height: number }
export interface IssueReminderGroupDto { count: number; items: IssueDto[] }
export interface PointReminderGroupDto { count: number; items: PointDto[] }
export interface DashboardDto {
  today: string; overdue: IssueReminderGroupDto; dueSoon: IssueReminderGroupDto
  awaitingReview: IssueReminderGroupDto; missedInspections: PointReminderGroupDto
}

export interface SystemInfoResponse { port: number; localUrl: string; addresses: string[]; today: string; version: string }
export interface BackupEntryDto { name: string; date: string; bytes: number }
export interface BackupOverviewDto { items: BackupEntryDto[]; retentionCount: number }
export interface BackupManifest { formatVersion: number; schemaVersion: number; createdAt: string; application: string }
export interface RestorePreviewDto {
  token: string; createdAt: string; schemaVersion: number; photoCount: number; pointCount: number
  inspectionCount: number; issueCount: number; expandedBytes: number; expiresAt: string
}
export interface RestoreRequest { token: string; confirm: boolean }
export interface RestoreResultDto { ok: boolean; preservedBackup: string; message: string }

export interface PointQuery {
  q?: string; communityId?: string; typeId?: string; disabled?: 'false' | 'true' | 'all'
  page?: number; pageSize?: number; recent?: boolean; missed?: boolean
}
export interface InspectionQuery { pointId?: string; communityId?: string; from?: string; to?: string; q?: string; page?: number; pageSize?: number }
export interface IssueQuery {
  status?: IssueStatus | 'all'; communityId?: string; pointId?: string; inspectionId?: string
  overdue?: boolean; dueSoon?: boolean; from?: string; to?: string; q?: string; page?: number; pageSize?: number
}
