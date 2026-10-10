using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string PointSelect = """
        SELECT p.id,p.name,p.community_id,c.name,p.type_id,t.name,p.address,p.contact,p.phone,p.frequency,p.notes,p.disabled,
        (SELECT MAX(n.inspection_date) FROM inspections n WHERE n.point_id=p.id AND n.inspection_date<=$today),
        (SELECT COUNT(*) FROM issues x JOIN inspections n ON n.id=x.inspection_id WHERE n.point_id=p.id AND x.status<>'closed')
        FROM points p JOIN communities c ON c.id=p.community_id JOIN point_types t ON t.id=p.type_id
        """;
    private const string PointWhere = """
        WHERE ($q='' OR p.name LIKE $like OR p.address LIKE $like OR p.contact LIKE $like)
        AND ($community='' OR p.community_id=$community) AND ($type='' OR p.type_id=$type)
        AND ($disabled='all' OR p.disabled=$inactive)
        """;

    private static PointDto ReadPoint(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetInt32(11) != 0, NullableString(row, 12), row.GetInt32(13));

    public PointDto GetPoint(string id)
    {
        lock (_gate)
        {
            using var command = Command(PointSelect + " WHERE p.id=$id;", null, ("$id", id), ("$today", Today));
            using var row = command.ExecuteReader();
            if (!row.Read()) throw new ApiException(404, "点位不存在。");
            return ReadPoint(row);
        }
    }

    public PageDto<PointDto> ListPoints(PointQuery query)
    {
        lock (_gate)
        {
            if (query.Disabled is not ("all" or "true" or "false")) throw new ApiException(400, "停用筛选必须是 all、true 或 false。");
            var q = Text(query.Q, "搜索内容", 200);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            (string Name, object? Value)[] parameters = [("$q", q), ("$like", "%" + q + "%"), ("$community", query.CommunityId ?? ""),
                ("$type", query.TypeId ?? ""), ("$disabled", query.Disabled), ("$inactive", query.Disabled == "true" ? 1 : 0)];
            int total;
            using (var count = Command("SELECT COUNT(*) FROM points p " + PointWhere, null, parameters)) total = Convert.ToInt32(count.ExecuteScalar());
            var ordering = query.Recent ? " ORDER BY 13 DESC,p.rowid DESC " : " ORDER BY p.name COLLATE NOCASE,p.rowid ";
            using var command = Command(PointSelect + " " + PointWhere + ordering + " LIMIT $limit OFFSET $offset;", null,
                [.. parameters, ("$today", Today), ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
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
            return new(point, inspections, issues);
        }
    }

    public PointDto SavePoint(string? id, PointWriteRequest request)
    {
        lock (_gate)
        {
            if (id is not null) _ = GetPoint(id);
            var name = Text(request.Name, "点位名称", 200, true);
            var community = RequireCommunity(Text(request.CommunityId, "所属社区", 100, true));
            var type = RequirePointType(Text(request.TypeId, "点位类型", 100, true));
            if (request.Frequency is not ("weekly" or "monthly" or "quarterly" or "irregular")) throw new ApiException(400, "排查频次无效。");
            var actualId = id ?? NewId();
            (string Name, object? Value)[] parameters = [("$id", actualId), ("$name", name), ("$community", community.Id), ("$type", type.Id),
                ("$address", Text(request.Address, "详细地址", 1000, true)), ("$contact", Text(request.Contact, "联系人", 100)),
                ("$phone", Text(request.Phone, "电话", 100)), ("$frequency", request.Frequency), ("$notes", Text(request.Notes, "备注", 8000)), ("$disabled", request.Disabled ? 1 : 0)];
            using var command = Command(id is null
                ? "INSERT INTO points(id,name,community_id,type_id,address,contact,phone,frequency,notes,disabled) VALUES($id,$name,$community,$type,$address,$contact,$phone,$frequency,$notes,$disabled);"
                : "UPDATE points SET name=$name,community_id=$community,type_id=$type,address=$address,contact=$contact,phone=$phone,frequency=$frequency,notes=$notes,disabled=$disabled WHERE id=$id;", null, parameters);
            RequireChanged(command.ExecuteNonQuery(), "点位");
            return GetPoint(actualId);
        }
    }

    public void DeletePoint(string id)
    {
        lock (_gate)
        {
            using (var used = Command("SELECT COUNT(*) FROM inspections WHERE point_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该点位已有历史排查，不能删除；请停用点位以保留工作记录。");
            using var command = Command("DELETE FROM points WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "点位");
        }
    }
}
