using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string IssueSelect = """
        SELECT x.id,x.inspection_id,n.point_id,n.point_name,n.community_id,n.community_name,n.inspection_date,
        x.description,x.requirement,x.deadline,x.responsible_unit,x.status,x.feedback_date,x.feedback_notes,
        x.review_date,x.review_conclusion,x.closed_date,CASE WHEN x.status<>'closed' AND x.deadline<$today THEN 1 ELSE 0 END,x.issue_type_id,x.issue_type_name
        FROM issues x JOIN inspections n ON n.id=x.inspection_id
        """;
    private const string IssueWhere = """
        WHERE ($status='' OR x.status=$status) AND ($community='' OR n.community_id=$community)
        AND ($point='' OR n.point_id=$point) AND ($inspection='' OR x.inspection_id=$inspection) AND ($type='' OR x.issue_type_id=$type)
        AND ($overdue=0 OR (x.status<>'closed' AND x.deadline<$today))
        AND ($soon=0 OR (x.status<>'closed' AND x.deadline>=$today AND x.deadline<=$through))
        AND ($from='' OR n.inspection_date>=$from) AND ($to='' OR n.inspection_date<=$to)
        AND ($q='' OR n.point_name LIKE $like OR x.description LIKE $like OR x.responsible_unit LIKE $like)
        """;

    private static IssueDto ReadIssue(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetString(9), row.GetString(10), row.GetString(11),
        NullableString(row, 12), row.GetString(13), NullableString(row, 14), row.GetString(15), NullableString(row, 16), row.GetInt32(17) != 0, row.GetString(18), row.GetString(19));

    private IssueDto[] ReadIssueList(string suffix, string today, (string Name, object? Value)[] parameters)
    {
        using var command = Command(IssueSelect + " " + suffix, null, [.. parameters, ("$today", today)]);
        using var rows = command.ExecuteReader();
        var result = new List<IssueDto>();
        while (rows.Read()) result.Add(ReadIssue(rows));
        return result.ToArray();
    }

    public IssueDto GetIssue(string id)
    {
        lock (_gate)
        {
            var rows = ReadIssueList("WHERE x.id=$id;", Today, [("$id", id)]);
            return rows.Length == 0 ? throw new ApiException(404, "问题不存在。") : rows[0];
        }
    }

    public IssueDetailDto GetIssueDetail(string id)
    {
        lock (_gate)
        {
            var issue = GetIssue(id);
            var inspection = GetInspection(issue.InspectionId);
            var actionRows = new List<(string Id, string Kind, string Date, string Notes)>();
            using (var command = Command("SELECT id,kind,action_date,notes FROM issue_actions WHERE issue_id=$id ORDER BY rowid;", null, ("$id", id)))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) actionRows.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3)));
            var actions = actionRows.Select(action => new IssueActionDto(action.Id, action.Kind, action.Date, action.Notes, ReadPhotos(actionId: action.Id))).ToArray();
            return new(issue, inspection, ReadPhotos(issueId: id), actions);
        }
    }

    public PageDto<IssueDto> ListIssues(IssueQuery query)
    {
        lock (_gate)
        {
            var status = query.Status == "all" ? "" : query.Status ?? "";
            if (status is not ("" or "pending" or "feedback" or "closed")) throw new ApiException(400, "问题状态无效。");
            var from = OptionalDate(query.From, "开始日期");
            var to = OptionalDate(query.To, "结束日期");
            CheckDateRange(from, to);
            var q = Text(query.Q, "搜索内容", 200);
            var (page, pageSize) = Paging(query.Page, query.PageSize);
            var today = Today;
            var through = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            (string Name, object? Value)[] parameters = [("$status", status), ("$community", query.CommunityId ?? ""), ("$point", query.PointId ?? ""),
                ("$inspection", query.InspectionId ?? ""), ("$type", query.TypeId ?? ""), ("$overdue", query.Overdue ? 1 : 0), ("$soon", query.DueSoon ? 1 : 0), ("$through", through),
                ("$from", from ?? ""), ("$to", to ?? ""), ("$q", q), ("$like", "%" + q + "%")];
            int total;
            using (var count = Command("SELECT COUNT(*) FROM issues x JOIN inspections n ON n.id=x.inspection_id " + IssueWhere, null, [.. parameters, ("$today", today)])) total = Convert.ToInt32(count.ExecuteScalar());
            var items = ReadIssueList(IssueWhere + " ORDER BY CASE x.status WHEN 'pending' THEN 0 WHEN 'feedback' THEN 1 ELSE 2 END,x.deadline,x.rowid DESC LIMIT $limit OFFSET $offset;", today,
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, total, page, pageSize);
        }
    }

    public IssueDto[] GetLedgerIssues(string from, string to, string? communityId = null, string[]? pointIds = null)
    {
        lock (_gate)
        {
            from = DateText(from, "开始日期");
            to = DateText(to, "结束日期");
            CheckDateRange(from, to);
            return ReadIssueList("WHERE n.inspection_date>=$from AND n.inspection_date<=$to AND ($community='' OR n.community_id=$community)" + LedgerPointSql(pointIds?.Length ?? 0, "n.point_id") + " ORDER BY n.inspection_date,n.rowid,x.rowid;",
                Today, [("$from", from), ("$to", to), ("$community", communityId ?? ""), .. LedgerPointParameters(pointIds)]);
        }
    }

    public IssueDetailDto UpdateIssue(string id, IssueUpdateRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var issue = GetIssue(id);
            if (issue.Status != "pending") throw new ApiException(409, "只有待整改问题可以修改；如需继续整改，请先退回待整改。");
            var kind = RequireIssueType(request.IssueTypeId);
            using var command = Command("UPDATE issues SET description=$description,requirement=$requirement,deadline=$deadline,responsible_unit=$unit,issue_type_id=$type,issue_type_name=$typeName WHERE id=$id;", null,
                ("$id", id), ("$type", kind.Id), ("$typeName", kind.Name), ("$description", Text(request.Description, "问题描述", 8000, true)), ("$requirement", Text(request.Requirement, "整改要求", 8000)),
                ("$deadline", DateText(request.Deadline, "整改期限")), ("$unit", Text(request.ResponsibleUnit, "责任单位", 200, true)));
            command.ExecuteNonQuery();
            return GetIssueDetail(id);
        }
    }

    private static string ActionDate(string? input, string minimum, string label)
    {
        var date = DateText(input, label, true);
        if (string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, $"{label}不能晚于今天。");
        if (string.CompareOrdinal(date, minimum) < 0) throw new ApiException(400, $"{label}不能早于 {minimum}。");
        return date;
    }

    public IssueDetailDto RecordFeedback(string id, FeedbackRequest request)
    {
        lock (_gate)
        {
            var issue = GetIssue(id);
            RecordInspectionFeedback(issue.InspectionId, new(request.Date, [new(id, request.Notes, request.PhotoIds)]));
            return GetIssueDetail(id);
        }
    }

    public IssueDetailDto ReviewAndClose(string id, ReviewRequest request)
    {
        lock (_gate)
        {
            var issue = GetIssue(id);
            ReviewInspection(issue.InspectionId, new(request.Date, [new(id, request.Conclusion, request.PhotoIds)]));
            return GetIssueDetail(id);
        }
    }

    public IssueDetailDto ReturnIssue(string id, ReturnIssueRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            var issue = GetIssue(id);
            if (issue.Status == "pending") throw new ApiException(409, "该问题已经是待整改状态。");
            var reason = Text(request.Reason, "退回原因", 8000);
            if (reason.Length == 0) reason = "退回继续整改。";
            using (var transaction = _db.BeginTransaction())
            {
                using (var action = Command("INSERT INTO issue_actions(id,issue_id,kind,action_date,notes) VALUES($id,$issue,'return',$date,$notes);", transaction,
                    ("$id", NewId()), ("$issue", id), ("$date", Today), ("$notes", reason))) action.ExecuteNonQuery();
                // Historical actions and their photos remain; the current round starts clean.
                using (var update = Command("UPDATE issues SET status='pending',feedback_date=NULL,feedback_notes='',review_date=NULL,review_conclusion='',closed_date=NULL WHERE id=$id;", transaction, ("$id", id))) update.ExecuteNonQuery();
                transaction.Commit();
            }
            return GetIssueDetail(id);
        }
    }
}
