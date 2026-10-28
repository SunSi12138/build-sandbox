using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private const string MatterSelect = """
        SELECT m.id,m.title,m.source,m.received_date,m.content,m.point_id,COALESCE(p.name,''),m.location,m.deadline,
            m.handler,m.reference_number,m.contact,m.phone,m.status,m.closed_date,m.outcome,m.conclusion,
            (SELECT COUNT(*) FROM inspection_matters r WHERE r.matter_id=m.id),
            (SELECT COUNT(*) FROM issue_matters r WHERE r.matter_id=m.id),
            (SELECT COUNT(*) FROM issue_matters r JOIN issues x ON x.id=r.issue_id WHERE r.matter_id=m.id AND x.status<>'closed'),
            (SELECT COUNT(*) FROM activity_matters r WHERE r.matter_id=m.id),
            CASE WHEN m.status<>'closed' AND m.deadline<$today THEN 1 ELSE 0 END
        FROM matters m LEFT JOIN points p ON p.id=m.point_id
        """;
    private const string MatterWhere = """
        WHERE ($q='' OR m.title LIKE $like OR m.content LIKE $like OR m.reference_number LIKE $like OR m.handler LIKE $like)
          AND ($status='' OR m.status=$status) AND ($source='' OR m.source=$source) AND ($point='' OR m.point_id=$point)
          AND ($from='' OR m.received_date>=$from) AND ($to='' OR m.received_date<=$to)
          AND ($overdue=0 OR (m.status<>'closed' AND m.deadline<$today))
        """;
    private MatterDto ReadMatter(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4),
        NullableString(row, 5), row.GetString(6), row.GetString(7), NullableString(row, 8), row.GetString(9), row.GetString(10), row.GetString(11),
        row.GetString(12), row.GetString(13), NullableString(row, 14), row.GetString(15), row.GetString(16), row.GetInt32(17), row.GetInt32(18), row.GetInt32(19), row.GetInt32(20), row.GetInt32(21) != 0)
        { Tags = ReadTags("matter", row.GetString(0)) };
    private MatterDto[] ReadMatters(string suffix, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(MatterSelect + " " + suffix, null, [.. parameters, ("$today", Today)]);
        using var rows = command.ExecuteReader(); var list = new List<MatterDto>();
        while (rows.Read()) list.Add(ReadMatter(rows));
        return list.ToArray();
    }
    public MatterDto GetMatter(string id)
    {
        lock (_gate) return ReadMatters("WHERE m.id=$id;", ("$id", id)).FirstOrDefault() ?? throw new ApiException(404, "事项不存在。");
    }
    public PageDto<MatterDto> ListMatters(MatterQuery query)
    {
        lock (_gate)
        {
            var status = query.Status is null or "all" ? "" : query.Status;
            if (status is not ("" or "pending" or "processing" or "closed")) throw new ApiException(400, "事项状态无效。");
            var source = query.Source is null or "all" ? "" : query.Source;
            if (source.Length > 0) ValidateMatterSource(source);
            var from = OptionalDate(query.From, "开始日期"); var to = OptionalDate(query.To, "结束日期"); CheckDateRange(from, to);
            var q = Text(query.Q, "搜索内容", 200); var (page, pageSize) = Paging(query.Page, query.PageSize);
            var where = MatterWhere + TagFilterSql("matter", "m.id", query.TagIds);
            (string Name, object? Value)[] parameters = [("$q", q), ("$like", "%" + q + "%"), ("$status", status), ("$source", source),
                ("$point", query.PointId ?? ""), ("$from", from ?? ""), ("$to", to ?? ""), ("$overdue", query.Overdue ? 1 : 0), .. TagParameters(query.TagIds)];
            using var count = Command("SELECT COUNT(*) FROM matters m " + where, null, [.. parameters, ("$today", Today)]);
            var total = Convert.ToInt32(count.ExecuteScalar());
            var items = ReadMatters(where + " ORDER BY CASE m.status WHEN 'pending' THEN 0 WHEN 'processing' THEN 1 ELSE 2 END,COALESCE(m.deadline,'9999-12-31'),m.received_date DESC,m.rowid DESC LIMIT $limit OFFSET $offset;",
                [.. parameters, ("$limit", pageSize), ("$offset", (page - 1) * pageSize)]);
            return new(items, total, page, pageSize);
        }
    }
    public MatterDetailDto GetMatterDetail(string id)
    {
        lock (_gate)
        {
            var matter = GetMatter(id); var actions = new List<MatterActionDto>();
            using (var command = Command("SELECT id,kind,action_date,handler,notes,outcome FROM matter_actions WHERE matter_id=$id ORDER BY rowid;", null, ("$id", id)))
            using (var rows = command.ExecuteReader()) while (rows.Read()) actions.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), rows.GetString(5)));
            var inspections = ReadInspectionList("WHERE EXISTS(SELECT 1 FROM inspection_matters r WHERE r.inspection_id=n.id AND r.matter_id=$id) ORDER BY n.inspection_date DESC;", [("$id", id)]);
            var issues = ReadIssueList("WHERE EXISTS(SELECT 1 FROM issue_matters r WHERE r.issue_id=x.id AND r.matter_id=$id) ORDER BY x.deadline;", Today, [("$id", id)]);
            var activities = ReadActivities("WHERE EXISTS(SELECT 1 FROM activity_matters r WHERE r.activity_id=a.id AND r.matter_id=$id) ORDER BY a.activity_date DESC;", [("$id", id)]);
            return new(matter, actions.ToArray(), inspections, issues, activities);
        }
    }
    private static void ValidateMatterSource(string source)
    {
        if (source is not ("public" or "hotline" or "assigned" or "discovered" or "other")) throw new ApiException(400, "请选择有效的事项来源。");
    }
    public MatterDto SaveMatter(string? id, MatterWriteRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var existing = id is null ? null : GetMatter(id);
            if (existing?.Status == "closed") throw new ApiException(409, "事项已办结，请先重新办理后修改。");
            ValidateMatterSource(request.Source);
            var received = DateText(request.ReceivedDate, "接收日期");
            if (string.CompareOrdinal(received, Today) > 0) throw new ApiException(400, "接收日期不能晚于今天。");
            var deadline = OptionalDate(request.Deadline, "办理期限"); CheckDateRange(received, deadline);
            var pointId = string.IsNullOrWhiteSpace(request.PointId) ? null : GetPoint(request.PointId).Id;
            var actualId = id ?? NewId();
            using var tx = _db.BeginTransaction();
            if (existing is not null)
            {
                using var earliest = Command("SELECT MIN(action_date) FROM matter_actions WHERE matter_id=$id;", tx, ("$id", id));
                if (earliest.ExecuteScalar() is string actionDate && string.CompareOrdinal(received, actionDate) > 0) throw new ApiException(400, "接收日期不能晚于已有办理记录。");
            }
            using (var command = Command(id is null ? """
                INSERT INTO matters(id,title,source,received_date,content,point_id,location,deadline,handler,reference_number,contact,phone)
                VALUES($id,$title,$source,$received,$content,$point,$location,$deadline,$handler,$number,$contact,$phone);
                """ : """
                UPDATE matters SET title=$title,source=$source,received_date=$received,content=$content,point_id=$point,location=$location,
                deadline=$deadline,handler=$handler,reference_number=$number,contact=$contact,phone=$phone WHERE id=$id;
                """, tx, ("$id", actualId), ("$title", Text(request.Title, "事项标题", 200, true)), ("$source", request.Source),
                ("$received", received), ("$content", Text(request.Content, "事项内容", 12000, true)), ("$point", pointId),
                ("$location", Text(request.Location, "涉及地点", 1000)), ("$deadline", deadline), ("$handler", Text(request.Handler, "经办人", 200)),
                ("$number", Text(request.ReferenceNumber, "交办编号", 200)), ("$contact", Text(request.Contact, "联系人", 100)), ("$phone", Text(request.Phone, "联系方式", 100))))
                RequireChanged(command.ExecuteNonQuery(), "事项");
            ReplaceTags("matter", actualId, request.TagIds, tx); tx.Commit(); return GetMatter(actualId);
        }
    }
    public MatterDetailDto AddMatterAction(string id, MatterActionRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var matter = GetMatter(id);
            if (request.Kind is not ("note" or "reply" or "transfer" or "close" or "reopen")) throw new ApiException(400, "办理记录类型无效。");
            if (request.Kind == "reopen" ? matter.Status != "closed" : matter.Status == "closed") throw new ApiException(409, request.Kind == "reopen" ? "事项尚未办结。" : "事项已办结，请先重新办理。");
            var date = DateText(request.Date, "办理日期");
            if (string.CompareOrdinal(date, matter.ReceivedDate) < 0 || string.CompareOrdinal(date, Today) > 0) throw new ApiException(400, "办理日期应在接收日期与今天之间。");
            var notes = Text(request.Notes, "办理内容", 12000, true);
            var handler = Text(request.Handler ?? matter.Handler, "经办人", 200, true);
            var outcome = request.Kind == "close" ? request.Outcome ?? "" : "";
            if (request.Kind == "close")
            {
                if (outcome is not ("resolved" or "no_issue" or "transferred" or "withdrawn")) throw new ApiException(400, "请选择办结结论。");
                if (outcome == "resolved" && matter.OpenIssueCount > 0) throw new ApiException(409, "仍有关联问题未闭环，不能按已解决办结。");
            }
            using var tx = _db.BeginTransaction();
            using (var last = Command("SELECT MAX(action_date) FROM matter_actions WHERE matter_id=$id;", tx, ("$id", id)))
                if (last.ExecuteScalar() is string previous && string.CompareOrdinal(date, previous) < 0) throw new ApiException(400, "办理日期不能早于上一条办理记录。");
            using (var action = Command("INSERT INTO matter_actions VALUES($action,$id,$kind,$date,$handler,$notes,$outcome);", tx,
                ("$action", NewId()), ("$id", id), ("$kind", request.Kind), ("$date", date), ("$handler", handler), ("$notes", notes), ("$outcome", outcome))) action.ExecuteNonQuery();
            using (var update = Command("UPDATE matters SET status=$status,closed_date=$closed,outcome=$outcome,conclusion=$conclusion WHERE id=$id;", tx,
                ("$id", id), ("$status", request.Kind == "close" ? "closed" : "processing"), ("$closed", request.Kind == "close" ? date : null),
                ("$outcome", outcome), ("$conclusion", request.Kind == "close" ? notes : ""))) update.ExecuteNonQuery();
            tx.Commit(); return GetMatterDetail(id);
        }
    }
    public void DeleteMatter(string id)
    {
        lock (_gate)
        {
            PrepareWrite(); var matter = GetMatter(id);
            if (matter.InspectionCount + matter.IssueCount + matter.ActivityCount > 0) throw new ApiException(409, "事项已有相关记录，不能删除；可以记录撤销结论后办结。");
            using var command = Command("DELETE FROM matters WHERE id=$id;", null, ("$id", id)); command.ExecuteNonQuery();
        }
    }
    private MatterReminderDto GetMatterReminders()
    {
        using var count = Command("SELECT COUNT(*),SUM(CASE WHEN deadline<$today THEN 1 ELSE 0 END) FROM matters WHERE status<>'closed';", null, ("$today", Today));
        using var reader = count.ExecuteReader(); reader.Read();
        var pending = reader.GetInt32(0); var overdue = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return new(pending, overdue, ListMatters(new(PageSize: 5)).Items.Where(item => item.Status != "closed").ToArray());
    }
}
