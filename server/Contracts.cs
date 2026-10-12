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
public sealed record BootstrapDto(SettingsDto Settings, CommunityDto[] Communities, PointTypeDto[] PointTypes, string Today, CampaignTypeDto[]? CampaignTypes = null);
public sealed record PointWriteRequest(string Name, string CommunityId, string TypeId, string Address, string? Contact = null, string? Phone = null, string Frequency = "irregular", string? Notes = null, bool Disabled = false, double? Longitude = null, double? Latitude = null, string[]? PhotoIds = null, bool Temporary = false);
public sealed record PointDto(string Id, string Name, string CommunityId, string CommunityName, string TypeId, string TypeName, string Address, string Contact, string Phone, string Frequency, string Notes, bool Disabled, string? LastInspectionDate, int OpenIssueCount, double? Longitude = null, double? Latitude = null, bool Temporary = false, string? CoverPhotoId = null, int PhotoCount = 0);
public sealed record PointDetailDto(PointDto Point, InspectionDto[] Inspections, IssueDto[] OpenIssues, PhotoDto[]? Photos = null);
public sealed record InspectionCreateRequest(string? PointId, string Date, string Inspector, string Result, string? Notes = null, string? NoticeNumber = null, IssueCreateRequest[]? Issues = null, string[]? PhotoIds = null, PointWriteRequest? NewPoint = null);
public sealed record IssueCreateRequest(string Description, string Requirement, string Deadline, string? ResponsibleUnit = null, string[]? PhotoIds = null);
public sealed record IssueUpdateRequest(string Description, string Requirement, string Deadline, string ResponsibleUnit);
public sealed record InspectionDto(string Id, string PointId, string PointName, string CommunityId, string CommunityName, string CommunityTitle, string TypeId, string TypeName, string Address, string Contact, string Phone, string Date, string Inspector, string Result, string Notes, string NoticeNumber, int IssueCount, int OpenIssueCount, string RectifyStatus = "none", int ClosedCount = 0, int OverdueCount = 0, string? NearestDeadline = null, bool PointTemporary = false, double? Longitude = null, double? Latitude = null);
public sealed record InspectionDetailDto(InspectionDto Inspection, IssueDto[] Issues, PhotoDto[] Photos, IssueDetailDto[]? IssueDetails = null);
public sealed record IssueDto(string Id, string InspectionId, string PointId, string PointName, string CommunityId, string CommunityName, string InspectionDate, string Description, string Requirement, string Deadline, string ResponsibleUnit, string Status, string? FeedbackDate, string FeedbackNotes, string? ReviewDate, string ReviewConclusion, string? ClosedDate, bool Overdue);
public sealed record IssueDetailDto(IssueDto Issue, InspectionDto Inspection, PhotoDto[] Photos, IssueActionDto[] Actions);
public sealed record IssueActionDto(string Id, string Kind, string Date, string Notes, PhotoDto[] Photos);
public sealed record FeedbackRequest(string Date, string Notes, string[]? PhotoIds = null);
public sealed record ReviewRequest(string Date, string Conclusion, string[]? PhotoIds = null);
public sealed record ReturnIssueRequest(string? Reason = null);
public sealed record PhotoDto(string Id, int Width, int Height, string Phase, string Url, string UploadedAt, string? InspectionId = null, string? IssueId = null, string? ActionId = null, string? PointId = null, string? CampaignId = null, bool HasThumb = false);
public sealed record PhotoUploadDto(string Id, int Width, int Height);
public sealed record IssueReminderGroupDto(int Count, IssueDto[] Items);
public sealed record PointReminderGroupDto(int Count, PointDto[] Items);
public sealed record DashboardDto(string Today, IssueReminderGroupDto Overdue, IssueReminderGroupDto DueSoon, IssueReminderGroupDto AwaitingReview, PointReminderGroupDto MissedInspections, InspectionDto[]? Todos = null, InspectionDto[]? RecentInspections = null);

public sealed record PointQuery(string? Q = null, string? CommunityId = null, string? TypeId = null, string Disabled = "false", int Page = 1, int PageSize = 30, bool Recent = false, bool Missed = false, string Temporary = "false");
public sealed record InspectionQuery(string? PointId = null, string? CommunityId = null, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30, string? Rectify = null, bool Overdue = false, bool DueSoon = false, string? TypeId = null, bool Temporary = false, bool AwaitingReview = false, bool OpenOnly = false);
public sealed record IssueQuery(string? Status = null, string? CommunityId = null, string? PointId = null, string? InspectionId = null, bool Overdue = false, bool DueSoon = false, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30);


public sealed record PointPromoteRequest(string Frequency = "irregular");
public sealed record InspectionUpdateRequest(string Date, string Inspector, string? NoticeNumber = null, string? Notes = null);
public sealed record InspectionCountsDto(int All, int None, int Pending, int Feedback, int Closed, int IssueCount);
public sealed record InspectionFeedbackItem(string IssueId, string Notes, string[]? PhotoIds = null);
public sealed record InspectionFeedbackRequest(string Date, InspectionFeedbackItem[] Items);
public sealed record InspectionReviewItem(string IssueId, string Conclusion, string[]? PhotoIds = null);
public sealed record InspectionReviewRequest(string Date, InspectionReviewItem[] Items);
public sealed record CampaignTypeDto(string Id, string Name);
public sealed record CampaignTypeWriteRequest(string Name);
public sealed record CampaignWriteRequest(string Name, string TypeId, string CommunityId, string Location, string Date, string Content,
    string? EndDate = null, string? Organizer = null, string? Participants = null, int? ParticipantCount = null, int? VehicleCount = null,
    decimal? GarbageTons = null, decimal? AreaSqm = null, string? Outcome = null, string? Notes = null,
    string[]? BeforePhotoIds = null, string[]? ProcessPhotoIds = null, string[]? AfterPhotoIds = null);
public sealed record CampaignDto(string Id, string Name, string TypeId, string TypeName, string CommunityId, string CommunityName,
    string Location, string Date, string? EndDate, string Organizer, string Participants, int? ParticipantCount, int? VehicleCount,
    decimal? GarbageTons, decimal? AreaSqm, string Content, string Outcome, string Notes, int PhotoCount);
public sealed record CampaignDetailDto(CampaignDto Campaign, PhotoDto[] Photos);
public sealed record CampaignQuery(string? Q = null, string? CommunityId = null, string? TypeId = null, string? From = null,
    string? To = null, int Page = 1, int PageSize = 30);
