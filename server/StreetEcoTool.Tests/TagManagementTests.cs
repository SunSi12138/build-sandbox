using System.IO.Compression;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class TagManagementTests
{
    [Fact]
    public void TagNamesNormalizeAndUsageProtectsDeletionWhileDisabledHistoryRemainsEditable()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var tag = store.SaveTag(null, new("  夏季攻坚  ")); Assert.Equal("夏季攻坚", tag.Name);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SaveTag(null, new("夏季攻坚"))).StatusCode);
        store.SetTags("point", point.Id, [tag.Id]);
        Assert.Equal(1, Assert.Single(store.GetTags()).UsageCount);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeleteTag(tag.Id)).StatusCode);
        store.SaveTag(tag.Id, new("夏季行动", true));
        var preserved = Assert.Single(store.SetTags("point", point.Id, [tag.Id]));
        Assert.True(preserved.Disabled); Assert.Equal("夏季行动", preserved.Name);
        var second = store.SaveMatter(null, new("事项", "other", Store.Today, "待核实"));
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SetTags("matter", second.Id, [tag.Id])).StatusCode);
        Assert.Empty(store.GetMatter(second.Id).Tags);
        store.SetTags("point", point.Id, []); store.DeleteTag(tag.Id);
        Assert.Empty(store.GetTags()); Assert.Empty(store.GetPoint(point.Id).Tags);
    }

    [Fact]
    public void TagFiltersUseAndSemanticsAndCountDistinctOwnersAcrossAllModules()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var a = store.SaveTag(null, new("专项")); var b = store.SaveTag(null, new("重点"));
        var point = env.CreatePoint(store); store.SetTags("point", point.Id, [a.Id, b.Id]);
        var secondPoint = store.SavePoint(null, new("另一个点位", point.CommunityId, point.TypeId, "其他地址")); store.SetTags("point", secondPoint.Id, [a.Id]);
        var matter = store.SaveMatter(null, new("共同事项", "assigned", Store.Today, "交办内容", TagIds: [a.Id, b.Id]));
        store.SaveMatter(null, new("其他事项", "other", Store.Today, "其他内容", TagIds: [a.Id]));
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "normal", TagIds: [a.Id, b.Id]));
        store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "normal", TagIds: [a.Id]));
        var type = store.GetActivityTypes().First(t => t.Template == "general");
        var activity = store.SaveActivity(null, new("相关活动", type.Id, Store.Today, "活动内容", TagIds: [a.Id, b.Id]));
        store.SaveActivity(null, new("其他活动", type.Id, Store.Today, "活动内容", TagIds: [a.Id]));
        Assert.Equal(8, store.GetTags().Single(t => t.Id == a.Id).UsageCount);
        Assert.Equal(4, store.GetTags().Single(t => t.Id == b.Id).UsageCount);
        var points = store.ListPoints(new(TagIds: [a.Id, b.Id])); Assert.Equal(1, points.Total); Assert.Equal(point.Id, Assert.Single(points.Items).Id);
        var matters = store.ListMatters(new(TagIds: [a.Id, b.Id])); Assert.Equal(1, matters.Total); Assert.Equal(matter.Id, Assert.Single(matters.Items).Id);
        var inspections = store.ListInspections(new(TagIds: [a.Id, b.Id])); Assert.Equal(1, inspections.Total); Assert.Equal(inspection.Inspection.Id, Assert.Single(inspections.Items).Id);
        var activities = store.ListActivities(new(TagIds: [a.Id, b.Id])); Assert.Equal(1, activities.Total); Assert.Equal(activity.Activity.Id, Assert.Single(activities.Items).Id);
        Assert.Empty(store.ListPoints(new(TagIds: ["missing"])).Items);
        Assert.Equal(inspection.Inspection.Id, Assert.Single(store.GetLedgerInspections(Store.Today, Store.Today, filters: new(TagIds: [a.Id, b.Id]))).Id);
        Assert.Equal(activity.Activity.Id, Assert.Single(store.GetLedgerActivities(Store.Today, Store.Today, filters: new(TagIds: [a.Id, b.Id]))).Id);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void RemediationLedgerCombinesMatterAndTagFiltersAndExportsOnlyMatchingWork()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var a = store.SaveTag(null, new("专项")); var b = store.SaveTag(null, new("重点"));
        var matter = store.SaveMatter(null, new("事项", "assigned", Store.Today, "交办内容"));
        var type = store.GetActivityTypes().Single(t => t.Template == "remediation");
        var request = new ActivityWriteRequest("匹配整治名称", type.Id, Store.Today, "匹配开展情况", PointId: point.Id,
            Remediation: new(store.GetCampaignTypes()[0].Id), TagIds: [a.Id, b.Id], MatterIds: [matter.Id]);
        var match = store.SaveActivity(null, request);
        store.SaveActivity(null, request with { Name = "缺少标签整治", TagIds = [a.Id] });
        store.SaveActivity(null, request with { Name = "未关联事项整治", MatterIds = [] });
        var filters = new CampaignQuery(TagIds: [a.Id, b.Id], MatterId: matter.Id);
        Assert.Equal(match.Activity.Id, Assert.Single(store.GetLedgerCampaigns(Store.Today, Store.Today, filters: filters)).Id);
        Assert.Empty(store.GetLedgerCampaigns(Store.Today, Store.Today, filters: filters with { ActivityTypeId = store.GetActivityTypes().First(t => t.Template == "general").Id }));
        var export = store.ExportCampaignLedgerWord(Store.Today, Store.Today, filters: filters);
        using var package = ZipFile.OpenRead(export.FilePath);
        using var reader = new StreamReader(package.GetEntry("word/document.xml")!.Open()); var xml = reader.ReadToEnd();
        Assert.Contains("匹配整治名称", xml); Assert.DoesNotContain("缺少标签整治", xml); Assert.DoesNotContain("未关联事项整治", xml);
    }

    [Fact]
    public void TagAssignmentValidatesLimitDuplicatesMissingIdsAndRollsBackWholeReplacement()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var tags = Enumerable.Range(0, 21).Select(i => store.SaveTag(null, new("标签" + i))).ToArray();
        store.SetTags("point", point.Id, tags.Take(20).Select(t => t.Id).ToArray());
        Assert.Equal(20, store.GetPoint(point.Id).Tags.Length);
        foreach (var invalid in new[] { tags.Select(t => t.Id).ToArray(), new[] { tags[0].Id, tags[0].Id }, new[] { tags[0].Id, "missing" }, new[] { " " } })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => store.SetTags("point", point.Id, invalid)).StatusCode);
            Assert.Equal(20, store.GetPoint(point.Id).Tags.Length);
            Assert.Equal(1, store.GetTags().Single(t => t.Id == tags[19].Id).UsageCount);
        }
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.SetTags("point", "missing", [])).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SetTags("invalid", point.Id, [])).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveTag(null, new(new string('长', 41)))).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveTag(null, new(" "))).StatusCode);
    }
}
