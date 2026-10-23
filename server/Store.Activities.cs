using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    public ActivityTypeDto[] GetActivityTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name FROM activity_types ORDER BY rowid;");
            using var rows = command.ExecuteReader();
            var list = new List<ActivityTypeDto>();
            while (rows.Read()) list.Add(new(rows.GetString(0), rows.GetString(1)));
            return list.ToArray();
        }
    }

    private ActivityTypeDto RequireActivityType(string? id, SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ApiException(400, "请选择活动类型。");
        using var command = Command("SELECT id,name FROM activity_types WHERE id=$id;", tx, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "活动类型不存在，请重新选择。");
        return new(row.GetString(0), row.GetString(1));
    }

    public ActivityTypeDto SaveActivityType(string? id, ActivityTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var name = Text(request.Name, "活动类型", 100, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM activity_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名活动类型。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO activity_types(id,name) VALUES($id,$name);" : "UPDATE activity_types SET name=$name WHERE id=$id;", null,
                ("$id", actualId), ("$name", name));
            RequireChanged(command.ExecuteNonQuery(), "活动类型");
            return new(actualId, name);
        }
    }

    public void DeleteActivityType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using (var used = Command("SELECT COUNT(*) FROM activities WHERE type_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类型已被活动记录引用，不能删除。");
            using var command = Command("DELETE FROM activity_types WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "活动类型");
        }
    }

    private const string ActivitySelect = """
        SELECT a.id,a.name,a.type_id,a.type_name,a.community_id,a.community_name,a.location,
               a.activity_date,a.end_date,a.organizer,a.participants,a.participant_count,a.content,a.outcome,a.notes,
               (SELECT COUNT(*) FROM photos p WHERE p.activity_id=a.id)
        FROM activities a
        """;
    private const string ActivityWhere = """
        WHERE ($q='' OR a.name LIKE $like OR a.location LIKE $like OR a.content LIKE $like)
          AND ($community='' OR a.community_id=$community)
          AND ($type='' OR a.type_id=$type)
          AND ($from='' OR a.activity_date>=$from)
          AND ($to='' OR a.activity_date<=$to)
        """;

    private static ActivityDto ReadActivity(SqliteDataReader row) => new(
        row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), NullableString(row, 4), row.GetString(5),
        row.GetString(6), row.GetString(7), NullableString(row, 8), row.GetString(9), row.GetString(10),
        row.IsDBNull(11) ? null : row.GetInt32(11), row.GetString(12), row.GetString(13), row.GetString(14), row.GetInt32(15));

    private ActivityDto[] ReadActivities(string suffix, (string Name, object? Value)[] parameters)
    {
        using var command = Command(ActivitySelect + " " + suffix, null, parameters);
        using var rows = command.ExecuteReader();
        var list = new List<ActivityDto>();
        while (rows.Read()) list.Add(ReadActivity(rows));
        return list.ToArray();
    }

    public ActivityDto GetActivity(string id)
    {
        lock (_gate)
        {
            var items = ReadActivities("WHERE a.id=$id;", [("$id", id)]);
            return items.Length == 0 ? throw new ApiException(404, "活动记录不存在。") : items[0];
        }
    }

    public ActivityDetailDto GetActivityDetail(string id)
    {
        lock (_gate) return new(GetActivity(id), ReadPhotos(activityId: id));
    }

    private static (string Name, object? Value)[] ActivityParameters(ActivityQuery query)
    {
        var from = OptionalDate(query.From, "开始日期");
        var to = OptionalDate(query.To, "结束日期");
        CheckDateRange(from, to);
        var q = Text(query.Q, "搜索内容", 200);
        return [("$q", q), ("$like", "%" + q + "%"), ("$community", query.CommunityId ?? ""),
            ("$type", query.TypeId ?? ""), ("$from", from ?? ""), ("$to", to ?? "")];
    }

    public PageDto<ActivityDto> ListActivities(ActivityQuery query)
    {
        lock (_gate)
        {
            var parameters = ActivityParameters(query);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            int count;
            using (var command = Command("SELECT COUNT(*) FROM activities a " + ActivityWhere, null, parameters))
                count = Convert.ToInt32(command.ExecuteScalar());
            var items = ReadActivities(ActivityWhere + " ORDER BY a.activity_date DESC,a.rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, count, page, pageSize);
        }
    }

    public ActivityDto[] GetLedgerActivities(string from, string to, string? communityId = null, string? typeId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(communityId)) _ = RequireCommunity(communityId);
            if (!string.IsNullOrWhiteSpace(typeId)) _ = RequireActivityType(typeId);
            var parameters = ActivityParameters(new(CommunityId: communityId, TypeId: typeId, From: from, To: to));
            return ReadActivities(ActivityWhere + " ORDER BY a.activity_date,a.rowid;", parameters);
        }
    }

    public ActivityDetailDto SaveActivity(string? id, ActivityWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var existing = id is null ? null : GetActivity(id);
            var date = DateText(request.Date, "开展日期");
            var endDate = OptionalDate(request.EndDate, "结束日期");
            CheckDateRange(date, endDate);
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "开展日期不能晚于今天。");
            if (request.ParticipantCount is < 0 or > 100000000) throw new ApiException(400, "参加人数超出允许范围。");
            var name = Text(request.Name, "活动名称", 200, true);
            var content = Text(request.Content, "开展情况", 12000, true);
            var organizer = Text(request.Organizer ?? GetSettings().IssuingUnit, "组织单位", 200);
            var photoIds = request.PhotoIds ?? [];
            if (photoIds.Length > 20) throw new ApiException(400, "活动照片最多 20 张。");
            var actualId = id ?? NewId();
            var removed = existing is null ? [] : ReadPhotos(activityId: actualId)
                .Where(photo => !photoIds.Contains(photo.Id, StringComparer.Ordinal)).Select(photo => photo.Id).ToArray();
            ExecuteWithPhotoDeletion(removed, tx =>
            {
                var type = RequireActivityType(request.TypeId, tx);
                var community = string.IsNullOrWhiteSpace(request.CommunityId) ? null : RequireCommunity(request.CommunityId, tx);
                (string Name, object? Value)[] args = [
                    ("$id", actualId), ("$name", name), ("$type", type.Id),
                    ("$typeName", existing is not null && existing.TypeId == type.Id ? existing.TypeName : type.Name),
                    ("$community", community?.Id), ("$communityName", community is null ? "" :
                        existing is not null && existing.CommunityId == community.Id ? existing.CommunityName : community.Name),
                    ("$location", Text(request.Location, "活动地点", 300)),
                    ("$date", date), ("$end", endDate), ("$organizer", organizer),
                    ("$participants", Text(request.Participants, "参加单位及人员", 2000)),
                    ("$people", request.ParticipantCount), ("$content", content),
                    ("$outcome", Text(request.Outcome, "活动成效", 12000)),
                    ("$notes", Text(request.Notes, "备注", 8000))
                ];
                using (var command = Command(id is null ? """
                    INSERT INTO activities(id,name,type_id,type_name,community_id,community_name,location,activity_date,end_date,
                    organizer,participants,participant_count,content,outcome,notes)
                    VALUES($id,$name,$type,$typeName,$community,$communityName,$location,$date,$end,$organizer,$participants,$people,$content,$outcome,$notes);
                    """ : """
                    UPDATE activities SET name=$name,type_id=$type,type_name=$typeName,community_id=$community,community_name=$communityName,
                    location=$location,activity_date=$date,end_date=$end,organizer=$organizer,participants=$participants,
                    participant_count=$people,content=$content,outcome=$outcome,notes=$notes WHERE id=$id;
                    """, tx, args)) RequireChanged(command.ExecuteNonQuery(), "活动记录");
                AttachPhotos(photoIds, "activity", null, null, tx, activityId: actualId);
            });
            return GetActivityDetail(actualId);
        }
    }

    public void DeleteActivity(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetActivity(id);
            var photos = ReadPhotos(activityId: id).Select(photo => photo.Id).ToArray();
            ExecuteWithPhotoDeletion(photos, tx =>
            {
                using var command = Command("DELETE FROM activities WHERE id=$id;", tx, ("$id", id));
                RequireChanged(command.ExecuteNonQuery(), "活动记录");
            });
        }
    }
}
