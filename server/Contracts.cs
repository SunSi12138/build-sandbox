namespace StreetEcoTool;

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record ErrorDto(string Message);
public sealed record OkDto(bool Ok = true);
public sealed record PageDto<T>(T[] Items, int Total, int Page, int PageSize, int? MatchingIssueCount = null);
public sealed record SettingsDto(string StreetName, string IssuingUnit, string DefaultInspector, string NoticeTemplate, int JpegQuality);
public sealed record CommunityDto(string Id, string Name, string Title);
public sealed record CommunityWriteRequest(string Name, string Title);
public sealed record PointTypeDto(string Id, string Name);
public sealed record PointTypeWriteRequest(string Name);
public sealed record IssueTypeDto(string Id, string Name);
public sealed record IssueTypeWriteRequest(string Name);
public sealed record ActivityTypeDto(string Id, string Name, string Template = "general", bool BuiltIn = false);
public sealed record ActivityTypeWriteRequest(string Name);
public sealed record BootstrapDto(SettingsDto Settings, CommunityDto[] Communities, PointTypeDto[] PointTypes, string Today, CampaignTypeDto[] RemediationTypes, IssueTypeDto[] IssueTypes, ActivityTypeDto[] ActivityTypes, TagDto[] Tags);
public sealed record PointWriteRequest(string Name, string CommunityId, string TypeId, string Address, string? Contact = null, string? Phone = null, string Frequency = "irregular", string? Notes = null, bool Disabled = false, double? Longitude = null, double? Latitude = null, string[]? PhotoIds = null, bool Temporary = false, string[]? TagIds = null);
public sealed record PointDto(string Id, string Name, string CommunityId, string CommunityName, string TypeId, string TypeName, string Address, string Contact, string Phone, string Frequency, string Notes, bool Disabled, string? LastInspectionDate, int OpenIssueCount, double? Longitude = null, double? Latitude = null, bool Temporary = false, string? CoverPhotoId = null, int PhotoCount = 0) { public TagDto[] Tags { get; init; } = []; }
public sealed record PointDetailDto(PointDto Point, InspectionDto[] Inspections, IssueDto[] OpenIssues, PhotoDto[]? Photos = null, ActivityDto[]? Activities = null, AttachmentDto[]? Attachments = null, MatterLinkDto[]? Matters = null);
public sealed record InspectionCreateRequest(string? PointId, string Date, string Inspector, string Result, string? Notes = null, string? NoticeNumber = null, IssueCreateRequest[]? Issues = null, string[]? PhotoIds = null, PointWriteRequest? NewPoint = null, string Source = "routine", string[]? MatterIds = null, string[]? TagIds = null);
public sealed record IssueCreateRequest(string Description, string Requirement, string Deadline, string? ResponsibleUnit = null, string[]? PhotoIds = null, string? IssueTypeId = null, string[]? MatterIds = null);
public sealed record IssueUpdateRequest(string Description, string Requirement, string Deadline, string ResponsibleUnit, string IssueTypeId = "", string[]? MatterIds = null);
public sealed record InspectionDto(string Id, string PointId, string PointName, string CommunityId, string CommunityName, string CommunityTitle, string TypeId, string TypeName, string Address, string Contact, string Phone, string Date, string Inspector, string Result, string Notes, string NoticeNumber, int IssueCount, int OpenIssueCount, string RectifyStatus = "none", int ClosedCount = 0, int OverdueCount = 0, string? NearestDeadline = null, bool PointTemporary = false, double? Longitude = null, double? Latitude = null, string Source = "routine") { public TagDto[] Tags { get; init; } = []; public MatterLinkDto[] Matters { get; init; } = []; }
public sealed record InspectionDetailDto(InspectionDto Inspection, IssueDto[] Issues, PhotoDto[] Photos, IssueDetailDto[]? IssueDetails = null);
public sealed record IssueDto(string Id, string InspectionId, string PointId, string PointName, string CommunityId, string CommunityName, string InspectionDate, string Description, string Requirement, string Deadline, string ResponsibleUnit, string Status, string? FeedbackDate, string FeedbackNotes, string? ReviewDate, string ReviewConclusion, string? ClosedDate, bool Overdue, string IssueTypeId = "", string IssueTypeName = "") { public MatterLinkDto[] Matters { get; init; } = []; }
public sealed record IssueDetailDto(IssueDto Issue, InspectionDto Inspection, PhotoDto[] Photos, IssueActionDto[] Actions);
public sealed record IssueActionDto(string Id, string Kind, string Date, string Notes, PhotoDto[] Photos);
public sealed record FeedbackRequest(string Date, string Notes, string[]? PhotoIds = null);
public sealed record ReviewRequest(string Date, string Conclusion, string[]? PhotoIds = null);
public sealed record ReturnIssueRequest(string? Reason = null);
public sealed record PhotoDto(string Id, int Width, int Height, string Phase, string Url, string UploadedAt, string? InspectionId = null, string? IssueId = null, string? ActionId = null, string? PointId = null, bool HasThumb = false, string? ActivityId = null);
public sealed record PhotoUploadDto(string Id, int Width, int Height);
public sealed record IssueReminderGroupDto(int Count, IssueDto[] Items);
public sealed record PointReminderGroupDto(int Count, PointDto[] Items);
public sealed record DashboardDto(string Today, IssueReminderGroupDto Overdue, IssueReminderGroupDto DueSoon, IssueReminderGroupDto AwaitingReview, PointReminderGroupDto MissedInspections, InspectionDto[]? Todos = null, InspectionDto[]? RecentInspections = null, MatterReminderDto? Matters = null);

public sealed record PointQuery(string? Q = null, string? CommunityId = null, string? TypeId = null, string Disabled = "false", int Page = 1, int PageSize = 30, bool Recent = false, bool Missed = false, string Temporary = "false", string[]? TagIds = null);
public sealed record InspectionQuery(string? PointId = null, string? CommunityId = null, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30, string? Rectify = null, bool Overdue = false, bool DueSoon = false, string? TypeId = null, bool Temporary = false, bool AwaitingReview = false, bool OpenOnly = false, string? IssueTypeId = null, string[]? TagIds = null, string? MatterId = null);
public sealed record IssueQuery(string? Status = null, string? CommunityId = null, string? PointId = null, string? InspectionId = null, bool Overdue = false, bool DueSoon = false, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30, string? TypeId = null);


public sealed record PointPhotosRequest(string[] PhotoIds);
public sealed record PointStatusRequest(bool? Disabled);
public sealed record PointPromoteRequest(string Frequency = "irregular");
public sealed record InspectionUpdateRequest(string Date, string Inspector, string? NoticeNumber = null, string? Notes = null, string? Source = null);
public sealed record InspectionCountsDto(int All, int None, int Pending, int Feedback, int Closed, int IssueCount);
public sealed record InspectionFeedbackItem(string IssueId, string Notes, string[]? PhotoIds = null);
public sealed record InspectionFeedbackRequest(string Date, InspectionFeedbackItem[] Items);
public sealed record InspectionReviewItem(string IssueId, string Conclusion, string[]? PhotoIds = null);
public sealed record InspectionReviewRequest(string Date, InspectionReviewItem[] Items);
public sealed record CampaignTypeDto(string Id, string Name);
public sealed record CampaignTypeWriteRequest(string Name);
public sealed record CampaignWriteRequest(string Name, string TypeId, string? PointId, string Date, string Content,
    string? EndDate = null, string? Organizer = null, string? Participants = null, int? ParticipantCount = null, int? VehicleCount = null,
    decimal? GarbageTons = null, decimal? AreaSqm = null, string? Outcome = null, string? Notes = null,
    string[]? BeforePhotoIds = null, string[]? ProcessPhotoIds = null, string[]? AfterPhotoIds = null, PointWriteRequest? NewPoint = null);
public sealed record CampaignDto(string Id, string Name, string TypeId, string TypeName, string CommunityId, string CommunityName,
    string Location, string Date, string? EndDate, string Organizer, string Participants, int? ParticipantCount, int? VehicleCount,
    decimal? GarbageTons, decimal? AreaSqm, string Content, string Outcome, string Notes, int PhotoCount, string PointId = "", string PointName = "", bool PointTemporary = false);
public sealed record CampaignDetailDto(CampaignDto Campaign, PhotoDto[] Photos);
public sealed record CampaignQuery(string? Q = null, string? CommunityId = null, string? TypeId = null, string? From = null,
    string? To = null, int Page = 1, int PageSize = 30, string? PointId = null, string[]? TagIds = null, string? MatterId = null, string? ActivityTypeId = null);

public sealed record RemediationWriteRequest(string TypeId, int? VehicleCount = null, decimal? GarbageTons = null,
    decimal? AreaSqm = null, string[]? BeforePhotoIds = null, string[]? ProcessPhotoIds = null, string[]? AfterPhotoIds = null);
public sealed record RemediationDto(string TypeId, string TypeName, int? VehicleCount, decimal? GarbageTons, decimal? AreaSqm);
public sealed record ActivityWriteRequest(string Name, string TypeId, string Date, string Content,
    string? CommunityId = null, string? EndDate = null, string? Location = null, string? Organizer = null,
    string? Participants = null, int? ParticipantCount = null, string? Outcome = null, string? Notes = null, string[]? PhotoIds = null,
    string? PointId = null, PointWriteRequest? NewPoint = null, RemediationWriteRequest? Remediation = null,
    string[]? TagIds = null, string[]? MatterIds = null, string[]? IssueIds = null);
public sealed record ActivityDto(string Id, string Name, string TypeId, string TypeName, string? CommunityId, string CommunityName,
    string Location, string Date, string? EndDate, string Organizer, string Participants, int? ParticipantCount,
    string Content, string Outcome, string Notes, int PhotoCount, string Template = "general", string? PointId = null,
    string PointName = "", bool PointTemporary = false, RemediationDto? Remediation = null)
{
    public TagDto[] Tags { get; init; } = [];
    public MatterLinkDto[] Matters { get; init; } = [];
    public IssueDto[] RelatedIssues { get; init; } = [];
}
public sealed record ActivityDetailDto(ActivityDto Activity, PhotoDto[] Photos);
public sealed record ActivityQuery(string? Q = null, string? CommunityId = null, string? TypeId = null,
    string? From = null, string? To = null, int Page = 1, int PageSize = 30, string? PointId = null,
    string? Template = null, string? RemediationTypeId = null, string[]? TagIds = null, string? MatterId = null);

public sealed record TagDto(string Id, string Name, bool Disabled, int UsageCount);
public sealed record TagWriteRequest(string Name, bool Disabled = false);
public sealed record TagLinksRequest(string[] TagIds);
public sealed record MatterLinksRequest(string[] MatterIds);
public sealed record MatterLinkDto(string Id, string Title, string Status);
public sealed record MatterWriteRequest(string Title, string Source, string ReceivedDate, string Content,
    string? PointId = null, string? Location = null, string? Deadline = null, string? Handler = null,
    string? ReferenceNumber = null, string? Contact = null, string? Phone = null, string[]? TagIds = null);
public sealed record MatterDto(string Id, string Title, string Source, string ReceivedDate, string Content,
    string? PointId, string PointName, string Location, string? Deadline, string Handler, string ReferenceNumber,
    string Contact, string Phone, string Status, string? ClosedDate, string Outcome, string Conclusion,
    int InspectionCount, int IssueCount, int OpenIssueCount, int ActivityCount, bool Overdue)
{ public TagDto[] Tags { get; init; } = []; }
public sealed record MatterActionDto(string Id, string Kind, string Date, string Handler, string Notes, string Outcome);
public sealed record MatterActionRequest(string Kind, string Date, string Notes, string? Handler = null, string? Outcome = null);
public sealed record MatterDetailDto(MatterDto Matter, MatterActionDto[] Actions, InspectionDto[] Inspections, IssueDto[] Issues, ActivityDto[] Activities);
public sealed record MatterQuery(string? Q = null, string? Status = null, string? Source = null, string? PointId = null,
    string? From = null, string? To = null, bool Overdue = false, int Page = 1, int PageSize = 30, string[]? TagIds = null);
public sealed record MatterReminderDto(int PendingCount, int OverdueCount, MatterDto[] Items);
public sealed record AttachmentDto(string Id, string PointId, string Name, string Notes, long Size, string ContentType, string UploadedAt, string Sha256, bool CanPreview);
public sealed record AttachmentUpdateRequest(string Name, string? Notes = null);

public sealed record RecordRelationsRequest(string[]? TagIds = null, string[]? MatterIds = null);
