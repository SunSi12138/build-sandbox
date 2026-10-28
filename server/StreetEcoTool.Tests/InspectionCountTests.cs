using Xunit;

namespace StreetEcoTool.Tests;

public sealed class InspectionCountTests
{
    [Fact]
    public void CountsKeepMixedIssueStatesMatterTagsAndIssueTypeFilters()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var tag = store.SaveTag(null, new("统计专项"));
        var matter = store.SaveMatter(null, new("统计事项", "public", Store.Today, "现场核查"));
        var typeA = store.GetIssueTypes()[0]; var typeB = store.GetIssueTypes()[1];
        IssueCreateRequest Issue(string type) => new("待处理问题", "整改", Store.Today, IssueTypeId: type);
        InspectionDetailDto Add(string result, IssueCreateRequest[]? issues = null) => store.CreateInspection(new(point.Id, Store.Today, "经办人", result, Issues: issues, TagIds: [tag.Id], MatterIds: [matter.Id]));
        Add("normal");
        Add("issues", [Issue(typeA.Id), Issue(typeB.Id)]);
        var feedback = Add("issues", [Issue(typeA.Id)]);
        store.RecordFeedback(feedback.Issues[0].Id, new(Store.Today, "已处理"));
        var closed = Add("issues", [Issue(typeB.Id)]);
        store.RecordFeedback(closed.Issues[0].Id, new(Store.Today, "已处理"));
        store.ReviewAndClose(closed.Issues[0].Id, new(Store.Today, "复查通过"));
        store.CreateInspection(new(point.Id, Store.Today, "不属于筛选", "issues", Issues: [Issue(typeA.Id)]));

        var filters = new InspectionQuery(TagIds: [tag.Id], MatterId: matter.Id, Rectify: "closed");
        Assert.Equal(new InspectionCountsDto(4, 1, 1, 1, 1, 4), store.GetInspectionCounts(filters));
        Assert.Equal(new InspectionCountsDto(2, 0, 1, 1, 0, 2), store.GetInspectionCounts(filters with { IssueTypeId = typeA.Id }));
        Assert.Equal(new InspectionCountsDto(1, 0, 0, 1, 0, 1), store.GetInspectionCounts(filters with { AwaitingReview = true }));
        Assert.Equal(new InspectionCountsDto(0, 0, 0, 0, 0, 0), store.GetInspectionCounts(filters with { Q = "不存在的排查" }));
    }
}
