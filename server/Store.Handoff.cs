using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record HandoffPlan(string Id, string Name, int Revision, string[] PendingPoints);
public sealed record HandoffIssue(string Id, string Name, int Revision);
public sealed record HandoffPreview(string FromId, string FromName, int FromRevision, string ToId, string ToName, int ToRevision, HandoffPlan[] Plans, HandoffIssue[] Issues, string Version);
public sealed record HandoffRequest(string FromId, string ToId, string Version, string Notes, bool DisablePrevious = false);
public sealed record HandoffResult(int Plans, int Issues);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HandoffPreview))]
[JsonSerializable(typeof(HandoffRequest))]
[JsonSerializable(typeof(HandoffResult))]
public partial class HandoffJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private static string[] StaffNames(string value) => value.Split([' ', '\t', '\r', '\n', ',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
    private string CurrentFollower(string issueId)
    {
        using var query = Command("SELECT json_extract(after_json,'$.follower') FROM community_history WHERE kind='issue' AND record_id=$id AND json_valid(after_json) AND COALESCE(json_extract(after_json,'$.follower'),'')<>'' ORDER BY at DESC,rowid DESC LIMIT 1;", null, ("$id", issueId));
        return query.ExecuteScalar() as string ?? "";
    }

    public HandoffPreview PreviewHandoff(string fromId, string toId)
    {
        lock (_gate)
        {
            if (fromId == toId) throw new ApiException(400, "交出和接收人员不能相同。");
            var staff = ListCommunityRecords("staff", includeArchived: true);
            var from = staff.FirstOrDefault(x => x.Id == fromId) ?? throw new ApiException(400, "交出人员不存在。");
            var to = staff.FirstOrDefault(x => x.Id == toId && !x.Archived) ?? throw new ApiException(400, "请选择正在使用的接收人员。");
            var plans = new List<HandoffPlan>();
            foreach (var plan in ListCommunityRecords("plan"))
            {
                if (!StaffNames(JsonText(plan.Data, "inspector")).Contains(from.Name)) continue;
                var remaining = new List<string>();
                foreach (var id in plan.Data.GetProperty("pointIds").EnumerateArray().Select(x => x.GetString()!).Distinct())
                {
                    using var query = Command("SELECT COUNT(*) FROM inspections WHERE point_id=$point AND inspection_date>=$from AND inspection_date<=$to AND result<>'incomplete' AND inspection_type_id=$type;", null,
                        ("$point", id), ("$from", JsonText(plan.Data, "from")), ("$to", JsonText(plan.Data, "to")), ("$type", JsonText(plan.Data, "typeId")));
                    if (Convert.ToInt32(query.ExecuteScalar()) == 0) remaining.Add(id);
                }
                if (remaining.Count > 0) plans.Add(new(plan.Id, plan.Name, plan.Revision, [.. remaining]));
            }
            var issues = ReadIssueList("WHERE x.status<>'closed' ORDER BY x.id;", Today, [])
                .Where(x => StaffNames(CurrentFollower(x.Id)).Contains(from.Name)).Select(x => new HandoffIssue(x.Id, x.PointName + "：" + x.Description, x.Revision)).ToArray();
            var preview = new HandoffPreview(from.Id, from.Name, from.Revision, to.Id, to.Name, to.Revision, plans.OrderBy(x => x.Id).ToArray(), issues, "");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preview, HandoffJsonContext.Default.HandoffPreview));
            return preview with { Version = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
    }

    public HandoffResult ApplyHandoff(HandoffRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var preview = PreviewHandoff(request.FromId, request.ToId);
            if (preview.Version != request.Version) throw new ApiException(409, "待交接工作有变化，请重新预览后交接。");
            var notes = Text(request.Notes, "交接说明", 2000, true);
            var allPlans = ListCommunityRecords("plan");
            var followers = preview.Issues.ToDictionary(x => x.Id, x => string.Join(' ', StaffNames(CurrentFollower(x.Id)).Select(name => name == preview.FromName ? preview.ToName : name).Distinct()));
            using var transaction = _db.BeginTransaction();
            var now = DateTimeOffset.Now.ToString("O");
            foreach (var item in preview.Plans)
            {
                var original = allPlans.Single(x => x.Id == item.Id);
                var body = JsonNode.Parse(original.Data.GetRawText())!.AsObject();
                var completed = original.Data.GetProperty("pointIds").EnumerateArray().Select(x => x.GetString()!).Except(item.PendingPoints).ToArray();
                body["pointIds"] = new JsonArray(item.PendingPoints.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                body["inspector"] = string.Join(' ', StaffNames(JsonText(original.Data, "inspector")).Select(x => x == preview.FromName ? preview.ToName : x).Distinct());
                body["notes"] = JsonText(original.Data, "notes") + "\n交接：" + notes;
                var newId = NewId();
                using (var add = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES($id,'plan','',$name,$body,$at);", transaction,
                    ("$id", newId), ("$name", original.Name + "（交接）"), ("$body", body.ToJsonString()), ("$at", now))) add.ExecuteNonQuery();
                AddCommunityHistory("plan", newId, "", body.ToJsonString(), "从“" + preview.FromName + "”接收", transaction);
                var previous = JsonNode.Parse(original.Data.GetRawText())!.AsObject();
                if (completed.Length > 0) previous["pointIds"] = new JsonArray(completed.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                else previous["handedOffTo"] = newId;
                using (var update = Command("UPDATE community_records SET body=$body,archived=$archived,revision=revision+1,updated_at=$at WHERE id=$id;", transaction,
                    ("$body", previous.ToJsonString()), ("$archived", completed.Length == 0 ? 1 : 0), ("$at", now), ("$id", original.Id))) update.ExecuteNonQuery();
                AddCommunityHistory("plan", original.Id, original.Data.GetRawText(), previous.ToJsonString(), "未完成任务交给“" + preview.ToName + "”", transaction);
            }
            foreach (var item in preview.Issues)
            {
                var operation = new IssueOperation("assign", Today, "人员交接：" + notes, Follower: followers[item.Id], Revision: item.Revision);
                AddCommunityHistory("issue", item.Id, "", JsonSerializer.Serialize(operation, IssueOperationJsonContext.Default.IssueOperation), "交给“" + preview.ToName + "”跟进", transaction);
            }
            using (var update = Command("UPDATE community_records SET archived=CASE WHEN $disable=1 THEN 1 ELSE archived END,revision=revision+1,updated_at=$at WHERE id=$id AND kind='staff';", transaction,
                ("$disable", request.DisablePrevious ? 1 : 0), ("$at", now), ("$id", request.FromId))) update.ExecuteNonQuery();
            AddCommunityHistory("staff", request.FromId, JsonSerializer.Serialize(preview, HandoffJsonContext.Default.HandoffPreview), JsonSerializer.Serialize(request, HandoffJsonContext.Default.HandoffRequest), "交接给“" + preview.ToName + "”", transaction);
            AddCommunityHistory("staff", request.ToId, "", JsonSerializer.Serialize(request, HandoffJsonContext.Default.HandoffRequest), "接收“" + preview.FromName + "”的工作", transaction);
            transaction.Commit();
            return new(preview.Plans.Length, preview.Issues.Length);
        }
    }

    public void MapHandoffApi(WebApplication app)
    {
        RequestDelegate preview = c => Api.Write(c, PreviewHandoff(c.Request.Query["fromId"].ToString(), c.Request.Query["toId"].ToString()), HandoffJsonContext.Default.HandoffPreview);
        RequestDelegate apply = async c => await Api.Write(c, ApplyHandoff(await Api.Read(c, HandoffJsonContext.Default.HandoffRequest)), HandoffJsonContext.Default.HandoffResult);
        app.MapGet("/api/management/handoff", preview); app.MapPost("/api/management/handoff", apply);
    }
}
