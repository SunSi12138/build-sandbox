using System.Net.Http.Headers;

namespace StreetEcoTool;

public sealed record WordDownload(string FilePath, string FileName);

public sealed partial class Store
{
    public WordDownload ExportInspectionWord(string id, bool notice)
    {
        lock (_gate)
        {
            var detail = GetInspectionDetail(id);
            var settings = GetSettings();
            if (notice)
            {
                if (detail.Issues.Length == 0) throw new ApiException(400, "本次排查无需整改通知单");
                if (string.IsNullOrWhiteSpace(settings.StreetName)) throw new ApiException(400, "请先填写街道全称");
                if (string.IsNullOrWhiteSpace(settings.IssuingUnit)) settings = settings with { IssuingUnit = settings.StreetName };
            }
            var photos = ResolveWordPhotos(detail.Photos.Where(photo => photo.Phase == "site"));
            var fileName = WordExporter.FileName(notice ? "整改通知单" : "排查记录单", detail.Inspection.PointName, detail.Inspection.Date);
            return WriteWordTemporary(fileName, destination =>
            {
                if (notice) WordExporter.WriteNotice(destination, detail, settings, photos);
                else WordExporter.WriteRecord(destination, detail, settings, photos);
            });
        }
    }

    public WordDownload ExportLedgerWord(string from, string to, string? communityId = null, bool includePhotos = false, string[]? pointIds = null, InspectionQuery? filters = null)
    {
        lock (_gate)
        {
            (from, to) = LedgerDates(from, to, "inspections", "inspection_date", filters is not null);
            CheckDateRange(from, to);
            CommunityDto? community = null;
            if (!string.IsNullOrEmpty(communityId)) community = RequireCommunity(Text(communityId, "所属社区", 100, true));
            var selectedPoints = ExportPoints(pointIds);
            var issues = GetLedgerIssues(from, to, community?.Id, selectedPoints.Select(p => p.Id).ToArray(), filters);
            WordPhoto[] photos = [];
            if (includePhotos)
            {
                var gathered = new List<PhotoDto>();
                foreach (var issue in issues) gathered.AddRange(ReadPhotos(issueId: issue.Id));
                foreach (var inspectionId in issues.Select(issue => issue.InspectionId).Distinct(StringComparer.Ordinal))
                    gathered.AddRange(ReadPhotos(inspectionId: inspectionId).Where(photo => photo.Phase == "site"));
                photos = ResolveWordPhotos(gathered);
            }
            var name = community?.Name ?? "全部社区";
            var history = ReadWordIssueHistory(issues);
            var ledger = new WordLedger(from, to, name, issues, photos, includePhotos, history, Today, selectedPoints.Length == 0 ? null : string.Join("、", selectedPoints.Select(p => p.Name)));
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("整改台账", name, to), destination => WordExporter.WriteLedger(destination, ledger, settings));
        }
    }

    public WordDownload ExportRectificationWord(string id)
    {
        lock (_gate)
        {
            var detail = GetInspectionDetail(id);
            var open = detail.Issues.Count(issue => issue.Status != "closed");
            if (open > 0) throw new ApiException(400, $"尚有 {open} 项问题未闭环");
            if (detail.Issues.Length == 0) throw new ApiException(400, "本次排查无需整改");
            var photos = ResolveWordPhotos(detail.Photos);
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("整改记录单", detail.Inspection.PointName, detail.Inspection.Date),
                destination => WordExporter.WriteRectificationRecord(destination, detail, settings, photos));
        }
    }

    public WordDownload ExportCampaignWord(string id)
    {
        lock (_gate)
        {
            var detail = GetCampaignDetail(id);
            var photos = ResolveWordPhotos(detail.Photos);
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("整治记录单", detail.Campaign.Name, detail.Campaign.Date),
                destination => WordExporter.WriteCampaignRecord(destination, detail, settings, photos));
        }
    }

    public WordDownload ExportInspectionLedgerWord(string from, string to, string? communityId = null, string[]? pointIds = null, InspectionQuery? filters = null)
    {
        lock (_gate)
        {
            (from, to) = LedgerDates(from, to, "inspections", "inspection_date", filters is not null);
            CheckDateRange(from, to);
            var community = string.IsNullOrEmpty(communityId) ? null : RequireCommunity(Text(communityId, "所属社区", 100, true));
            var selectedPoints = ExportPoints(pointIds);
            var inspections = GetLedgerInspections(from, to, community?.Id, selectedPoints.Select(p => p.Id).ToArray(), filters);
            var name = community?.Name ?? "全部社区";
            var ledger = new WordInspectionLedger(from, to, name, inspections, Today, selectedPoints.Length == 0 ? null : string.Join("、", selectedPoints.Select(p => p.Name)));
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("排查台账", name, to), destination => WordExporter.WriteInspectionLedger(destination, ledger, settings));
        }
    }

    public WordDownload ExportCampaignLedgerWord(string from, string to, string? communityId = null, string? typeId = null, bool includePhotos = false, string[]? pointIds = null, CampaignQuery? filters = null)
    {
        lock (_gate)
        {
            (from, to) = LedgerDates(from, to, "activities", "activity_date", filters is not null);
            CheckDateRange(from, to);
            var community = string.IsNullOrEmpty(communityId) ? null : RequireCommunity(Text(communityId, "所属社区", 100, true));
            var selectedPoints = ExportPoints(pointIds);
            var campaigns = GetLedgerCampaigns(from, to, community?.Id, typeId, selectedPoints.Select(p => p.Id).ToArray(), filters);
            var photos = includePhotos ? ResolveWordPhotos(campaigns.SelectMany(campaign => ReadPhotos(activityId: campaign.Id))) : [];
            var name = community?.Name ?? "全部社区";
            var ledger = new WordCampaignLedger(from, to, name, campaigns, photos, includePhotos, Today, selectedPoints.Length == 0 ? null : string.Join("、", selectedPoints.Select(p => p.Name)));
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("整治台账", name, to), destination => WordExporter.WriteCampaignLedger(destination, ledger, settings));
        }
    }

    public WordDownload ExportActivityWord(string id)
    {
        lock (_gate)
        {
            var detail = GetActivityDetail(id);
            if (detail.Activity.Template == "remediation") return ExportCampaignWord(id);
            var photos = ResolveWordPhotos(detail.Photos);
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("活动开展情况记录单", detail.Activity.Name, detail.Activity.Date),
                destination => WordExporter.WriteActivityRecord(destination, detail, settings, photos));
        }
    }

    public WordDownload ExportActivityLedgerWord(string from, string to, string? communityId = null, string? typeId = null, bool includePhotos = false, ActivityQuery? filters = null)
    {
        lock (_gate)
        {
            (from, to) = LedgerDates(from, to, "activities", "activity_date", filters is not null);
            CheckDateRange(from, to);
            var community = string.IsNullOrWhiteSpace(communityId) ? null : RequireCommunity(communityId);
            if (!string.IsNullOrWhiteSpace(typeId)) _ = RequireActivityType(typeId);
            var activities = GetLedgerActivities(from, to, community?.Id, typeId, filters);
            var photos = includePhotos ? ResolveWordPhotos(activities.SelectMany(activity => ReadPhotos(activityId: activity.Id))) : [];
            var name = community?.Name ?? "全部社区";
            var ledger = new WordActivityLedger(from, to, name, activities, photos, includePhotos, Today);
            return WriteWordTemporary(WordExporter.FileName("活动台账", name, to),
                destination => WordExporter.WriteActivityLedger(destination, ledger, GetSettings()));
        }
    }

    private (string From, string To) LedgerDates(string from, string to, string table, string column, bool current)
    {
        if (!current) return (DateText(from, "开始日期"), DateText(to, "结束日期"));
        var start = OptionalDate(from, "开始日期");
        var end = OptionalDate(to, "结束日期");
        CheckDateRange(start, end);
        // Table/column are fixed call-site constants; user input remains parameterized.
        using var command = Command($"SELECT MIN({column}),MAX({column}) FROM {table};");
        using var row = command.ExecuteReader();
        row.Read();
        var earliest = row.IsDBNull(0) ? Today : row.GetString(0);
        var latest = row.IsDBNull(1) ? Today : row.GetString(1);
        start ??= string.CompareOrdinal(earliest, end ?? latest) > 0 ? end! : earliest;
        end ??= string.CompareOrdinal(latest, start) < 0 ? start : latest;
        return (start, end);
    }

    private WordIssueHistory[] ReadWordIssueHistory(IssueDto[] issues)
    {
        var history = new List<WordIssueHistory>(issues.Length);
        foreach (var issue in issues)
        {
            var actions = new List<IssueActionDto>();
            using var command = Command("SELECT id,kind,action_date,notes FROM issue_actions WHERE issue_id=$issue ORDER BY rowid;", null, ("$issue", issue.Id));
            using var reader = command.ExecuteReader();
            // The ledger's photo collection already contains historical action photos.
            // Keep the text history even when the caller does not request photos.
            while (reader.Read()) actions.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), []));
            history.Add(new(issue.Id, actions.ToArray()));
        }
        return history.ToArray();
    }

    // This helper is called only while _gate is held, so the file paths and database
    // associations cannot change halfway through generating an export.
    private WordPhoto[] ResolveWordPhotos(IEnumerable<PhotoDto> photos)
    {
        var result = new List<WordPhoto>();
        foreach (var photo in photos.DistinctBy(photo => photo.Id, StringComparer.Ordinal))
        {
            using var command = Command("SELECT relative_path FROM photos WHERE id=$id;", null, ("$id", photo.Id));
            var relative = command.ExecuteScalar() as string ?? throw new ApiException(409, "部分照片已不存在，请刷新记录后重新导出。");
            var path = PhotoStorage.ResolvePath(PhotosDir, photo.Id, relative);
            if (!File.Exists(path)) throw new ApiException(409, "部分照片文件缺失，请检查电脑上的照片或备份后重新导出。");
            result.Add(new WordPhoto(photo, path));
        }
        return result.ToArray();
    }

    private WordDownload WriteWordTemporary(string fileName, Action<Stream> write)
    {
        Directory.CreateDirectory(TemporaryDir);
        var path = Path.Combine(TemporaryDir, NewId() + ".docx");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(file);
            return new WordDownload(path, fileName);
        }
        catch { TryDeleteFile(path); throw; }
    }

    public void MapWordApi(WebApplication app)
    {
        RequestDelegate record = async context => await SendWordAsync(context, ExportInspectionWord(Api.Id(context), false));
        RequestDelegate notice = async context => await SendWordAsync(context, ExportInspectionWord(Api.Id(context), true));
        RequestDelegate rectification = async context => await SendWordAsync(context, ExportRectificationWord(Api.Id(context)));
        RequestDelegate activity = async context => await SendWordAsync(context, ExportActivityWord(Api.Id(context)));
        RequestDelegate activityLedger = async context => await SendWordAsync(context, ExportActivityLedgerWord(
            context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(),
            context.Request.Query["communityId"].ToString(), context.Request.Query["typeId"].ToString(), WordPhotoOption(context), CurrentExport(context) ? Api.ActivityFilters(context) : null));
        RequestDelegate ledger = async context => await SendWordAsync(context, ExportLedgerWord(
            context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(), context.Request.Query["communityId"].ToString(), WordPhotoOption(context), ExportPointIds(context), CurrentExport(context) ? Api.InspectionFilters(context) : null));
        RequestDelegate inspectionLedger = async context => await SendWordAsync(context, ExportInspectionLedgerWord(
            context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(), context.Request.Query["communityId"].ToString(), ExportPointIds(context), CurrentExport(context) ? Api.InspectionFilters(context) : null));
        RequestDelegate campaignLedger = async context => await SendWordAsync(context, ExportCampaignLedgerWord(
            context.Request.Query["from"].ToString(), context.Request.Query["to"].ToString(), context.Request.Query["communityId"].ToString(), context.Request.Query["remediationTypeId"].ToString(), WordPhotoOption(context), ExportPointIds(context), CurrentExport(context) ? Api.CampaignFilters(context) : null));
        app.MapGet("/api/inspections/{id}/export/record", record);
        app.MapGet("/api/inspections/{id}/export/notice", notice);
        app.MapGet("/api/inspections/{id}/export/rectification", rectification);
        app.MapGet("/api/activities/{id}/export/record", activity);
        app.MapGet("/api/exports/activity-ledger", activityLedger);
        app.MapGet("/api/exports/rectification-ledger", ledger);
        app.MapGet("/api/exports/inspection-ledger", inspectionLedger);
        app.MapGet("/api/exports/remediation-ledger", campaignLedger);
    }

    private static bool CurrentExport(HttpContext context) => context.Request.Query["scope"].ToString() switch
    {
        "" or "custom" => false,
        "current" => true,
        _ => throw new ApiException(400, "导出范围必须是 current 或 custom")
    };

    private static string[] ExportPointIds(HttpContext context) =>
        context.Request.Query["pointId"].Select(id => id ?? "").ToArray();

    private static bool WordPhotoOption(HttpContext context) => context.Request.Query["photos"].ToString() switch
    {
        "" or "false" or "0" => false,
        "true" or "1" => true,
        _ => throw new ApiException(400, "是否附照片必须是 true 或 false")
    };

    private static async Task SendWordAsync(HttpContext context, WordDownload document)
    {
        try
        {
            context.Response.ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = document.FileName }.ToString();
            await context.Response.SendFileAsync(document.FilePath, context.RequestAborted);
        }
        finally { TryDeleteFile(document.FilePath); }
    }
}
