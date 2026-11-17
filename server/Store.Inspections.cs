using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string InspectionSelect = """
        SELECT n.id,n.point_id,n.point_name,n.community_id,n.community_name,n.community_title,n.type_id,n.type_name,
        n.address,n.contact,n.phone,n.inspection_date,n.inspector,n.result,n.notes,n.notice_number,
        (SELECT COUNT(*) FROM active_issues x WHERE x.inspection_id=n.id),
        (SELECT COUNT(*) FROM active_issues x WHERE x.inspection_id=n.id AND x.status<>'closed'),
        CASE WHEN n.result='normal' THEN 'none' WHEN n.result='issues' AND NOT EXISTS(SELECT 1 FROM active_issues ax WHERE ax.inspection_id=n.id) THEN 'voided' WHEN n.result='incomplete' AND NOT EXISTS(SELECT 1 FROM active_issues ix WHERE ix.inspection_id=n.id) THEN 'incomplete'
             WHEN EXISTS(SELECT 1 FROM active_issues x WHERE x.inspection_id=n.id AND x.status='pending') THEN 'pending'
             WHEN EXISTS(SELECT 1 FROM active_issues x WHERE x.inspection_id=n.id AND x.status='feedback') THEN 'feedback' ELSE 'closed' END,
        (SELECT COUNT(*) FROM active_issues x WHERE x.inspection_id=n.id AND x.status='closed'),
        (SELECT COUNT(*) FROM active_issues x WHERE x.inspection_id=n.id AND x.status<>'closed' AND x.deadline<$today),
        (SELECT MIN(x.deadline) FROM active_issues x WHERE x.inspection_id=n.id AND x.status<>'closed'),
        pt.temporary,pt.longitude,pt.latitude,
        n.inspection_type_id,n.inspection_type_name,n.use_id,n.use_name,n.created_at,n.incomplete_reason,n.checklist,
        (SELECT COUNT(*) FROM community_history h WHERE h.record_id=n.id AND h.kind='inspection')
        + (SELECT COUNT(*) FROM community_history h JOIN issues x ON x.id=h.record_id WHERE x.inspection_id=n.id AND h.kind='issue')
        + (SELECT COUNT(*) FROM issue_actions a JOIN issues x ON x.id=a.issue_id WHERE x.inspection_id=n.id)
         ,n.voided,n.void_reason
        FROM active_inspections n JOIN points pt ON pt.id=n.point_id
        """;
    private const string InspectionWhere = """
        WHERE ($point='' OR n.point_id=$point) AND ($community='' OR n.community_id=$community)
        AND ($from='' OR n.inspection_date>=$from) AND ($to='' OR n.inspection_date<=$to)
        AND ($q='' OR n.point_name LIKE $like OR n.notice_number LIKE $like OR n.inspector LIKE $like)
        AND ($type='' OR n.type_id=$type) AND ($temporary=0 OR pt.temporary=1)
        AND (($overdue=0 AND $soon=0 AND $awaiting=0 AND $open=0) OR EXISTS(
            SELECT 1 FROM active_issues x WHERE x.inspection_id=n.id
            AND ($overdue=0 OR (x.status<>'closed' AND x.deadline<$today))
            AND ($soon=0 OR (x.status<>'closed' AND x.deadline>=$today AND x.deadline<=$through))
            AND ($awaiting=0 OR x.status='feedback') AND ($open=0 OR x.status<>'closed')))
        AND ($rectify='' OR $rectify=CASE WHEN n.result='normal' THEN 'none' WHEN n.result='issues' AND NOT EXISTS(SELECT 1 FROM active_issues ax WHERE ax.inspection_id=n.id) THEN 'voided' WHEN n.result='incomplete' AND NOT EXISTS(SELECT 1 FROM active_issues ix WHERE ix.inspection_id=n.id) THEN 'incomplete'
             WHEN EXISTS(SELECT 1 FROM active_issues x WHERE x.inspection_id=n.id AND x.status='pending') THEN 'pending'
             WHEN EXISTS(SELECT 1 FROM active_issues x WHERE x.inspection_id=n.id AND x.status='feedback') THEN 'feedback' ELSE 'closed' END)
        """;

    private static InspectionDto ReadInspection(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetString(11), row.GetString(12), row.GetString(13), row.GetString(14), row.GetString(15), row.GetInt32(16), row.GetInt32(17), row.GetString(18), row.GetInt32(19), row.GetInt32(20), NullableString(row, 21),
        row.GetInt32(22) != 0, row.IsDBNull(23) ? null : row.GetDouble(23), row.IsDBNull(24) ? null : row.GetDouble(24),
        row.GetString(25),row.GetString(26),row.GetString(27),row.GetString(28),row.GetString(29),row.GetString(30),JsonSerializer.Deserialize(row.GetString(31), AppJsonContext.Default.CheckItemArray), row.GetInt32(32), row.GetInt32(33) != 0, row.GetString(34));

    private InspectionDto[] ReadInspectionList(string suffix, (string Name, object? Value)[] parameters, string? today = null, bool includeVoided = false)
    {
        var select = includeVoided ? InspectionSelect.Replace("FROM active_inspections n JOIN", "FROM inspections n JOIN", StringComparison.Ordinal) : InspectionSelect;
        using var command = Command(select + " " + suffix, null, [.. parameters, ("$today", today ?? Today)]);
        using var rows = command.ExecuteReader();
        var result = new List<InspectionDto>();
        while (rows.Read()) result.Add(ReadInspection(rows));
        return result.ToArray();
    }

    public InspectionDto GetInspection(string id)
    {
        lock (_gate)
        {
            var rows = ReadInspectionList("WHERE n.id=$id;", [("$id", id)], includeVoided: true);
            return rows.Length == 0 ? throw new ApiException(404, "排查记录不存在。") : rows[0];
        }
    }

    public InspectionDetailDto GetInspectionDetail(string id)
    {
        lock (_gate)
        {
            var inspection = GetInspection(id);
            var issues = ReadIssueList("WHERE x.inspection_id=$id ORDER BY x.rowid;", Today, [("$id", id)], true);
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
        if (rectify is not ("" or "none" or "incomplete" or "voided" or "pending" or "feedback" or "closed")) throw new ApiException(400, "整改状态筛选无效。");
        return [("$point", query.PointId ?? ""), ("$community", query.CommunityId ?? ""),
            ("$from", from ?? ""), ("$to", to ?? ""), ("$q", q), ("$like", "%" + q + "%"),
            ("$type", query.TypeId ?? ""), ("$temporary", query.Temporary ? 1 : 0), ("$overdue", query.Overdue ? 1 : 0),
            ("$soon", query.DueSoon ? 1 : 0), ("$awaiting", query.AwaitingReview ? 1 : 0), ("$open", query.OpenOnly ? 1 : 0), ("$rectify", rectify),
            ("$through", DateTime.Today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
    }

    private int CountMatchingIssues((string Name, object? Value)[] parameters)
    {
        using var command = Command("""
            SELECT COUNT(*) FROM active_inspections n JOIN points pt ON pt.id=n.point_id JOIN active_issues matched ON matched.inspection_id=n.id
            """ + " " + InspectionWhere + " " + """
             AND ($overdue=0 OR (matched.status<>'closed' AND matched.deadline<$today))
             AND ($soon=0 OR (matched.status<>'closed' AND matched.deadline>=$today AND matched.deadline<=$through))
             AND ($awaiting=0 OR matched.status='feedback') AND ($open=0 OR matched.status<>'closed');
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
            using (var count = Command("SELECT COUNT(*) FROM active_inspections n JOIN points pt ON pt.id=n.point_id " + InspectionWhere, null, [.. parameters, ("$today", Today)]))
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

    public InspectionDto[] GetLedgerInspections(string from, string to, string? communityId = null, string[]? pointIds = null)
    {
        lock (_gate)
        {
            var parameters = InspectionParameters(new(CommunityId: communityId, From: from, To: to));
            return ReadInspectionList(InspectionWhere + LedgerPointSql(pointIds?.Length ?? 0, "n.point_id") + " ORDER BY n.inspection_date,n.rowid;", [.. parameters, .. LedgerPointParameters(pointIds)]);
        }
    }

    public InspectionDetailDto CreateInspection(InspectionCreateRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var submissionKey = Text(request.SubmissionKey, "提交编号", 100);
            if (submissionKey.Length > 0)
            {
                using var submitted = Command("SELECT id FROM inspections WHERE submission_key=$key;", null, ("$key", submissionKey));
                if (submitted.ExecuteScalar() is string existingId) return GetInspectionDetail(existingId);
            }
            if (string.IsNullOrWhiteSpace(request.PointId) == (request.NewPoint is null))
                throw new ApiException(400, "请选择常态点位或填写临时点位，不能同时提交。");
            var date = DateText(request.Date, "排查日期", true);
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "排查日期不能晚于今天。");
            var inspector = Text(string.IsNullOrWhiteSpace(request.Inspector) ? GetSettings().DefaultInspector : request.Inspector, "排查人", 100, true);
            if (request.Result is not ("normal" or "issues" or "incomplete")) throw new ApiException(400, "请选择检查结果。");
            var incompleteReason = Text(request.IncompleteReason, "未完成原因", 2000, request.Result == "incomplete");
            var inspectionType = string.IsNullOrWhiteSpace(request.InspectionTypeId) ? null : ListCommunityRecords("inspection-type").FirstOrDefault(x => x.Id == request.InspectionTypeId) ?? throw new ApiException(400, "检查类型已停用或不存在。");
            var checklist = request.Checklist ?? [];
            if (checklist.Length > 100 || checklist.Any(x => x is null || x.Result is not ("normal" or "issues" or "na" or "unchecked") || string.IsNullOrWhiteSpace(x.Name))) throw new ApiException(400, "检查项目填写不正确。");
            if (request.Result == "normal" && checklist.Any(x => x.Result == "issues")) throw new ApiException(400, "检查项目中有问题，请将结果改为发现问题并登记问题详情。");
            if (request.Result != "incomplete" && checklist.Any(x => x.Result == "unchecked")) throw new ApiException(400, "还有未检查的项目，请补充结果，或选择未完成并填写原因。");
            if (request.Shape is not null) ValidateShape(request.Shape);
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
                ConsumeSharedDraft(request, inspectionId, transaction);
                var point = ResolveRecordPoint(request.PointId, request.NewPoint, transaction);
                var community = RequireCommunity(point.CommunityId, transaction);
                string useName = "", useId = "";
                if (!string.IsNullOrWhiteSpace(request.UseId))
                {
                    using var useQuery = Command("SELECT id,name,body,archived FROM community_records WHERE kind='use' AND id=$id AND parent_id=$parent;", transaction, ("$id", request.UseId), ("$parent", point.Id));
                    using var useRow = useQuery.ExecuteReader();
                    if (!useRow.Read()) throw new ApiException(400, "经营使用记录不属于该场所。");
                    using var useBody = JsonDocument.Parse(useRow.GetString(2));
                    var start = JsonText(useBody.RootElement, "startDate");
                    var end = JsonText(useBody.RootElement, "endDate");
                    if (useRow.GetInt32(3) != 0) throw new ApiException(400, "使用记录已停用，请选择有效记录。");
                    if ((start.Length > 0 && string.CompareOrdinal(date, start) < 0) || (end.Length > 0 && string.CompareOrdinal(date, end) > 0)) throw new ApiException(400, "检查日期不在该经营、使用记录的起止日期内。");
                    useId = useRow.GetString(0); useName = useRow.GetString(1);
                }
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
                using (var meta = Command("UPDATE inspections SET inspection_type_id=$typeId,inspection_type_name=$typeName,use_id=$useId,use_name=$useName,created_at=$at,incomplete_reason=$reason,checklist=$checklist,submission_key=$key WHERE id=$id;", transaction,
                    ("$id", inspectionId), ("$typeId", inspectionType?.Id ?? ""), ("$typeName", inspectionType?.Name ?? ""), ("$useId", useId), ("$useName", useName),
                    ("$at", DateTimeOffset.Now.ToString("O")), ("$reason", incompleteReason), ("$checklist", JsonSerializer.Serialize(checklist, AppJsonContext.Default.CheckItemArray)), ("$key", submissionKey.Length == 0 ? null : submissionKey))) meta.ExecuteNonQuery();
                if (request.Shape is not null && point.Temporary)
                {
                    using var shapeCommand = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES('point',$point,$shape,1) ON CONFLICT(kind,record_id) DO NOTHING;", transaction,
                        ("$point", point.Id), ("$shape", JsonSerializer.Serialize(request.Shape, CommunityJsonContext.Default.GeoShape)));
                    shapeCommand.ExecuteNonQuery();
                }
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
            var before = GetInspection(id);
            RequireActive(before.Voided);
            CheckRevision(request.Revision, before.Revision);
            var date = DateText(request.Date, "排查日期");
            if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "排查日期不能晚于今天。");
            using (var earliest = Command("SELECT MIN(a.action_date) FROM issue_actions a JOIN issues x ON x.id=a.issue_id WHERE x.inspection_id=$id;", null, ("$id", id)))
                if (earliest.ExecuteScalar() is string minimum && string.CompareOrdinal(date, minimum) > 0)
                    throw new ApiException(400, "排查日期不能晚于已有办理日期。");
            using var transaction = _db.BeginTransaction();
            using var update = Command("UPDATE inspections SET inspection_date=$date,inspector=$inspector,notice_number=$number,notes=$notes WHERE id=$id;", transaction,
                ("$id", id), ("$date", date), ("$inspector", Text(request.Inspector, "排查人", 100, true)),
                ("$number", Text(request.NoticeNumber, "通知单编号", 100)), ("$notes", Text(request.Notes, "备注", 8000)));
            update.ExecuteNonQuery();
            AddCommunityHistory("inspection", id, JsonSerializer.Serialize(before, AppJsonContext.Default.InspectionDto), JsonSerializer.Serialize(request, AppJsonContext.Default.InspectionUpdateRequest), "修改检查", transaction);
            transaction.Commit();
            return GetInspectionDetail(id);
        }
    }

    public void DeleteInspection(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetInspection(id);
            var photoIds = new List<string>();
            using (var query = Command("""
                SELECT id FROM photos WHERE inspection_id=$id
                OR issue_id IN (SELECT id FROM issues WHERE inspection_id=$id);
                """, null, ("$id", id)))
            using (var rows = query.ExecuteReader())
                while (rows.Read()) photoIds.Add(rows.GetString(0));
            ExecuteWithPhotoDeletion([.. photoIds], transaction =>
            {
                using (var history = Command("""
                    DELETE FROM community_history WHERE (kind='inspection' AND record_id=$id)
                    OR (kind='issue' AND record_id IN (SELECT id FROM issues WHERE inspection_id=$id));
                    """, transaction, ("$id", id))) history.ExecuteNonQuery();
                using (var actions = Command("DELETE FROM issue_actions WHERE issue_id IN (SELECT id FROM issues WHERE inspection_id=$id);", transaction, ("$id", id))) actions.ExecuteNonQuery();
                using (var issues = Command("DELETE FROM issues WHERE inspection_id=$id;", transaction, ("$id", id))) issues.ExecuteNonQuery();
                using (var record = Command("DELETE FROM inspections WHERE id=$id;", transaction, ("$id", id))) record.ExecuteNonQuery();
            });
        }
    }
}
