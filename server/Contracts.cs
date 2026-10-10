namespace StreetEcoTool;

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record ErrorDto(string Message);
public sealed record OkDto(bool Ok = true);
public sealed record PageDto<T>(T[] Items, int Total, int Page, int PageSize);
public sealed record SettingsDto(string StreetName, string IssuingUnit, string DefaultInspector, string NoticeTemplate, int JpegQuality);
public sealed record CommunityDto(string Id, string Name, string Title);
public sealed record CommunityWriteRequest(string Name, string Title);
public sealed record PointTypeDto(string Id, string Name);
public sealed record PointTypeWriteRequest(string Name);
public sealed record BootstrapDto(SettingsDto Settings, CommunityDto[] Communities, PointTypeDto[] PointTypes, string Today);
public sealed record PointWriteRequest(string Name, string CommunityId, string TypeId, string Address, string? Contact = null, string? Phone = null, string Frequency = "irregular", string? Notes = null, bool Disabled = false);
public sealed record PointDto(string Id, string Name, string CommunityId, string CommunityName, string TypeId, string TypeName, string Address, string Contact, string Phone, string Frequency, string Notes, bool Disabled, string? LastInspectionDate, int OpenIssueCount);
public sealed record PointDetailDto(PointDto Point, InspectionDto[] Inspections, IssueDto[] OpenIssues);
public sealed record InspectionCreateRequest(string PointId, string Date, string Inspector, string Result, string? Notes = null, string? NoticeNumber = null, IssueCreateRequest[]? Issues = null, string[]? PhotoIds = null);
public sealed record IssueCreateRequest(string Description, string Requirement, string Deadline, string? ResponsibleUnit = null, string[]? PhotoIds = null);
public sealed record IssueUpdateRequest(string Description, string Requirement, string Deadline, string ResponsibleUnit);
public sealed record InspectionDto(string Id, string PointId, string PointName, string CommunityId, string CommunityName, string CommunityTitle, string TypeId, string TypeName, string Address, string Contact, string Phone, string Date, string Inspector, string Result, string Notes, string NoticeNumber, int IssueCount, int OpenIssueCount);
public sealed record InspectionDetailDto(InspectionDto Inspection, IssueDto[] Issues, PhotoDto[] Photos);
public sealed record IssueDto(string Id, string InspectionId, string PointId, string PointName, string CommunityId, string CommunityName, string InspectionDate, string Description, string Requirement, string Deadline, string ResponsibleUnit, string Status, string? FeedbackDate, string FeedbackNotes, string? ReviewDate, string ReviewConclusion, string? ClosedDate, bool Overdue);
public sealed record IssueDetailDto(IssueDto Issue, InspectionDto Inspection, PhotoDto[] Photos, IssueActionDto[] Actions);
public sealed record IssueActionDto(string Id, string Kind, string Date, string Notes, PhotoDto[] Photos);
public sealed record FeedbackRequest(string Date, string Notes, string[]? PhotoIds = null);
public sealed record ReviewRequest(string Date, string Conclusion, string[]? PhotoIds = null);
public sealed record ReturnIssueRequest(string? Reason = null);
public sealed record PhotoDto(string Id, int Width, int Height, string Phase, string Url, string UploadedAt, string? InspectionId = null, string? IssueId = null, string? ActionId = null);
public sealed record PhotoUploadDto(string Id, int Width, int Height);
public sealed record IssueReminderGroupDto(int Count, IssueDto[] Items);
public sealed record PointReminderGroupDto(int Count, PointDto[] Items);
public sealed record DashboardDto(string Today, IssueReminderGroupDto Overdue, IssueReminderGroupDto DueSoon, IssueReminderGroupDto AwaitingReview, PointReminderGroupDto MissedInspections);

public sealed record PointQuery(string? Q = null, string? CommunityId = null, string? TypeId = null, string Disabled = "false", int Page = 1, int PageSize = 30, bool Recent = false, bool Missed = false);
public sealed record InspectionQuery(string? PointId = null, string? CommunityId = null, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30);
public sealed record IssueQuery(string? Status = null, string? CommunityId = null, string? PointId = null, string? InspectionId = null, bool Overdue = false, bool DueSoon = false, string? From = null, string? To = null, string? Q = null, int Page = 1, int PageSize = 30);
