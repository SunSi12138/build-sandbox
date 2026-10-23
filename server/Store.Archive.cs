using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record ArchiveRequest(bool Voided, string Reason, int Revision);
public sealed record ArchivedRecord(string Id, string Kind, string Name, string Date, string Reason, int Revision, string InspectionId = "");
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ArchiveRequest))]
[JsonSerializable(typeof(ArchivedRecord[]))]
public partial class ArchiveJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    public void SetArchived(string kind, string id, ArchiveRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var reason = Text(request.Reason, request.Voided ? "作废原因" : "恢复原因", 2000, true);
            string table; string before; int revision; bool voided;
            if (kind == "inspection")
            {
                var record = GetInspection(id); table = "inspections"; revision = record.Revision; voided = record.Voided;
                before = JsonSerializer.Serialize(record, AppJsonContext.Default.InspectionDto);
            }
            else if (kind == "issue")
            {
                var record = GetIssue(id);
                if (GetInspection(record.InspectionId).Voided) throw new ApiException(409, "请先恢复所属检查记录。");
                table = "issues"; revision = record.Revision; voided = record.Voided;
                before = JsonSerializer.Serialize(record, AppJsonContext.Default.IssueDto);
            }
            else throw new ApiException(400, "不支持的记录类型。");
            CheckRevision(request.Revision, revision);
            if (request.Voided == voided) throw new ApiException(409, "记录状态已改变，请刷新。");
            using var transaction = _db.BeginTransaction();
            using (var update = Command($"UPDATE {table} SET voided=$voided,void_reason=$reason WHERE id=$id;", transaction,
                ("$voided", request.Voided ? 1 : 0), ("$reason", request.Voided ? reason : ""), ("$id", id))) update.ExecuteNonQuery();
            AddCommunityHistory(kind, id, before, JsonSerializer.Serialize(request with { Reason = reason }, ArchiveJsonContext.Default.ArchiveRequest), request.Voided ? "作废" : "恢复", transaction);
            transaction.Commit();
        }
    }

    private static void RequireActive(bool voided)
    {
        if (voided) throw new ApiException(409, "记录已作废，请先恢复后再办理。");
    }

    public ArchivedRecord[] GetArchivedRecords()
    {
        lock (_gate)
        {
            var result = new List<ArchivedRecord>();
            result.AddRange(ReadInspectionList("WHERE n.voided=1 ORDER BY n.inspection_date DESC;", [], includeVoided: true).Select(x => new ArchivedRecord(x.Id, "inspection", x.PointName, x.Date, x.VoidReason, x.Revision)));
            result.AddRange(ReadIssueList("WHERE x.voided=1 AND n.voided=0 ORDER BY n.inspection_date DESC;", Today, [], true).Select(x => new ArchivedRecord(x.Id, "issue", x.PointName + "：" + x.Description, x.InspectionDate, x.VoidReason, x.Revision, x.InspectionId)));
            return result.OrderByDescending(x => x.Date).ToArray();
        }
    }

    public void MapArchiveApi(WebApplication app)
    {
        RequestDelegate list = c => Api.Write(c, GetArchivedRecords(), ArchiveJsonContext.Default.ArchivedRecordArray);
        RequestDelegate update = async c => { SetArchived(c.Request.RouteValues["kind"]!.ToString()!, Api.Id(c), await Api.Read(c, ArchiveJsonContext.Default.ArchiveRequest)); await Api.Write(c, new OkDto(), AppJsonContext.Default.OkDto); };
        app.MapGet("/api/archive", list); app.MapPut("/api/archive/{kind}/{id}", update);
    }
}
