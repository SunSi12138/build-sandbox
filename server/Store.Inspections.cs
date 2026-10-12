using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string InspectionSelect = """
        SELECT n.id,n.point_id,n.point_name,n.community_id,n.community_name,n.community_title,n.type_id,n.type_name,
        n.address,n.contact,n.phone,n.inspection_date,n.inspector,n.result,n.notes,n.notice_number,
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id),
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed'),
        CASE WHEN n.result='normal' THEN 'none'
             WHEN EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status='pending') THEN 'pending'
             WHEN EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status='feedback') THEN 'feedback' ELSE 'closed' END,
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id AND x.status='closed'),
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed' AND x.deadline<$today),
        (SELECT MIN(x.deadline) FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed'),
        pt.temporary,pt.longitude,pt.latitude
        FROM inspections n JOIN points pt ON pt.id=n.point_id
        """;
    private const string InspectionWhere = """
        WHERE ($point='' OR n.point_id=$point) AND ($community='' OR n.community_id=$community)
        AND ($from='' OR n.inspection_date>=$from) AND ($to='' OR n.inspection_date<=$to)
        AND ($q='' OR n.point_name LIKE $like OR n.notice_number LIKE $like OR n.inspector LIKE $like)
        AND ($type='' OR n.type_id=$type) AND ($temporary=0 OR pt.temporary=1)
        AND ($overdue=0 OR EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed' AND x.deadline<$today))
        AND ($soon=0 OR EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed' AND x.deadline>=$today AND x.deadline<=$through))
        AND ($rectify='' OR $rectify=CASE WHEN n.result='normal' THEN 'none'
             WHEN EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status='pending') THEN 'pending'
             WHEN EXISTS(SELECT 1 FROM issues x WHERE x.inspection_id=n.id AND x.status='feedback') THEN 'feedback' ELSE 'closed' END)
        """;

    private static InspectionDto ReadInspection(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetString(11), row.GetString(12), row.GetString(13), row.GetString(14), row.GetString(15), row.GetInt32(16), row.GetInt32(17), row.GetString(18), row.GetInt32(19), row.GetInt32(20), NullableString(row, 21),
        row.GetInt32(22) != 0, row.IsDBNull(23) ? null : row.GetDouble(23), row.IsDBNull(24) ? null : row.GetDouble(24));

    private InspectionDto[] ReadInspectionList(string suffix, (string Name, object? Value)[] parameters, string? today = null)
    {
        using var command = Command(InspectionSelect + " " + suffix, null, [.. parameters, ("$today", today ?? Today)]);
        using var rows = command.ExecuteReader();
        var result = new List<InspectionDto>();
        while (rows.Read()) result.Add(ReadInspection(rows));
        return result.ToArray();
    }

    public InspectionDto GetInspection(string id)
    {
        lock (_gate)
        {
            var rows = ReadInspectionList("WHERE n.id=$id;", [("$id", id)]);
            return rows.Length == 0 ? throw new ApiException(404, "排查记录不存在。") : rows[0];
        }
    }

    public InspectionDetailDto GetInspectionDetail(string id)
    {
        lock (_gate)
        {
            var inspection = GetInspection(id);
            var issues = ReadIssueList("WHERE x.inspection_id=$id ORDER BY x.rowid;", Today, [("$id", id)]);
            return new(inspection, issues, ReadPhotos(inspectionId: id, includeIssuePhotos: true), issues.Select(issue => GetIssueDetail(issue.Id)).ToArray());
        }
    }

    private static (string Name, object? Value)[] InspectionParameters(InspectionQuery query)
    {
        var from = OptionalDate(query.From, "开始日期");
        var to = OptionalDate(query.To, "结束日期");
        CheckDateRange(from, to);
        var q = Text(query.Q, "搜索内容", 200);
        var rectify = query.Rectify is null or "all" ? "" : query.Rectify;
        if (rectify is not ("" or "none" or "pending" or "feedback" or "closed")) throw new ApiException(400, "整改状态筛选无效。");
        return [("$point", query.PointId ?? ""), ("$community", query.CommunityId ?? ""),
            ("$from", from ?? ""), ("$to", to ?? ""), ("$q", q), ("$like", "%" + q + "%"),
            ("$type", query.TypeId ?? ""), ("$temporary", query.Temporary ? 1 : 0), ("$overdue", query.Overdue ? 1 : 0),
            ("$soon", query.DueSoon ? 1 : 0), ("$rectify", rectify),
            ("$through", DateTime.Today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
    }

    private int CountMatchingIssues((string Name, object? Value)[] parameters)
    {
        using var command = Command("""
            SELECT COUNT(*) FROM inspections n JOIN points pt ON pt.id=n.point_id JOIN issues matched ON matched.inspection_id=n.id
            """ + " " + InspectionWhere + " " + """
             AND ($overdue=0 OR (matched.status<>'closed' AND matched.deadline<$today))
             AND ($soon=0 OR (matched.status<>'closed' AND matched.deadline>=$today AND matched.deadline<=$through));
            """, null, [.. parameters, ("$today", Today)]);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public PageDto<InspectionDto> ListInspections(InspectionQuery query)
    {
        lock (_gate)
        {
            var parameters = InspectionParameters(query);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            int total;
            using (var count = Command("SELECT COUNT(*) FROM inspections n JOIN points pt ON pt.id=n.point_id " + InspectionWhere, null, [.. parameters, ("$today", Today)]))
                total = Convert.ToInt32(count.ExecuteScalar());
            var items = ReadInspectionList(InspectionWhere + " ORDER BY n.inspection_date DESC,n.rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, total, page, pageSize, CountMatchingIssues(parameters));
        }
    }

    public InspectionCountsDto GetInspectionCounts(InspectionQuery query)
    {
        lock (_gate)
        {
            var parameters = InspectionParameters(query with { Rectify = null });
            var items = ReadInspectionList(InspectionWhere, parameters);
            return new(items.Length, items.Count(item => item.RectifyStatus == "none"), items.Count(item => item.RectifyStatus == "pending"),
                items.Count(item => item.RectifyStatus == "feedback"), items.Count(item => item.RectifyStatus == "closed"), CountMatchingIssues(parameters));
        }
    }

    public InspectionDto[] GetLedgerInspections(string from, string to, string? communityId = null)
    {
        lock (_gate)
        {
            var parameters = InspectionParameters(new(CommunityId: communityId, From: from, To: to));
            return ReadInspectionList(InspectionWhere + " ORDER BY n.inspection_date,n.rowid;", parameters);
        }
    }

    public InspectionDetailDto CreateInspection(InspectionCreateRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            if (string.IsNullOrWhiteSpace(request.PointId) == (request.NewPoint is null))
                throw new ApiException(400, "请选择常态点位或填写临时点位，不能同时提交。");
            var date = DateText(request.Date, "排查日期", true);
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "排查日期不能晚于今天。");
            var inspector = Text(string.IsNullOrWhiteSpace(request.Inspector) ? GetSettings().DefaultInspector : request.Inspector, "排查人", 100, true);
            if (request.Result is not ("normal" or "issues")) throw new ApiException(400, "排查结果必须是 normal 或 issues。");
            var inputIssues = request.Issues ?? [];
            if (request.Result == "normal" && inputIssues.Length > 0) throw new ApiException(400, "正常排查不能同时包含问题。");
            if (request.Result == "issues" && inputIssues.Length == 0) throw new ApiException(400, "发现问题时，请至少登记一条问题。");
            if (inputIssues.Any(issue => issue is null)) throw new ApiException(400, "问题列表中不能包含空项。");
            if (inputIssues.Length > 50) throw new ApiException(400, "单次排查最多登记 50 条问题。");
            var notes = Text(request.Notes, "排查备注", 8000);
            var noticeNumber = Text(request.NoticeNumber, "通知单编号", 100);
            var inspectionId = NewId();
            using (var transaction = _db.BeginTransaction())
            {
                var pointId = request.NewPoint is null ? Text(request.PointId, "排查点位", 100, true)
                    : SavePointCore(null, request.NewPoint with { Frequency = "irregular", Disabled = false }, transaction, createTemporary: true);
                var point = GetPointInternal(pointId, transaction);
                if (point.Disabled) throw new ApiException(409, "该点位已停用，请先启用后再排查。");
                var community = RequireCommunity(point.CommunityId, transaction);
                var validatedIssues = inputIssues.Select(issue => new IssueCreateRequest(Text(issue.Description, "问题描述", 8000, true),
                    Text(issue.Requirement, "整改要求", 8000), DateText(issue.Deadline, "整改期限"),
                    Text(string.IsNullOrWhiteSpace(issue.ResponsibleUnit) ? point.CommunityName : issue.ResponsibleUnit, "责任单位", 200, true), issue.PhotoIds)).ToArray();
                using (var insert = Command("""
                    INSERT INTO inspections(id,point_id,inspection_date,inspector,result,notes,notice_number,
                    point_name,community_id,community_name,community_title,type_id,type_name,address,contact,phone)
                    VALUES($id,$point,$date,$inspector,$result,$notes,$number,$name,$community,$communityName,$title,$type,$typeName,$address,$contact,$phone);
                    """, transaction, ("$id", inspectionId), ("$point", point.Id), ("$date", date), ("$inspector", inspector),
                    ("$result", request.Result), ("$notes", notes), ("$number", noticeNumber), ("$name", point.Name),
                    ("$community", point.CommunityId), ("$communityName", point.CommunityName), ("$title", community.Title),
                    ("$type", point.TypeId), ("$typeName", point.TypeName), ("$address", point.Address), ("$contact", point.Contact), ("$phone", point.Phone))) insert.ExecuteNonQuery();
                AttachPhotos(request.PhotoIds, "site", inspectionId, null, transaction);
                foreach (var issue in validatedIssues)
                {
                    var issueId = NewId();
                    using (var insertIssue = Command("INSERT INTO issues(id,inspection_id,description,requirement,deadline,responsible_unit,status) VALUES($id,$inspection,$description,$requirement,$deadline,$unit,'pending');",
                        transaction, ("$id", issueId), ("$inspection", inspectionId), ("$description", issue.Description), ("$requirement", issue.Requirement),
                        ("$deadline", issue.Deadline), ("$unit", issue.ResponsibleUnit))) insertIssue.ExecuteNonQuery();
                    AttachPhotos(issue.PhotoIds, "site", null, issueId, transaction);
                }
                transaction.Commit();
            }
            return GetInspectionDetail(inspectionId);
        }
    }
    public InspectionDetailDto UpdateInspection(string id, InspectionUpdateRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetInspection(id);
            var date = DateText(request.Date, "排查日期");
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "排查日期不能晚于今天。");
            using (var earliest = Command("SELECT MIN(a.action_date) FROM issue_actions a JOIN issues x ON x.id=a.issue_id WHERE x.inspection_id=$id;", null, ("$id", id)))
                if (earliest.ExecuteScalar() is string minimum && string.CompareOrdinal(date, minimum) > 0)
                    throw new ApiException(400, "排查日期不能晚于已有办理日期。");
            using var update = Command("UPDATE inspections SET inspection_date=$date,inspector=$inspector,notice_number=$number,notes=$notes WHERE id=$id;", null,
                ("$id", id), ("$date", date), ("$inspector", Text(request.Inspector, "排查人", 100, true)),
                ("$number", Text(request.NoticeNumber, "通知单编号", 100)), ("$notes", Text(request.Notes, "备注", 8000)));
            update.ExecuteNonQuery();
            return GetInspectionDetail(id);
        }
    }

    public void DeleteInspection(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            var inspection = GetInspection(id);
            var removeTemporary = inspection.PointTemporary;
            if (removeTemporary)
            {
                using var others = Command("SELECT COUNT(*) FROM inspections WHERE point_id=$point AND id<>$id;", null,
                    ("$point", inspection.PointId), ("$id", id));
                removeTemporary = Convert.ToInt32(others.ExecuteScalar()) == 0;
            }
            var photos = ReadPhotos(inspectionId: id, includeIssuePhotos: true).Select(photo => photo.Id).ToList();
            if (removeTemporary) photos.AddRange(ReadPhotos(pointId: inspection.PointId).Select(photo => photo.Id));
            ExecuteWithPhotoDeletion(photos.ToArray(), transaction =>
            {
                using var delete = Command("""
                    DELETE FROM issue_actions WHERE issue_id IN (SELECT id FROM issues WHERE inspection_id=$id);
                    DELETE FROM issues WHERE inspection_id=$id;
                    DELETE FROM inspections WHERE id=$id;
                    """, transaction, ("$id", id));
                delete.ExecuteNonQuery();
                if (removeTemporary)
                {
                    using var point = Command("DELETE FROM points WHERE id=$id;", transaction, ("$id", inspection.PointId));
                    point.ExecuteNonQuery();
                }
            });
        }
    }

}
