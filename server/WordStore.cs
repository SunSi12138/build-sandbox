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
                if (detail.Issues.Length == 0) throw new ApiException(400, "本次排查未发现问题，可以导出排查记录单，无需整改通知单。");
                if (string.IsNullOrWhiteSpace(settings.StreetName)) throw new ApiException(400, "请先在设置中填写街道全称，再导出整改通知单。");
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

    public WordDownload ExportLedgerWord(string from, string to, string? communityId = null, bool includePhotos = false)
    {
        lock (_gate)
        {
            from = DateText(from, "开始日期");
            to = DateText(to, "结束日期");
            CheckDateRange(from, to);
            CommunityDto? community = null;
            if (!string.IsNullOrEmpty(communityId)) community = RequireCommunity(Text(communityId, "所属社区", 100, true));
            var issues = GetLedgerIssues(from, to, community?.Id);
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
            var ledger = new WordLedger(from, to, name, issues, photos, includePhotos, history);
            var settings = GetSettings();
            return WriteWordTemporary(WordExporter.FileName("整改台账", name, to), destination => WordExporter.WriteLedger(destination, ledger, settings));
        }
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
        RequestDelegate ledger = async context =>
        {
            var from = context.Request.Query["from"].ToString();
            var to = context.Request.Query["to"].ToString();
            var community = context.Request.Query["communityId"].ToString();
            var photos = context.Request.Query["photos"].ToString() switch
            {
                "" or "false" or "0" => false,
                "true" or "1" => true,
                _ => throw new ApiException(400, "是否附照片必须是 true 或 false。")
            };
            await SendWordAsync(context, ExportLedgerWord(from, to, community, photos));
        };
        app.MapGet("/api/inspections/{id}/export/record", record);
        app.MapGet("/api/inspections/{id}/export/notice", notice);
        app.MapGet("/api/exports/ledger", ledger);
    }

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
