using Xunit;

namespace StreetEcoTool.Tests;

public sealed class MatterRelationTests
{
    private static string Day(int offset) => DateOnly.Parse(Store.Today).AddDays(offset).ToString("yyyy-MM-dd");
    private static MatterWriteRequest Request(string title = "油烟反映") => new(title, "public", Day(-3), "居民反映夜间油烟", Handler: "经办人", Deadline: Day(-1));

    [Theory]
    [InlineData("public")]
    [InlineData("hotline")]
    [InlineData("assigned")]
    [InlineData("discovered")]
    [InlineData("other")]
    public void MatterSourcesCreatePendingOverdueRecordWithoutPoint(string source)
    {
        using var env = new V2Environment(); using var store = env.Open();
        var matter = store.SaveMatter(null, Request() with { Source = source, Title = "  油烟反映  " });
        Assert.Equal("油烟反映", matter.Title);
        Assert.Equal("pending", matter.Status); Assert.Null(matter.PointId); Assert.True(matter.Overdue);
        Assert.Empty(store.GetMatterDetail(matter.Id).Actions);
        Assert.Equal(matter.Id, Assert.Single(store.ListMatters(new(Source: source, Overdue: true)).Items).Id);
        Assert.Equal(1, store.ListMatters(new()).Total);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("title")]
    [InlineData("future")]
    [InlineData("deadline")]
    [InlineData("point")]
    public void InvalidMatterCreationLeavesNoPartialRecord(string invalid)
    {
        using var env = new V2Environment(); using var store = env.Open();
        var request = invalid switch {
            "source" => Request() with { Source = "invalid" },
            "title" => Request() with { Title = " " },
            "future" => Request() with { ReceivedDate = Day(1), Deadline = null },
            "deadline" => Request() with { Deadline = Day(-4) },
            _ => Request() with { PointId = "missing" }
        };
        Assert.Throws<ApiException>(() => store.SaveMatter(null, request));
        Assert.Empty(store.ListMatters(new()).Items);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void SharedInspectionKeepsIndividualIssueMatterLinksIndependentAndAtomic()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var point = env.CreatePoint(store);
        var first = store.SaveMatter(null, Request("油烟")); var second = store.SaveMatter(null, Request("垃圾"));
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "issues", Source: "matter", MatterIds: [first.Id, second.Id],
            Issues: [new("油烟问题", "治理", Store.Today, MatterIds: [first.Id]), new("垃圾问题", "清理", Store.Today, MatterIds: [second.Id])]));
        Assert.Equal(2, inspection.Inspection.Matters.Length);
        foreach (var (matter, issue) in new[] { (first, inspection.Issues[0]), (second, inspection.Issues[1]) })
        {
            var detail = store.GetMatterDetail(matter.Id);
            Assert.Equal("processing", detail.Matter.Status);
            Assert.Equal(1, detail.Matter.InspectionCount); Assert.Equal(1, detail.Matter.IssueCount); Assert.Equal(1, detail.Matter.OpenIssueCount);
            Assert.Equal(issue.Id, Assert.Single(detail.Issues).Id);
            Assert.Equal(matter.Id, Assert.Single(store.GetIssueDetail(issue.Id).Issue.Matters).Id);
        }
        Assert.Throws<ApiException>(() => store.SetMatterLinks("issue", inspection.Issues[0].Id, [second.Id, "missing"]));
        Assert.Equal(first.Id, Assert.Single(store.GetIssueDetail(inspection.Issues[0].Id).Issue.Matters).Id);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeleteMatter(first.Id)).StatusCode);
        store.SetMatterLinks("issue", inspection.Issues[0].Id, [first.Id, second.Id]);
        Assert.Equal(2, store.GetMatter(second.Id).IssueCount);
        store.SetMatterLinks("inspection", inspection.Inspection.Id, []);
        Assert.Equal("routine", store.GetInspection(inspection.Inspection.Id).Source);
        Assert.Equal(0, store.GetMatter(first.Id).InspectionCount);
        Assert.Equal(1, store.GetMatter(first.Id).IssueCount);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void MultipleMatterInspectionDoesNotGuessIssueLinksAndInvalidSourceRollsBack()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var first = store.SaveMatter(null, Request("第一事项")); var second = store.SaveMatter(null, Request("第二事项"));
        var request = new InspectionCreateRequest(point.Id, Store.Today, "检查员", "issues", Source: "matter", MatterIds: [first.Id, second.Id], Issues: [new("现场问题", "整改", Store.Today)]);
        var created = store.CreateTypedInspection(request);
        Assert.Empty(Assert.Single(created.Issues).Matters);
        Assert.Equal(0, store.GetMatter(first.Id).IssueCount); Assert.Equal(0, store.GetMatter(second.Id).IssueCount);
        foreach (var invalid in new[] { request with { Source = "invalid" }, request with { MatterIds = [] }, request with { MatterIds = [first.Id, "missing"] } })
            Assert.Throws<ApiException>(() => store.CreateTypedInspection(invalid));
        Assert.Equal(1, store.ListInspections(new()).Total);
        Assert.Equal(1, store.GetMatter(first.Id).InspectionCount);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void CombinedRelationsRollbackBothAssociationsWhenLaterMatterValidationFailsThenCommitTogether()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var oldTag = store.SaveTag(null, new("原标签")); var newTag = store.SaveTag(null, new("新标签"));
        var oldMatter = store.SaveMatter(null, Request("原事项")); var newMatter = store.SaveMatter(null, Request("新事项"));
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "normal", Source: "matter", MatterIds: [oldMatter.Id], TagIds: [oldTag.Id]));
        var id = inspection.Inspection.Id;
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.SetRelations("inspection", id, new([newTag.Id], [newMatter.Id, "missing"]))).StatusCode);
        var unchanged = store.GetInspection(id);
        Assert.Equal(oldTag.Id, Assert.Single(unchanged.Tags).Id); Assert.Equal(oldMatter.Id, Assert.Single(unchanged.Matters).Id);
        Assert.Equal("matter", unchanged.Source);
        Assert.Equal(1, store.GetMatter(oldMatter.Id).InspectionCount); Assert.Equal(0, store.GetMatter(newMatter.Id).InspectionCount);
        Assert.Equal("pending", store.GetMatter(newMatter.Id).Status);
        Assert.Equal(1, store.GetTags().Single(t => t.Id == oldTag.Id).UsageCount); Assert.Equal(0, store.GetTags().Single(t => t.Id == newTag.Id).UsageCount);
        store.SetRelations("inspection", id, new([newTag.Id], [newMatter.Id]));
        var saved = store.GetInspection(id);
        Assert.Equal(newTag.Id, Assert.Single(saved.Tags).Id); Assert.Equal(newMatter.Id, Assert.Single(saved.Matters).Id);
        Assert.Equal(0, store.GetMatter(oldMatter.Id).InspectionCount); Assert.Equal(1, store.GetMatter(newMatter.Id).InspectionCount);
        Assert.Equal("processing", store.GetMatter(newMatter.Id).Status);
        Assert.Equal(0, store.GetTags().Single(t => t.Id == oldTag.Id).UsageCount); Assert.Equal(1, store.GetTags().Single(t => t.Id == newTag.Id).UsageCount);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void CombinedTagSavePreservesOtherInspectionSourceUntilActualMatterLinkIsRemoved()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var tag = store.SaveTag(null, new("新标签")); var matter = store.SaveMatter(null, Request());
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "normal", Source: "other"));
        var id = inspection.Inspection.Id;
        store.SetRelations("inspection", id, new([tag.Id], []));
        var tagged = store.GetInspection(id);
        Assert.Equal("other", tagged.Source); Assert.Equal(tag.Id, Assert.Single(tagged.Tags).Id); Assert.Empty(tagged.Matters);
        store.SetRelations("inspection", id, new([tag.Id], [matter.Id]));
        Assert.Equal("matter", store.GetInspection(id).Source); Assert.Equal(matter.Id, Assert.Single(store.GetInspection(id).Matters).Id);
        store.SetRelations("inspection", id, new([tag.Id], []));
        var unlinked = store.GetInspection(id);
        Assert.Equal("routine", unlinked.Source); Assert.Empty(unlinked.Matters); Assert.Equal(tag.Id, Assert.Single(unlinked.Tags).Id);
        Assert.Equal(0, store.GetMatter(matter.Id).InspectionCount);
    }

    [Fact]
    public void ResolvedClosureRequiresClosedIssuesAndReopenPreservesChronologicalHistory()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var point = env.CreatePoint(store); var matter = store.SaveMatter(null, Request());
        var inspection = store.CreateTypedInspection(new(point.Id, Day(-2), "检查员", "issues", Source: "matter", MatterIds: [matter.Id], Issues: [new("问题", "整改", Store.Today)]));
        var issue = Assert.Single(inspection.Issues);
        Assert.Equal(matter.Id, Assert.Single(issue.Matters).Id);
        store.AddMatterAction(matter.Id, new("reply", Day(-1), "已告知办理进展"));
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.AddMatterAction(matter.Id, new("close", Store.Today, "已解决", Outcome: "resolved"))).StatusCode);
        Assert.Single(store.GetMatterDetail(matter.Id).Actions);
        store.RecordFeedback(issue.Id, new(Store.Today, "整改完成")); store.ReviewAndClose(issue.Id, new(Store.Today, "复查通过"));
        var closed = store.AddMatterAction(matter.Id, new("close", Store.Today, "处理完成", Outcome: "resolved"));
        Assert.Equal("closed", closed.Matter.Status); Assert.Equal(Store.Today, closed.Matter.ClosedDate); Assert.False(closed.Matter.Overdue);
        Assert.Equal("处理完成", closed.Matter.Conclusion); Assert.Equal("resolved", closed.Matter.Outcome);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SaveMatter(matter.Id, Request())).StatusCode);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.AddMatterAction(matter.Id, new("note", Store.Today, "不应允许"))).StatusCode);
        var reopened = store.AddMatterAction(matter.Id, new("reopen", Store.Today, "补充核实"));
        Assert.Equal("processing", reopened.Matter.Status); Assert.Null(reopened.Matter.ClosedDate); Assert.Equal("", reopened.Matter.Conclusion);
        Assert.Equal(new[] { "reply", "close", "reopen" }, reopened.Actions.Select(x => x.Kind));
        Assert.Equal("resolved", reopened.Actions[1].Outcome);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.AddMatterAction(matter.Id, new("note", Day(-1), "回填旧日期"))).StatusCode);
        Assert.Equal(3, store.GetMatterDetail(matter.Id).Actions.Length);
    }

    [Theory]
    [InlineData("no_issue")]
    [InlineData("transferred")]
    [InlineData("withdrawn")]
    public void AdministrativeClosurePreservesOpenIssueAndIndependentOutcome(string outcome)
    {
        using var env = new V2Environment(); using var store = env.Open();
        var point = env.CreatePoint(store); var matter = store.SaveMatter(null, Request());
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "issues", Issues: [new("问题", "整改", Store.Today, MatterIds: [matter.Id])]));
        var closed = store.AddMatterAction(matter.Id, new("close", Store.Today, "事项办理结论", Outcome: outcome));
        Assert.Equal("closed", closed.Matter.Status); Assert.Equal(outcome, closed.Matter.Outcome); Assert.Equal(1, closed.Matter.OpenIssueCount);
        Assert.Equal("pending", store.GetIssueDetail(inspection.Issues[0].Id).Issue.Status);
    }

    [Fact]
    public void ActionValidationAndReceptionEditsCannotRewriteHistory()
    {
        using var env = new V2Environment(); using var store = env.Open(); var matter = store.SaveMatter(null, Request());
        foreach (var request in new[] {
            new MatterActionRequest("invalid", Store.Today, "内容"), new("reopen", Store.Today, "内容"),
            new("note", Day(-4), "内容"), new("note", Day(1), "内容"), new("note", Store.Today, " "),
            new("note", Store.Today, "内容", Handler: " "), new("close", Store.Today, "结论", Outcome: "invalid") })
            Assert.Throws<ApiException>(() => store.AddMatterAction(matter.Id, request));
        Assert.Empty(store.GetMatterDetail(matter.Id).Actions);
        store.AddMatterAction(matter.Id, new("note", Day(-2), "开始办理"));
        Assert.Throws<ApiException>(() => store.SaveMatter(matter.Id, Request() with { ReceivedDate = Day(-1) }));
        Assert.Equal(Day(-3), store.GetMatter(matter.Id).ReceivedDate);
        Assert.Single(store.GetMatterDetail(matter.Id).Actions);
        store.DeleteMatter(matter.Id);
        Assert.Empty(store.ListMatters(new()).Items);
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM matter_actions;"));
    }
}
