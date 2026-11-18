using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed record MergeFieldPreview(string FieldId, string Label, string Type, string SourceValue, string TargetValue, string Decision);
public sealed record MergePreview(PointDto Source, PointDto Target, int Inspections, int Uses, int Units, int Photos, int Campaigns, int Plans, MergeFieldPreview[]? Fields = null);
public sealed record MergeRequest(string SourceId, string TargetId, int SourceRevision, int TargetRevision, string Reason);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MergePreview))]
[JsonSerializable(typeof(MergeRequest))]
public partial class ManagementJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private static bool HasFieldValue(JsonElement? data, string id, out JsonElement result)
    {
        if (data is { ValueKind: JsonValueKind.Object } && data.Value.TryGetProperty(id, out result) &&
            result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
            result.ToString().Length > 0) return true;
        result = default;
        return false;
    }

    private static MergeFieldPreview[] CompareMergeFields(PointDto source, PointDto target)
    {
        var fields = new List<MergeFieldPreview>();
        var sourceFields = source.FieldSnapshot ?? [];
        var targetFields = target.FieldSnapshot ?? [];
        var sameType = source.TypeId == target.TypeId;
        foreach (var field in sourceFields)
        {
            var equivalent = sameType && targetFields.Any(x =>
                x.Id == field.Id && x.Type == field.Type && x.Label == field.Label);
            var sourceHas = HasFieldValue(source.ExtraValues, field.Id, out var sourceValue);
            var targetHas = equivalent && HasFieldValue(target.ExtraValues, field.Id, out var targetValue);
            fields.Add(new(field.Id, field.Label, field.Type,
                sourceHas ? field.Type == "image" ? "已附图片" : sourceValue.ToString() : "",
                targetHas ? field.Type == "image" ? "已附图片" : targetValue.ToString() : "",
                !sourceHas ? "来源无值" : field.Type == "image" ? "来源字段图片保留在原档案"
                : !equivalent ? "字段定义不同，保留来源档案" : targetHas ? "保留目标值" : "补入目标空字段"));
        }
        foreach (var field in targetFields.Where(x => !sourceFields.Any(s =>
            sameType && s.Id == x.Id && s.Type == x.Type && s.Label == x.Label)))
        {
            var targetHas = HasFieldValue(target.ExtraValues, field.Id, out var value);
            fields.Add(new(field.Id, field.Label, field.Type, "",
                targetHas ? field.Type == "image" ? "已附图片" : value.ToString() : "", "保留目标字段"));
        }
        return [.. fields];
    }

    private string? MergedTarget(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT json_extract(body,'$.targetId') FROM community_records WHERE kind='point-merge' AND parent_id=$id LIMIT 1;", transaction, ("$id", id));
        return command.ExecuteScalar() as string;
    }

    private void RequireUnmergedPoint(string id, SqliteTransaction? transaction = null)
    {
        if (MergedTarget(id, transaction) is { } target) throw new ApiException(409, "这个场所已合并，请到保留的场所继续办理。编号：" + target);
    }

    public MergePreview PreviewMerge(string sourceId, string targetId)
    {
        lock (_gate)
        {
            if (sourceId == targetId) throw new ApiException(400, "请选择两个不同的场所。");
            RequireUnmergedPoint(sourceId); RequireUnmergedPoint(targetId);
            var source = GetPointInternal(sourceId); var target = GetPointInternal(targetId);
            if (target.Disabled || target.Temporary) throw new ApiException(400, "保留的场所应为正在使用的常态场所。");
            int Count(string sql) { using var c = Command(sql, null, ("$id", sourceId)); return Convert.ToInt32(c.ExecuteScalar()); }
            return new(source, target, Count("SELECT COUNT(*) FROM inspections WHERE point_id=$id;"),
                Count("SELECT COUNT(*) FROM community_records WHERE kind='use' AND parent_id=$id;"),
                Count("SELECT COUNT(*) FROM community_records WHERE kind='unit' AND parent_id=$id;"),
                Count("SELECT COUNT(*) FROM photos WHERE point_id=$id;"), Count("SELECT COUNT(*) FROM campaigns WHERE point_id=$id;"),
                Count("SELECT COUNT(DISTINCT r.id) FROM community_records r,json_each(r.body,'$.pointIds') j WHERE r.kind='plan' AND j.value=$id;"),
                CompareMergeFields(source, target));
        }
    }

    public PointDto MergePoints(MergeRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var preview = PreviewMerge(request.SourceId, request.TargetId);
            CheckRevision(request.SourceRevision, preview.Source.Revision); CheckRevision(request.TargetRevision, preview.Target.Revision);
            var reason = Text(request.Reason, "合并原因", 2000, true);
            var records = ListCommunityRecords("unit", request.SourceId, true).Concat(ListCommunityRecords("use", request.SourceId, true)).ToArray();
            var plans = ListCommunityRecords("plan", includeArchived: true).Where(x => x.Data.GetProperty("pointIds").EnumerateArray().Any(p => p.GetString() == request.SourceId)).ToArray();
            // Only same-type, same-ID, same-label non-image values can fill target
            // blanks. Conflicts keep the target. All other source fields remain in
            // its disabled historical dossier, including their own photo ownership.
            var mergedValues = JsonNode.Parse(preview.Target.ExtraValues?.GetRawText() ?? "{}")!.AsObject();
            if (preview.Source.TypeId == preview.Target.TypeId)
            {
                foreach (var field in preview.Source.FieldSnapshot ?? [])
                {
                    if (field.Type == "image" || !(preview.Target.FieldSnapshot ?? []).Any(x =>
                        x.Id == field.Id && x.Label == field.Label && x.Type == field.Type)) continue;
                    if (HasFieldValue(preview.Target.ExtraValues, field.Id, out _) ||
                        !HasFieldValue(preview.Source.ExtraValues, field.Id, out var value)) continue;
                    mergedValues[field.Id] = JsonNode.Parse(value.GetRawText());
                }
            }
            using var transaction = _db.BeginTransaction();
            using (var move = Command("""
                UPDATE inspections SET point_id=$target WHERE point_id=$source;
                UPDATE campaigns SET point_id=$target WHERE point_id=$source;
                UPDATE photos SET point_id=$target WHERE point_id=$source AND id NOT IN (
                    SELECT v.value FROM points p JOIN json_each(p.extra_values) v
                    JOIN json_each(p.field_snapshot) f ON json_extract(f.value,'$.id')=v.key
                    WHERE p.id=$source AND json_extract(f.value,'$.type')='image');
                UPDATE points SET disabled=1 WHERE id=$source;
                """, transaction, ("$source", request.SourceId), ("$target", request.TargetId))) move.ExecuteNonQuery();
            using (var setValues = Command("UPDATE points SET extra_values=$values WHERE id=$target;", transaction,
                ("$values", mergedValues.ToJsonString()), ("$target", request.TargetId))) setValues.ExecuteNonQuery();
            foreach (var record in records)
            {
                using var update = Command("UPDATE community_records SET parent_id=$target,revision=revision+1,updated_at=$at WHERE id=$id;", transaction,
                    ("$target", request.TargetId), ("$at", DateTimeOffset.Now.ToString("O")), ("$id", record.Id)); update.ExecuteNonQuery();
                AddCommunityHistory(record.Kind, record.Id, JsonSerializer.Serialize(record, CommunityJsonContext.Default.CommunityRecord), JsonSerializer.Serialize(record with { ParentId = request.TargetId, Revision = record.Revision + 1 }, CommunityJsonContext.Default.CommunityRecord), "随场所合并", transaction);
            }
            foreach (var plan in plans)
            {
                var body = JsonNode.Parse(plan.Data.GetRawText())!.AsObject();
                var ids = plan.Data.GetProperty("pointIds").EnumerateArray().Select(x => x.GetString() == request.SourceId ? request.TargetId : x.GetString()!).Distinct();
                body["pointIds"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                using var update = Command("UPDATE community_records SET body=$body,revision=revision+1,updated_at=$at WHERE id=$id;", transaction,
                    ("$body", body.ToJsonString()), ("$at", DateTimeOffset.Now.ToString("O")), ("$id", plan.Id)); update.ExecuteNonQuery();
                AddCommunityHistory("plan", plan.Id, plan.Data.GetRawText(), body.ToJsonString(), "合并重复场所", transaction);
            }
            using (var link = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at,archived) VALUES($id,'point-merge',$source,$name,$body,$at,1);", transaction,
                ("$id", NewId()), ("$source", request.SourceId), ("$name", preview.Source.Name), ("$body", JsonSerializer.Serialize(request with { Reason = reason }, ManagementJsonContext.Default.MergeRequest)), ("$at", DateTimeOffset.Now.ToString("O")))) link.ExecuteNonQuery();
            var before = JsonSerializer.Serialize(preview, ManagementJsonContext.Default.MergePreview);
            var after = JsonSerializer.Serialize(request with { Reason = reason }, ManagementJsonContext.Default.MergeRequest);
            AddCommunityHistory("point", request.SourceId, before, after, "合并至“" + preview.Target.Name + "”", transaction);
            AddCommunityHistory("point", request.TargetId, before, after, "并入“" + preview.Source.Name + "”", transaction);
            transaction.Commit();
            return GetPointInternal(request.TargetId);
        }
    }

    public void MapManagementApi(WebApplication app)
    {
        RequestDelegate preview = c => Api.Write(c, PreviewMerge(c.Request.Query["sourceId"].ToString(), c.Request.Query["targetId"].ToString()), ManagementJsonContext.Default.MergePreview);
        RequestDelegate merge = async c => await Api.Write(c, MergePoints(await Api.Read(c, ManagementJsonContext.Default.MergeRequest)), AppJsonContext.Default.PointDto);
        app.MapGet("/api/management/merge", preview); app.MapPost("/api/management/merge", merge);
    }
}
