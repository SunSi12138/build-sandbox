export interface GeoShape { type: 'Point' | 'Polygon'; coordinates: number[][] }
export type IssueStatus = 'pending' | 'feedback' | 'closed'
export type Frequency = 'weekly' | 'monthly' | 'quarterly' | 'irregular'
export type PhotoPhase = 'site' | 'after' | 'review' | 'point' | 'before' | 'process'
export type RectifyStatus = 'none' | 'pending' | 'feedback' | 'closed'
export type InspectionResult = 'normal' | 'issues'

export interface ErrorDto { message: string }
export interface OkDto { ok: boolean }
export interface PageDto<T> { items: T[]; total: number; page: number; pageSize: number; matchingIssueCount?: number | null }
export interface SettingsDto { streetName: string; issuingUnit: string; defaultInspector: string; noticeTemplate: string; jpegQuality: number }
export interface CommunityDto { id: string; name: string; title: string }
export interface CommunityWriteRequest { name: string; title: string }
export interface PointTypeDto { id: string; name: string }
export interface PointTypeWriteRequest { name: string }
export interface BootstrapDto { settings: SettingsDto; communities: CommunityDto[]; pointTypes: PointTypeDto[]; campaignTypes?: CampaignTypeDto[]; today: string }

export interface PointWriteRequest {
  name: string; communityId: string; typeId: string; address: string
  contact?: string | null; phone?: string | null; frequency: Frequency; notes?: string | null; disabled: boolean
  shape?: GeoShape | null; longitude?: number | null; latitude?: number | null; temporary?: boolean; photoIds?: string[]
}
export interface PointDto {
  id: string; name: string; communityId: string; communityName: string; typeId: string; typeName: string
  address: string; contact: string; phone: string; frequency: Frequency; notes: string; disabled: boolean
  shape?: GeoShape | null; lastInspectionDate: string | null; openIssueCount: number
  longitude: number | null; latitude: number | null; temporary: boolean; coverPhotoId: string | null; photoCount: number
}
export interface PointDetailDto { point: PointDto; inspections: InspectionDto[]; openIssues: IssueDto[]; photos?: PhotoDto[]; campaigns?: CampaignDto[] }
export interface InspectionCreateRequest {
  pointId?: string | null; newPoint?: PointWriteRequest | null; date: string; inspector: string; result: InspectionResult; notes?: string | null
  shape?: GeoShape | null; noticeNumber?: string | null; issues?: IssueCreateRequest[]; photoIds?: string[]
}
export interface IssueCreateRequest { description: string; requirement: string; deadline: string; responsibleUnit?: string | null; photoIds?: string[] }
export interface IssueUpdateRequest { description: string; requirement: string; deadline: string; responsibleUnit: string }
export interface InspectionDto {
  id: string; pointId: string; pointName: string; communityId: string; communityName: string; communityTitle: string
  typeId: string; typeName: string; address: string; contact: string; phone: string; date: string
  inspector: string; result: InspectionResult; notes: string; noticeNumber: string; issueCount: number; openIssueCount: number
  rectifyStatus: RectifyStatus; closedCount: number; overdueCount: number; nearestDeadline: string | null
  shape?: GeoShape | null; pointTemporary: boolean; longitude: number | null; latitude: number | null
}
export interface InspectionDetailDto { inspection: InspectionDto; issues: IssueDto[]; photos: PhotoDto[]; issueDetails?: IssueDetailDto[] }
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
  pointId: string | null; campaignId: string | null; hasThumb: boolean
}
export interface PhotoUploadDto { id: string; width: number; height: number }
export interface IssueReminderGroupDto { count: number; items: IssueDto[] }
export interface PointReminderGroupDto { count: number; items: PointDto[] }
export interface DashboardDto {
  today: string; overdue: IssueReminderGroupDto; dueSoon: IssueReminderGroupDto
  awaitingReview: IssueReminderGroupDto; missedInspections: PointReminderGroupDto
  todos?: InspectionDto[]; recentInspections?: InspectionDto[]
}

export interface NetworkInterfaceDto { name: string; address: string; other: boolean }
export interface SystemInfoResponse { port: number; localUrl: string; addresses: string[]; today: string; version: string; listen: string[]; interfaces: NetworkInterfaceDto[]; canConfigure: boolean; unavailableSaved: string[]; switching: boolean; lastError: string | null }
export interface ListenUpdateResponse { ok: boolean; listen: string[]; addresses: string[]; localUrl: string; rebinding: boolean }
export interface BackupEntryDto { name: string; date: string; bytes: number }
export interface BackupOverviewDto { items: BackupEntryDto[]; retentionCount: number }
export interface BackupManifest { formatVersion: number; schemaVersion: number; createdAt: string; application: string }
export interface RestorePreviewDto {
  token: string; createdAt: string; schemaVersion: number; photoCount: number; pointCount: number
  inspectionCount: number; issueCount: number; campaignCount: number; expandedBytes: number; expiresAt: string
}
export interface RestoreRequest { token: string; confirm: boolean }
export interface RestoreResultDto { ok: boolean; preservedBackup: string; message: string }

export interface PointQuery {
  q?: string; communityId?: string; typeId?: string; disabled?: 'false' | 'true' | 'all'
  map?: boolean; page?: number; pageSize?: number; recent?: boolean; missed?: boolean; temporary?: 'false' | 'true' | 'all'
}
export interface InspectionQuery { map?: boolean; pointId?: string; communityId?: string; from?: string; to?: string; q?: string; page?: number; pageSize?: number; rectify?: RectifyStatus; overdue?: boolean; dueSoon?: boolean; typeId?: string; temporary?: boolean }
export interface InspectionUpdateRequest { date: string; inspector: string; noticeNumber?: string; notes?: string }
export interface InspectionCountsDto { all: number; none: number; pending: number; feedback: number; closed: number; issueCount: number }
export interface InspectionFeedbackRequest { date: string; items: { issueId: string; notes: string; photoIds: string[] }[] }
export interface InspectionReviewRequest { date: string; items: { issueId: string; conclusion: string; photoIds: string[] }[] }
export interface IssueQuery {
  status?: IssueStatus | 'all'; communityId?: string; pointId?: string; inspectionId?: string
  overdue?: boolean; dueSoon?: boolean; from?: string; to?: string; q?: string; page?: number; pageSize?: number
}

export interface CampaignTypeDto { id: string; name: string }
export interface NewPointDraft { name: string; communityId: string; typeId: string; address: string; contact: string; phone: string; coordinates: string }
export interface CampaignWriteRequest {
  name: string; typeId: string; pointId?: string | null; newPoint?: PointWriteRequest | null; date: string; content: string
  endDate?: string | null; organizer?: string | null; participants?: string | null; participantCount?: number | null; vehicleCount?: number | null
  garbageTons?: number | null; areaSqm?: number | null; outcome?: string | null; notes?: string | null
  beforePhotoIds?: string[]; processPhotoIds?: string[]; afterPhotoIds?: string[]
}
export interface CampaignDto {
  id: string; name: string; pointId: string; pointName: string; pointTemporary: boolean; typeId: string; typeName: string; communityId: string; communityName: string; location: string; date: string
  endDate: string | null; organizer: string; participants: string; participantCount: number | null; vehicleCount: number | null
  garbageTons: number | null; areaSqm: number | null; content: string; outcome: string; notes: string; photoCount: number
}
export interface CampaignDetailDto { campaign: CampaignDto; photos: PhotoDto[] }
export interface CampaignQuery { pointId?: string; q?: string; communityId?: string; typeId?: string; from?: string; to?: string; page?: number; pageSize?: number }
