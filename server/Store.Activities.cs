using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    public ActivityTypeDto[] GetActivityTypes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,template,built_in FROM activity_types ORDER BY rowid;");
            using var rows = command.ExecuteReader(); var list = new List<ActivityTypeDto>();
            while (rows.Read()) list.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3) != 0));
            return list.ToArray();
        }
    }
    private ActivityTypeDto RequireActivityType(string? id, SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ApiException(400, "请选择活动类型。");
        using var command = Command("SELECT id,name,template,built_in FROM activity_types WHERE id=$id;", tx, ("$id", id));
        using var row = command.ExecuteReader();
        if (!row.Read()) throw new ApiException(400, "活动类型不存在，请重新选择。");
        return new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetInt32(3) != 0);
    }
    public ActivityTypeDto SaveActivityType(string? id, ActivityTypeWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var name = Text(request.Name, "活动类型", 100, true);
            using (var duplicate = Command("SELECT COUNT(*) FROM activity_types WHERE name=$name AND id<>$id;", null, ("$name", name), ("$id", id ?? "")))
                if (Convert.ToInt32(duplicate.ExecuteScalar()) > 0) throw new ApiException(409, "已有同名活动类型。");
            var actualId = id ?? NewId();
            using var command = Command(id is null ? "INSERT INTO activity_types(id,name) VALUES($id,$name);" : "UPDATE activity_types SET name=$name WHERE id=$id;", null, ("$id", actualId), ("$name", name));
            RequireChanged(command.ExecuteNonQuery(), "活动类型"); return RequireActivityType(actualId);
        }
    }
    public void DeleteActivityType(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            if (RequireActivityType(id).BuiltIn) throw new ApiException(409, "内置整治类型不能删除。");
            using (var used = Command("SELECT COUNT(*) FROM activities WHERE type_id=$id;", null, ("$id", id)))
                if (Convert.ToInt32(used.ExecuteScalar()) > 0) throw new ApiException(409, "该类型已被活动记录引用，不能删除。");
            using var command = Command("DELETE FROM activity_types WHERE id=$id;", null, ("$id", id)); RequireChanged(command.ExecuteNonQuery(), "活动类型");
        }
    }
    private const string ActivitySelect = """
        SELECT a.id,a.name,a.type_id,a.type_name,a.community_id,a.community_name,a.location,
               a.activity_date,a.end_date,a.organizer,a.participants,a.participant_count,a.content,a.outcome,a.notes,
               (SELECT COUNT(*) FROM photos p WHERE p.activity_id=a.id),a.template,a.point_id,a.point_name,COALESCE(pt.temporary,0),
               r.type_id,r.type_name,r.vehicle_count,r.garbage_tons,r.area_sqm
        FROM activities a LEFT JOIN points pt ON pt.id=a.point_id LEFT JOIN activity_remediations r ON r.activity_id=a.id
        """;
    private const string ActivityWhere = """
        WHERE ($q='' OR a.name LIKE $like OR a.location LIKE $like OR a.content LIKE $like OR a.point_name LIKE $like)
          AND ($community='' OR a.community_id=$community) AND ($type='' OR a.type_id=$type)
          AND ($from='' OR a.activity_date>=$from) AND ($to='' OR a.activity_date<=$to)
          AND ($point='' OR a.point_id=$point) AND ($template='' OR a.template=$template)
          AND ($remediation='' OR EXISTS(SELECT 1 FROM activity_remediations d WHERE d.activity_id=a.id AND d.type_id=$remediation))
          AND ($matter='' OR EXISTS(SELECT 1 FROM activity_matters l WHERE l.activity_id=a.id AND l.matter_id=$matter))
        """;
    private ActivityDto ReadActivity(SqliteDataReader row) => new(
        row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), NullableString(row, 4), row.GetString(5), row.GetString(6),
        row.GetString(7), NullableString(row, 8), row.GetString(9), row.GetString(10), row.IsDBNull(11) ? null : row.GetInt32(11),
        row.GetString(12), row.GetString(13), row.GetString(14), row.GetInt32(15), row.GetString(16), NullableString(row, 17), row.GetString(18), row.GetInt32(19) != 0,
        row.IsDBNull(20) ? null : new(row.GetString(20), row.GetString(21), row.IsDBNull(22) ? null : row.GetInt32(22), row.IsDBNull(23) ? null : row.GetDecimal(23), row.IsDBNull(24) ? null : row.GetDecimal(24)))
        { Tags = ReadTags("activity", row.GetString(0)), Matters = ReadMatterLinks("activity", row.GetString(0)),
          RelatedIssues = ReadIssueList("WHERE EXISTS(SELECT 1 FROM activity_issues l WHERE l.issue_id=x.id AND l.activity_id=$activity);", Today, [("$activity", row.GetString(0))]) };
    private ActivityDto[] ReadActivities(string suffix, (string Name, object? Value)[] parameters)
    {
        using var command = Command(ActivitySelect + " " + suffix, null, parameters);
        using var rows = command.ExecuteReader(); var list = new List<ActivityDto>();
        while (rows.Read()) list.Add(ReadActivity(rows));
        return list.ToArray();
    }
    public ActivityDto GetActivity(string id)
    {
        lock (_gate) return ReadActivities("WHERE a.id=$id;", [("$id", id)]).FirstOrDefault() ?? throw new ApiException(404, "活动记录不存在。");
    }
    public ActivityDetailDto GetActivityDetail(string id) { lock (_gate) return new(GetActivity(id), ReadPhotos(activityId: id)); }
    private static (string Name, object? Value)[] ActivityParameters(ActivityQuery query)
    {
        var from = OptionalDate(query.From, "开始日期"); var to = OptionalDate(query.To, "结束日期"); CheckDateRange(from, to);
        var q = Text(query.Q, "搜索内容", 200);
        if (query.Template is not (null or "" or "general" or "remediation")) throw new ApiException(400, "活动模板筛选无效。");
        return [("$q", q), ("$like", "%" + q + "%"), ("$community", query.CommunityId ?? ""), ("$type", query.TypeId ?? ""),
            ("$from", from ?? ""), ("$to", to ?? ""), ("$point", query.PointId ?? ""), ("$template", query.Template ?? ""),
            ("$remediation", query.RemediationTypeId ?? ""), ("$matter", query.MatterId ?? ""), .. TagParameters(query.TagIds)];
    }
    public PageDto<ActivityDto> ListActivities(ActivityQuery query)
    {
        lock (_gate)
        {
            var parameters = ActivityParameters(query); var (page, pageSize) = Paging(query.Page, query.PageSize);
            var where = ActivityWhere + TagFilterSql("activity", "a.id", query.TagIds);
            using var count = Command("SELECT COUNT(*) FROM activities a " + where, null, parameters);
            var total = Convert.ToInt32(count.ExecuteScalar());
            return new(ReadActivities(where + " ORDER BY a.activity_date DESC,a.rowid DESC LIMIT $limit OFFSET $offset;", [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]), total, page, pageSize);
        }
    }
    public ActivityDto[] GetLedgerActivities(string from, string to, string? communityId = null, string? typeId = null, ActivityQuery? filters = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(communityId)) _ = RequireCommunity(communityId);
            if (!string.IsNullOrWhiteSpace(typeId)) _ = RequireActivityType(typeId);
            var query = (filters ?? new()) with { CommunityId = communityId, TypeId = typeId, From = from, To = to };
            return ReadActivities(ActivityWhere + TagFilterSql("activity", "a.id", query.TagIds) + " ORDER BY a.activity_date,a.rowid;", ActivityParameters(query));
        }
    }
    public ActivityDetailDto SaveActivity(string? id, ActivityWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var existing = id is null ? null : GetActivity(id); var type = RequireActivityType(request.TypeId);
            if (existing is not null && existing.Template != type.Template) throw new ApiException(409, "已保存的活动不能更换填报模板。");
            var remediation = request.Remediation;
            if (type.Template == "remediation" ? remediation is null : remediation is not null) throw new ApiException(400, "活动类型与整治填报内容不一致。");
            if (remediation is not null && (request.PhotoIds?.Length ?? 0) > 0) throw new ApiException(400, "整治照片请按前、中、后阶段提交。");
            if (existing?.Template == "remediation" && (request.NewPoint is not null || request.PointId != existing.PointId)) throw new ApiException(400, "不能更换已保存整治的点位。");
            var date = DateText(request.Date, "开展日期"); var end = OptionalDate(request.EndDate, "结束日期"); CheckDateRange(date, end);
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "开展日期不能晚于今天。");
            if (request.ParticipantCount is < 0 or > 100000000 || remediation?.VehicleCount is < 0 or > 100000000 || remediation?.GarbageTons is < 0 or > 1000000000000m || remediation?.AreaSqm is < 0 or > 1000000000000m) throw new ApiException(400, "填报数量超出允许范围。");
            var photos = remediation is null ? request.PhotoIds ?? [] : [.. remediation.BeforePhotoIds ?? [], .. remediation.ProcessPhotoIds ?? [], .. remediation.AfterPhotoIds ?? []];
            if (remediation is null && photos.Length > 20) throw new ApiException(400, "活动照片最多 20 张。");
            if (photos.Distinct(StringComparer.Ordinal).Count() != photos.Length) throw new ApiException(400, "照片不能重复关联。");
            var organizer = Text(request.Organizer ?? GetSettings().IssuingUnit, "组织单位", 200);
            var actualId = id ?? NewId();
            var removed = existing is null ? [] : ReadPhotos(activityId: actualId).Where(photo => !photos.Contains(photo.Id, StringComparer.Ordinal)).Select(photo => photo.Id).ToArray();
            ExecuteWithPhotoDeletion(removed, tx =>
            {
                PointDto? point = null;
                if (type.Template == "remediation" || request.NewPoint is not null || !string.IsNullOrWhiteSpace(request.PointId)) point = existing?.PointId is not null && request.NewPoint is null && request.PointId == existing.PointId ? GetPointInternal(existing.PointId, tx) : ResolveRecordPoint(request.PointId, request.NewPoint, tx);
                var community = point is not null ? RequireCommunity(point.CommunityId, tx) : string.IsNullOrWhiteSpace(request.CommunityId) ? null : RequireCommunity(request.CommunityId, tx);
                var samePoint = existing is not null && existing.PointId == point?.Id;
                using (var command = Command(id is null ? """
                    INSERT INTO activities(id,name,type_id,type_name,template,point_id,point_name,community_id,community_name,location,activity_date,end_date,organizer,participants,participant_count,content,outcome,notes)
                    VALUES($id,$name,$type,$typeName,$template,$point,$pointName,$community,$communityName,$location,$date,$end,$organizer,$participants,$people,$content,$outcome,$notes);
                    """ : """
                    UPDATE activities SET name=$name,type_id=$type,type_name=$typeName,template=$template,point_id=$point,point_name=$pointName,community_id=$community,community_name=$communityName,location=$location,
                    activity_date=$date,end_date=$end,organizer=$organizer,participants=$participants,participant_count=$people,content=$content,outcome=$outcome,notes=$notes WHERE id=$id;
                    """, tx, ("$id", actualId), ("$name", Text(request.Name, "活动名称", 200, true)), ("$type", type.Id), ("$typeName", existing?.TypeId == type.Id ? existing.TypeName : type.Name), ("$template", type.Template),
                    ("$point", point?.Id), ("$pointName", samePoint ? existing!.PointName : point?.Name ?? ""), ("$community", community?.Id),
                    ("$communityName", existing is not null && existing.CommunityId == community?.Id ? existing.CommunityName : community?.Name ?? ""),
                    ("$location", type.Template == "remediation" ? samePoint ? existing!.Location : point!.Address : Text(request.Location ?? point?.Address, "活动地点", 1000)),
                    ("$date", date), ("$end", end), ("$organizer", organizer), ("$participants", Text(request.Participants, "参加单位及人员", 2000)),
                    ("$people", request.ParticipantCount), ("$content", Text(request.Content, "开展情况", 12000, true)), ("$outcome", Text(request.Outcome, "活动成效", 12000)), ("$notes", Text(request.Notes, "备注", 8000)))) RequireChanged(command.ExecuteNonQuery(), "活动记录");
                if (remediation is not null)
                {
                    var category = RequireCampaignType(remediation.TypeId, tx);
                    using var detail = Command("""
                        INSERT INTO activity_remediations(activity_id,type_id,type_name,vehicle_count,garbage_tons,area_sqm) VALUES($id,$type,$name,$vehicles,$garbage,$area)
                        ON CONFLICT(activity_id) DO UPDATE SET type_id=$type,type_name=$name,vehicle_count=$vehicles,garbage_tons=$garbage,area_sqm=$area;
                        """, tx, ("$id", actualId), ("$type", category.Id), ("$name", existing?.Remediation?.TypeId == category.Id ? existing.Remediation.TypeName : category.Name),
                        ("$vehicles", remediation.VehicleCount), ("$garbage", remediation.GarbageTons is decimal tons ? decimal.Round(tons, 2, MidpointRounding.AwayFromZero) : null), ("$area", remediation.AreaSqm is decimal area ? decimal.Round(area, 2, MidpointRounding.AwayFromZero) : null)); detail.ExecuteNonQuery();
                    AttachPhotos(remediation.BeforePhotoIds, "before", null, null, tx, activityId: actualId);
                    AttachPhotos(remediation.ProcessPhotoIds, "process", null, null, tx, activityId: actualId);
                    AttachPhotos(remediation.AfterPhotoIds, "after", null, null, tx, activityId: actualId);
                }
                else AttachPhotos(photos, "activity", null, null, tx, activityId: actualId);
                ReplaceTags("activity", actualId, request.TagIds, tx);
                ReplaceMatterLinks("activity", actualId, request.MatterIds, tx);
                ReplaceActivityIssues(actualId, request.IssueIds, tx);
            });
            return GetActivityDetail(actualId);
        }
    }
    public void DeleteActivity(string id)
    {
        lock (_gate)
        {
            PrepareWrite(); var activity = GetActivity(id);
            var deletePoint = activity.PointId is not null && IsLastTemporaryRecord(activity.PointId, activityId: id);
            var photos = ReadPhotos(activityId: id).Concat(deletePoint ? ReadPhotos(pointId: activity.PointId) : []).Select(photo => photo.Id).ToArray();
            ExecuteWithPhotoDeletion(photos, tx =>
            {
                using (var command = Command("DELETE FROM activities WHERE id=$id;", tx, ("$id", id))) RequireChanged(command.ExecuteNonQuery(), "活动记录");
                if (deletePoint) { using var point = Command("DELETE FROM points WHERE id=$id;", tx, ("$id", activity.PointId)); point.ExecuteNonQuery(); }
            });
        }
    }
}
