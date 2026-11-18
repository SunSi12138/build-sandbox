using System.Globalization;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string PointSelect = """
        SELECT p.id,p.name,p.community_id,c.name,p.type_id,t.name,p.address,p.contact,p.phone,p.frequency,p.notes,p.disabled,
        (SELECT MAX(n.inspection_date) FROM active_inspections n WHERE n.point_id=p.id AND n.result<>'incomplete' AND n.inspection_date<=$today),
        (SELECT COUNT(*) FROM active_issues x JOIN active_inspections n ON n.id=x.inspection_id WHERE n.point_id=p.id AND x.status<>'closed'),
        p.longitude,p.latitude,p.temporary,
        (SELECT ph.id FROM photos ph WHERE ph.point_id=p.id ORDER BY ph.uploaded_at,ph.rowid LIMIT 1),
        (SELECT COUNT(*) FROM photos ph WHERE ph.point_id=p.id),
        (SELECT COUNT(*) FROM community_history h WHERE h.kind='point' AND h.record_id=p.id),p.color,p.extra_values
        FROM points p JOIN communities c ON c.id=p.community_id JOIN point_types t ON t.id=p.type_id
        """;
    private const string PointWhere = """
        WHERE ($q='' OR p.name LIKE $like OR p.address LIKE $like OR p.contact LIKE $like)
        AND ($community='' OR p.community_id=$community) AND ($type='' OR p.type_id=$type)
        AND ($disabled='all' OR p.disabled=$inactive) AND ($temporary='all' OR p.temporary=$isTemporary)
        AND ($missed=0 OR (p.disabled=0 AND p.temporary=0 AND p.frequency<>'irregular' AND NOT EXISTS (
            SELECT 1 FROM active_inspections n WHERE n.point_id=p.id AND n.result<>'incomplete' AND n.inspection_date<=$today
            AND n.inspection_date >= CASE p.frequency WHEN 'weekly' THEN $week WHEN 'monthly' THEN $month WHEN 'quarterly' THEN $quarter END)))
        """;

    private static PointDto ReadPoint(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetInt32(11) != 0, NullableString(row, 12), row.GetInt32(13), row.IsDBNull(14) ? null : row.GetDouble(14),
        row.IsDBNull(15) ? null : row.GetDouble(15), row.GetInt32(16) != 0, NullableString(row, 17), row.GetInt32(18), row.GetInt32(19), row.GetString(20), ReadPointExtra(row.GetString(21)));

    private static JsonElement? ReadPointExtra(string value) =>
        value == "{}" ? null : JsonDocument.Parse(value).RootElement.Clone();

    public PointDto GetPoint(string id)
    {
        lock (_gate) return GetPointInternal(id);
    }

    private PointDto GetPointInternal(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command(PointSelect + " WHERE p.id=$id;", transaction, ("$id", id), ("$today", Today));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(404, "点位不存在。");
        return ReadPoint(row);
    }

    public PageDto<PointDto> ListPoints(PointQuery query)
    {
        lock (_gate)
        {
            if (query.Disabled is not ("all" or "true" or "false")) throw new ApiException(400, "停用筛选必须是 all、true 或 false。");
            if (query.Temporary is not ("all" or "true" or "false")) throw new ApiException(400, "点位性质筛选必须是 all、true 或 false。");
            var q = Text(query.Q, "搜索内容", 200);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            var today = DateOnly.FromDateTime(DateTime.Today);
            (string Name, object? Value)[] parameters = [("$q", q), ("$like", "%" + q + "%"), ("$community", query.CommunityId ?? ""),
                ("$type", query.TypeId ?? ""), ("$temporary", query.Temporary), ("$isTemporary", query.Temporary == "true" ? 1 : 0), ("$disabled", query.Disabled), ("$inactive", query.Disabled == "true" ? 1 : 0),
                ("$missed", query.Missed ? 1 : 0), ("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$week", ReminderRules.PeriodStart("weekly", today)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$month", ReminderRules.PeriodStart("monthly", today)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("$quarter", ReminderRules.PeriodStart("quarterly", today)!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
            int total;
            using (var count = Command("SELECT COUNT(*) FROM points p " + PointWhere, null, parameters)) total = Convert.ToInt32(count.ExecuteScalar());
            var ordering = query.Recent ? " ORDER BY 13 DESC,p.rowid DESC " : " ORDER BY p.name COLLATE NOCASE,p.rowid ";
            using var command = Command(PointSelect + " " + PointWhere + ordering + " LIMIT $limit OFFSET $offset;", null,
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            using var rows = command.ExecuteReader();
            var items = new List<PointDto>();
            while (rows.Read()) items.Add(ReadPoint(rows));
            return new(items.ToArray(), total, page, pageSize);
        }
    }

    public PointDetailDto GetPointDetail(string id)
    {
        lock (_gate)
        {
            var point = GetPoint(id);
            var inspections = ReadInspectionList(" WHERE n.point_id=$point ORDER BY n.inspection_date DESC,n.rowid DESC;", [("$point", id)]);
            var issues = ReadIssueList(" WHERE n.point_id=$point AND x.status<>'closed' ORDER BY x.deadline,x.rowid;", Today, [("$point", id)]);
            var campaigns = ReadCampaigns("WHERE c.point_id=$point ORDER BY c.campaign_date DESC,c.rowid DESC;", [("$point", id)]);
            return new(point, inspections, issues, ReadPhotos(pointId: id), campaigns, MergedTarget(id));
        }
    }

    public PointDto SavePoint(string? id, PointWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            if (id is null && request.Temporary) throw new ApiException(400, "临时点位请随排查或整治一起保存。");
            string actualId;
            using (var transaction = _db.BeginTransaction())
            {
                actualId = SavePointCore(id, request, transaction);
                transaction.Commit();
            }
            return GetPoint(actualId);
        }
    }

    private PointDto ResolveRecordPoint(string? id, PointWriteRequest? newPoint, SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(id) == (newPoint is null))
            throw new ApiException(400, "请选择点位或填写临时点位，不能同时提交。");
        var pointId = newPoint is null ? Text(id, "点位", 100, true)
            : SavePointCore(null, newPoint with { Frequency = "irregular", Disabled = false }, transaction, createTemporary: true);
        var point = GetPointInternal(pointId, transaction);
        RequireUnmergedPoint(pointId, transaction);
        if (point.Disabled) throw new ApiException(409, "该点位已停用，请先启用。");
        return point;
    }

    // A temporary point is disposable only when neither business records nor independent
    // use/unit/plan/merge records still refer to it. Archived records are references too.
    // Call under _gate before staging photo deletion; SQL mutations share one transaction.
    private bool ShouldRecycleTemporaryPoint(string pointId, string? inspectionId = null, string? campaignId = null)
    {
        if (!GetPointInternal(pointId).Temporary) return false;
        using var command = Command("""
            SELECT (SELECT COUNT(*) FROM inspections WHERE point_id=$point AND id<>$inspection)
                 + (SELECT COUNT(*) FROM campaigns WHERE point_id=$point AND id<>$campaign)
                 + (SELECT COUNT(*) FROM community_records WHERE parent_id=$point AND kind IN ('unit','use','point-merge'))
                 + (SELECT COUNT(*) FROM community_records r WHERE r.kind='plan'
                    AND EXISTS (SELECT 1 FROM json_each(r.body,'$.pointIds') j WHERE j.value=$point));
            """, null, ("$point", pointId), ("$inspection", inspectionId ?? ""), ("$campaign", campaignId ?? ""));
        return Convert.ToInt32(command.ExecuteScalar()) == 0;
    }

    private void RecycleTemporaryPoint(string pointId, SqliteTransaction transaction)
    {
        using (var shape = Command("DELETE FROM map_shapes WHERE kind='point' AND record_id=$point;", transaction, ("$point", pointId))) shape.ExecuteNonQuery();
        using (var history = Command("DELETE FROM community_history WHERE kind='point' AND record_id=$point;", transaction, ("$point", pointId))) history.ExecuteNonQuery();
        using var point = Command("DELETE FROM points WHERE id=$point;", transaction, ("$point", pointId));
        RequireChanged(point.ExecuteNonQuery(), "临时场所");
    }

    private string SavePointCore(string? id, PointWriteRequest request, SqliteTransaction transaction, bool createTemporary = false)
    {
        var existing = id is null ? null : GetPointInternal(id, transaction);
        if (id is not null) RequireUnmergedPoint(id, transaction);
        if (existing is not null) CheckRevision(request.Revision, existing.Revision);
        var temporary = createTemporary || existing?.Temporary == true;
        var name = Text(request.Name, "点位名称", 200, true);
        var community = RequireCommunity(Text(request.CommunityId, "所属社区", 100, true), transaction);
        var type = RequirePointType(Text(request.TypeId, "点位类型", 100, true), transaction);
        ValidateFrequency(request.Frequency);
        var (longitude, latitude) = ValidateCoordinates(request.Longitude, request.Latitude);
        if (request.Shape is not null)
        {
            ValidateShape(request.Shape);
            (longitude, latitude) = ValidateCoordinates(request.Shape.Coordinates[0][0], request.Shape.Coordinates[0][1]);
        }
        var actualId = id ?? NewId();
        (string Name, object? Value)[] parameters = [("$id", actualId), ("$name", name), ("$community", community.Id), ("$type", type.Id),
            ("$address", Text(request.Address, "详细地址", 1000, true)), ("$contact", Text(request.Contact, "联系人", 100)),
            ("$phone", Text(request.Phone, "电话", 100)), ("$frequency", temporary ? "irregular" : request.Frequency),
            ("$notes", Text(request.Notes, "备注", 8000)), ("$disabled", request.Disabled ? 1 : 0),
            ("$longitude", longitude), ("$latitude", latitude), ("$temporary", temporary ? 1 : 0), ("$color",MapColor(request.Color)),
            ("$extra", NormalizeFieldValues(type.Fields ?? [], request.ExtraValues, request.PhotoIds).GetRawText())];
        using (var command = Command(id is null
            ? "INSERT INTO points(id,name,community_id,type_id,address,contact,phone,frequency,notes,disabled,longitude,latitude,temporary,color,extra_values) VALUES($id,$name,$community,$type,$address,$contact,$phone,$frequency,$notes,$disabled,$longitude,$latitude,$temporary,$color,$extra);"
            : "UPDATE points SET name=$name,community_id=$community,type_id=$type,address=$address,contact=$contact,phone=$phone,frequency=$frequency,notes=$notes,disabled=$disabled,longitude=$longitude,latitude=$latitude,temporary=$temporary,color=$color,extra_values=$extra WHERE id=$id;", transaction, parameters))
            RequireChanged(command.ExecuteNonQuery(), "点位");
        AttachPhotos(request.PhotoIds, "point", null, null, transaction, pointId: actualId);
        if (request.Shape is not null)
        {
            ValidateShape(request.Shape);
            using var previous = Command("SELECT COALESCE((SELECT revision FROM map_shapes WHERE kind='point' AND record_id=$id),0);", transaction, ("$id",actualId));
            var version = Convert.ToInt32(previous.ExecuteScalar());
            if (version != request.ShapeRevision) throw new ApiException(409,"地图位置已被修改，请刷新后再保存。");
            using var shape = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES('point',$id,$shape,$revision) ON CONFLICT(kind,record_id) DO UPDATE SET shape=excluded.shape,revision=excluded.revision;", transaction,
                ("$id",actualId),("$shape",System.Text.Json.JsonSerializer.Serialize(request.Shape,CommunityJsonContext.Default.GeoShape)),("$revision",version+1));
            shape.ExecuteNonQuery();
        }
        else if (existing is not null && (longitude != existing.Longitude || latitude != existing.Latitude))
        {
            using var previous = Command("SELECT COALESCE((SELECT revision FROM map_shapes WHERE kind='point' AND record_id=$id),0);", transaction, ("$id", actualId));
            var version = Convert.ToInt32(previous.ExecuteScalar());
            if (version != request.ShapeRevision) throw new ApiException(409, "地图位置已被修改，请刷新后再保存。");
            if (longitude is not null && latitude is not null)
            {
                var replacement = new GeoShape("Point", [[longitude.Value, latitude.Value]]);
                using var shape = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES('point',$id,$shape,$revision) ON CONFLICT(kind,record_id) DO UPDATE SET shape=excluded.shape,revision=excluded.revision;", transaction,
                    ("$id", actualId), ("$shape", System.Text.Json.JsonSerializer.Serialize(replacement, CommunityJsonContext.Default.GeoShape)), ("$revision", version + 1));
                shape.ExecuteNonQuery();
            }
            else
            {
                using var remove = Command("DELETE FROM map_shapes WHERE kind='point' AND record_id=$id;", transaction, ("$id", actualId));
                remove.ExecuteNonQuery();
            }
        }
        AddCommunityHistory("point", actualId, existing is null ? "" : System.Text.Json.JsonSerializer.Serialize(existing, AppJsonContext.Default.PointDto), System.Text.Json.JsonSerializer.Serialize(request, AppJsonContext.Default.PointWriteRequest), id is null ? "新增场所" : "修改场所", transaction);
        return actualId;
    }

    // Photo completion must never replay a stale point edit. This operation is
    // additive/idempotent for this owner; removal remains an explicit DELETE.
    public PointDto AttachPointPhotos(string id, PointPhotosRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetPointInternal(id);
            RequireUnmergedPoint(id);
            if (request.PhotoIds is null) throw new ApiException(400, "请提供照片编号列表。");
            using (var transaction = _db.BeginTransaction())
            {
                AttachPhotos(request.PhotoIds, "point", null, null, transaction, pointId: id);
                transaction.Commit();
            }
            return GetPointInternal(id);
        }
    }

    public PointDto SetPointDisabled(string id, PointStatusRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var before = GetPointInternal(id);
            RequireUnmergedPoint(id);
            CheckRevision(request.Revision, before.Revision);
            if (request.Disabled is null) throw new ApiException(400, "请提供停用状态。");
            using var transaction = _db.BeginTransaction();
            using var command = Command("UPDATE points SET disabled=$disabled WHERE id=$id;", transaction,
                ("$id", id), ("$disabled", request.Disabled.Value ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "点位");
            AddCommunityHistory("point", id, System.Text.Json.JsonSerializer.Serialize(before, AppJsonContext.Default.PointDto), request.Disabled.Value ? "{\"disabled\":true}" : "{\"disabled\":false}", request.Disabled.Value ? "停用场所" : "恢复场所", transaction);
            transaction.Commit();
            return GetPointInternal(id);
        }
    }

    private static void ValidateFrequency(string frequency)
    {
        if (frequency is not ("weekly" or "monthly" or "quarterly" or "irregular")) throw new ApiException(400, "排查频次无效。");
    }

    private static (double? Longitude, double? Latitude) ValidateCoordinates(double? longitude, double? latitude)
    {
        if (longitude is null && latitude is null) return (null, null);
        if (longitude is null || latitude is null || !double.IsFinite(longitude.Value) || !double.IsFinite(latitude.Value) ||
            longitude is < -180 or > 180 || latitude is < -90 or > 90)
            throw new ApiException(400, "请输入「经度,纬度」，如 88.893426,29.274331。");
        if (longitude is >= 3 and <= 54 && latitude is >= 73 and <= 136)
            throw new ApiException(400, "看起来顺序反了，应为 经度,纬度。");
        return (Math.Round(longitude.Value, 6, MidpointRounding.AwayFromZero), Math.Round(latitude.Value, 6, MidpointRounding.AwayFromZero));
    }

    public PointDto PromotePoint(string id, PointPromoteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var point = GetPoint(id);
            RequireUnmergedPoint(id);
            if (!point.Temporary) throw new ApiException(409, "该场所已经是常态场所，请刷新。");
            ValidateFrequency(request.Frequency);
            using var transaction = _db.BeginTransaction();
            using var command = Command("UPDATE points SET temporary=0,frequency=$frequency WHERE id=$id;", transaction,
                ("$id", id), ("$frequency", request.Frequency));
            command.ExecuteNonQuery();
            AddCommunityHistory("point", id, "", "{}", "转为常态场所", transaction);
            transaction.Commit();
            return GetPoint(id);
        }
    }

    private PointDto[] ExportPoints(string[]? pointIds)
    {
        if (pointIds is null || pointIds.Length == 0) return [];
        if (pointIds.Length > 200) throw new ApiException(400, "一次最多选择 200 个点位。");
        return pointIds.Select(id => Text(id, "点位", 100, true)).Distinct(StringComparer.Ordinal)
            .Select(id => GetPointInternal(id)).ToArray();
    }

    private static string LedgerPointSql(int count, string column) => count == 0 ? "" :
        " AND " + column + " IN (" + string.Join(",", Enumerable.Range(0, count).Select(i => "$point" + i)) + ")";

    private static (string Name, object? Value)[] LedgerPointParameters(string[]? pointIds) =>
        (pointIds ?? []).Select((id, i) => ("$point" + i, (object?)id)).ToArray();

    public void DeletePoint(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetPoint(id);
            RequireUnmergedPoint(id);
            using (var used = Command("SELECT (SELECT COUNT(*) FROM inspections WHERE point_id=$id) + (SELECT COUNT(*) FROM campaigns WHERE point_id=$id) + (SELECT COUNT(*) FROM community_records WHERE parent_id=$id AND kind IN('unit','use')) + (SELECT COUNT(*) FROM community_records r,json_each(r.body,'$.pointIds') j WHERE r.kind='plan' AND j.value=$id);", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该场所已有使用、检查或工作安排记录，请改为停用以保留资料。");
            ExecuteWithPhotoDeletion(ReadPhotos(pointId: id).Select(photo => photo.Id).ToArray(), transaction =>
            {
                using (var shape = Command("DELETE FROM map_shapes WHERE kind='point' AND record_id=$id;", transaction, ("$id", id))) shape.ExecuteNonQuery();
                using var command = Command("DELETE FROM points WHERE id=$id;", transaction, ("$id", id));
                RequireChanged(command.ExecuteNonQuery(), "点位");
            });
        }
    }
}
