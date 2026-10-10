using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string InspectionSelect = """
        SELECT n.id,n.point_id,n.point_name,n.community_id,n.community_name,n.community_title,n.type_id,n.type_name,
        n.address,n.contact,n.phone,n.inspection_date,n.inspector,n.result,n.notes,n.notice_number,
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id),
        (SELECT COUNT(*) FROM issues x WHERE x.inspection_id=n.id AND x.status<>'closed')
        FROM inspections n
        """;
    private const string InspectionWhere = """
        WHERE ($point='' OR n.point_id=$point) AND ($community='' OR n.community_id=$community)
        AND ($from='' OR n.inspection_date>=$from) AND ($to='' OR n.inspection_date<=$to)
        AND ($q='' OR n.point_name LIKE $like OR n.notice_number LIKE $like OR n.inspector LIKE $like)
        """;

    private static InspectionDto ReadInspection(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10),
        row.GetString(11), row.GetString(12), row.GetString(13), row.GetString(14), row.GetString(15), row.GetInt32(16), row.GetInt32(17));

    private InspectionDto[] ReadInspectionList(string suffix, (string Name, object? Value)[] parameters)
    {
        using var command = Command(InspectionSelect + " " + suffix, null, parameters);
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
            return new(inspection, issues, ReadPhotos(inspectionId: id, includeIssuePhotos: true));
        }
    }

    public PageDto<InspectionDto> ListInspections(InspectionQuery query)
    {
        lock (_gate)
        {
            var from = OptionalDate(query.From, "开始日期");
            var to = OptionalDate(query.To, "结束日期");
            CheckDateRange(from, to);
            var q = Text(query.Q, "搜索内容", 200);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            (string Name, object? Value)[] parameters = [("$point", query.PointId ?? ""), ("$community", query.CommunityId ?? ""),
                ("$from", from ?? ""), ("$to", to ?? ""), ("$q", q), ("$like", "%" + q + "%")];
            int total;
            using (var count = Command("SELECT COUNT(*) FROM inspections n " + InspectionWhere, null, parameters)) total = Convert.ToInt32(count.ExecuteScalar());
            var items = ReadInspectionList(InspectionWhere + " ORDER BY n.inspection_date DESC,n.rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, total, page, pageSize);
        }
    }

    public InspectionDetailDto CreateInspection(InspectionCreateRequest request)
    {
        lock (_gate)
        {
            var point = GetPoint(Text(request.PointId, "排查点位", 100, true));
            if (point.Disabled) throw new ApiException(409, "该点位已停用，请先在点位管理中启用后再排查。");
            var community = RequireCommunity(point.CommunityId);
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
            var validatedIssues = inputIssues.Select(issue => new IssueCreateRequest(Text(issue.Description, "问题描述", 8000, true),
                Text(issue.Requirement, "整改要求", 8000), DateText(issue.Deadline, "整改期限"),
                Text(string.IsNullOrWhiteSpace(issue.ResponsibleUnit) ? point.CommunityName : issue.ResponsibleUnit, "责任单位", 200, true), issue.PhotoIds)).ToArray();
            var inspectionId = NewId();
            using (var transaction = _db.BeginTransaction())
            {
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
}
