using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    public InspectionDetailDto RecordInspectionFeedback(string inspectionId, InspectionFeedbackRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetInspection(inspectionId);
            var items = request.Items ?? [];
            ValidateBatchIds(items.Select(item => item?.IssueId).ToArray());
            var validated = items.Select(item =>
            {
                var issue = GetIssue(item.IssueId);
                if (issue.InspectionId != inspectionId) throw new ApiException(400, "所选问题不属于本次排查。");
                if (issue.Status != "pending") throw new ApiException(409, "所选问题当前不是待整改状态，请刷新后重试。");
                return (Issue: issue, Date: ActionDate(request.Date, issue.InspectionDate, "反馈日期"),
                    Notes: Text(item.Notes, "整改情况", 8000, true), item.PhotoIds);
            }).ToArray();
            using (var transaction = _db.BeginTransaction())
            {
                foreach (var item in validated)
                {
                    var actionId = NewId();
                    AddAction(transaction, actionId, item.Issue.Id, "feedback", item.Date, item.Notes);
                    using var update = Command("UPDATE issues SET status='feedback',feedback_date=$date,feedback_notes=$notes,review_date=NULL,review_conclusion='',closed_date=NULL WHERE id=$id;", transaction,
                        ("$id", item.Issue.Id), ("$date", item.Date), ("$notes", item.Notes));
                    update.ExecuteNonQuery();
                    AttachPhotos(item.PhotoIds, "after", null, item.Issue.Id, transaction, actionId);
                }
                transaction.Commit();
            }
            return GetInspectionDetail(inspectionId);
        }
    }

    public InspectionDetailDto ReviewInspection(string inspectionId, InspectionReviewRequest request)
    {
        lock (_gate)
        {
            PrepareWrite();
            _ = GetInspection(inspectionId);
            var items = request.Items ?? [];
            ValidateBatchIds(items.Select(item => item?.IssueId).ToArray());
            var validated = items.Select(item =>
            {
                var issue = GetIssue(item.IssueId);
                if (issue.InspectionId != inspectionId) throw new ApiException(400, "所选问题不属于本次排查。");
                if (issue.Status != "feedback") throw new ApiException(409, "所选问题尚未反馈，请刷新后重试。");
                return (Issue: issue, Date: ActionDate(request.Date, issue.FeedbackDate ?? issue.InspectionDate, "复查日期"),
                    Notes: Text(item.Conclusion, "复查结论", 8000, true), item.PhotoIds);
            }).ToArray();
            using (var transaction = _db.BeginTransaction())
            {
                foreach (var item in validated)
                {
                    var actionId = NewId();
                    AddAction(transaction, actionId, item.Issue.Id, "review", item.Date, item.Notes);
                    using var update = Command("UPDATE issues SET status='closed',review_date=$date,review_conclusion=$notes,closed_date=$date WHERE id=$id;", transaction,
                        ("$id", item.Issue.Id), ("$date", item.Date), ("$notes", item.Notes));
                    update.ExecuteNonQuery();
                    AttachPhotos(item.PhotoIds, "review", null, item.Issue.Id, transaction, actionId);
                }
                transaction.Commit();
            }
            return GetInspectionDetail(inspectionId);
        }
    }

    private static void ValidateBatchIds(string?[] ids)
    {
        if (ids.Length is < 1 or > 50) throw new ApiException(400, "请选择 1 至 50 项问题。");
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ApiException(400, "所选问题包含空项或重复项。");
    }

    private void AddAction(SqliteTransaction transaction, string id, string issueId, string kind, string date, string notes)
    {
        using var action = Command("INSERT INTO issue_actions(id,issue_id,kind,action_date,notes) VALUES($id,$issue,$kind,$date,$notes);", transaction,
            ("$id", id), ("$issue", issueId), ("$kind", kind), ("$date", date), ("$notes", notes));
        action.ExecuteNonQuery();
    }
}
