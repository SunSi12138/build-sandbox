using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string PointSelect = """
        SELECT p.id,p.name,p.community_id,c.name,p.type_id,t.name,p.address,p.contact,p.phone,p.frequency,p.notes,p.disabled,
        (SELECT MAX(n.inspection_date) FROM inspections n WHERE n.point_id=p.id AND n.inspection_date<=$today),
        (SELECT COUNT(*) FROM issues x JOIN inspections n ON n.id=x.inspection_id WHERE n.point_id=p.id AND x.status<>'closed'),
        p.longitude,p.latitude,p.temporary,
        (SELECT ph.id FROM photos ph WHERE ph.point_id=p.id ORDER BY ph.uploaded_at,ph.rowid LIMIT 1),
        (SELECT COUNT(*) FROM photos ph WHERE ph.point_id=p.id)
        FROM points p JOIN communities c ON c.id=p.community_id JOIN point_types t ON t.id=p.type_id
        """;
    private const string PointWhere = """
        WHERE ($q='' OR p.name LIKE $like OR p.address LIKE $like OR p.contact LIKE $like)
        AND ($community='' OR p.community_id=$community) AND ($type='' OR p.type_id=$type)
        AND ($disabled='all' OR p.disabled=$inactive) AND ($temporary='all' OR p.temporary=$isTemporary)
        AND ($missed=0 OR (p.disabled=0 AND p.temporary=0 AND p.frequency<>'irregular' AND NOT EXISTS (
            SELECT 1 FROM inspections n WHERE n.point_id=p.id AND n.inspection_date<=$today
            AND n.inspection_date >= CASE p.frequency WHEN 'weekly' THEN $week WHEN 'monthly' THEN $month WHEN 'quarterly' THEN $quarter END)))
        """;

    private static PointDto ReadPoint(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetInt32(11) != 0, NullableString(row, 12), row.GetInt32(13), row.IsDBNull(14) ? null : row.GetDouble(14),
        row.IsDBNull(15) ? null : row.GetDouble(15), row.GetInt32(16) != 0, NullableString(row, 17), row.GetInt32(18));

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
            return new(point, inspections, issues, ReadPhotos(pointId: id));
        }
    }

    public PointDto SavePoint(string? id, PointWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            if (id is null && request.Temporary) throw new ApiException(400, "临时点位请随排查一起保存。");
            string actualId;
            using (var transaction = _db.BeginTransaction())
            {
                actualId = SavePointCore(id, request, transaction);
                transaction.Commit();
            }
            return GetPoint(actualId);
        }
    }

    private string SavePointCore(string? id, PointWriteRequest request, SqliteTransaction transaction, bool createTemporary = false)
    {
        var existing = id is null ? null : GetPointInternal(id, transaction);
        var temporary = createTemporary || existing?.Temporary == true;
        var name = Text(request.Name, "点位名称", 200, true);
        var community = RequireCommunity(Text(request.CommunityId, "所属社区", 100, true), transaction);
        var type = RequirePointType(Text(request.TypeId, "点位类型", 100, true), transaction);
        ValidateFrequency(request.Frequency);
        var (longitude, latitude) = ValidateCoordinates(request.Longitude, request.Latitude);
        var actualId = id ?? NewId();
        (string Name, object? Value)[] parameters = [("$id", actualId), ("$name", name), ("$community", community.Id), ("$type", type.Id),
            ("$address", Text(request.Address, "详细地址", 1000, true)), ("$contact", Text(request.Contact, "联系人", 100)),
            ("$phone", Text(request.Phone, "电话", 100)), ("$frequency", temporary ? "irregular" : request.Frequency),
            ("$notes", Text(request.Notes, "备注", 8000)), ("$disabled", request.Disabled ? 1 : 0),
            ("$longitude", longitude), ("$latitude", latitude), ("$temporary", temporary ? 1 : 0)];
        using (var command = Command(id is null
            ? "INSERT INTO points(id,name,community_id,type_id,address,contact,phone,frequency,notes,disabled,longitude,latitude,temporary) VALUES($id,$name,$community,$type,$address,$contact,$phone,$frequency,$notes,$disabled,$longitude,$latitude,$temporary);"
            : "UPDATE points SET name=$name,community_id=$community,type_id=$type,address=$address,contact=$contact,phone=$phone,frequency=$frequency,notes=$notes,disabled=$disabled,longitude=$longitude,latitude=$latitude,temporary=$temporary WHERE id=$id;", transaction, parameters))
            RequireChanged(command.ExecuteNonQuery(), "点位");
        AttachPhotos(request.PhotoIds, "point", null, null, transaction, pointId: actualId);
        return actualId;
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
            ValidateFrequency(request.Frequency);
            using var command = Command("UPDATE points SET temporary=0,frequency=$frequency WHERE id=$id;", null,
                ("$id", id), ("$frequency", request.Frequency));
            command.ExecuteNonQuery();
            return GetPoint(id);
        }
    }

    public void DeletePoint(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetPoint(id);
            using (var used = Command("SELECT COUNT(*) FROM inspections WHERE point_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该点位已有历史排查，不能删除；请停用点位。");
            ExecuteWithPhotoDeletion(ReadPhotos(pointId: id).Select(photo => photo.Id).ToArray(), transaction =>
            {
                using var command = Command("DELETE FROM points WHERE id=$id;", transaction, ("$id", id));
                RequireChanged(command.ExecuteNonQuery(), "点位");
            });
        }
    }
}
