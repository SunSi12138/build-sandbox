using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PointReassignmentTests
{
    [Fact]
    public void InspectionCanBeReassignedWithoutLosingIssuesPhotosOrLinks()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var source = environment.CreatePoint(store);
        var community = store.SaveCommunity(null, new("目标社区", "目标居民委员会"));
        var target = store.SavePoint(null, new("正确点位", community.Id, source.TypeId, "正确路段"));
        var site = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "site");
        var issuePhoto = store.UploadPhoto(WordExporterTests.Jpeg(140, 90), "site");
        var inspection = store.CreateInspection(new(source.Id, Store.Today, "工作人员", "issues",
            Issues: [new("发现垃圾", "要求清理", Store.Today, PhotoIds: [issuePhoto.Id], IssueTypeId: store.GetIssueTypes()[0].Id)],
            PhotoIds: [site.Id]));
        var issueId = Assert.Single(inspection.Issues).Id;

        Assert.Equal(400, Assert.Throws<ApiException>(() => store.ReassignInspectionPoint(
            inspection.Inspection.Id, new(source.Id))).StatusCode);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.ReassignInspectionPoint(
            inspection.Inspection.Id, new("missing"))).StatusCode);
        Assert.Equal(source.Id, store.GetInspection(inspection.Inspection.Id).PointId);

        var moved = store.ReassignInspectionPoint(inspection.Inspection.Id, new(target.Id));
        Assert.Equal(target.Id, moved.Inspection.PointId);
        Assert.Equal(target.Name, moved.Inspection.PointName);
        Assert.Equal(community.Name, moved.Inspection.CommunityName);
        Assert.Equal(community.Title, moved.Inspection.CommunityTitle);
        Assert.Equal(target.Address, moved.Inspection.Address);
        Assert.Equal(issueId, Assert.Single(moved.Issues).Id);
        Assert.Equal(target.Id, store.GetIssue(issueId).PointId);
        Assert.Contains(moved.Photos, photo => photo.Id == site.Id);
        Assert.Contains(moved.Photos, photo => photo.Id == issuePhoto.Id);
        using (var original = store.OpenPhoto(site.Id)) Assert.True(original.CanRead);
        using (var original = store.OpenPhoto(issuePhoto.Id)) Assert.True(original.CanRead);
        Assert.Empty(store.GetPointDetail(source.Id).Inspections);
        Assert.Equal(inspection.Inspection.Id, Assert.Single(store.GetPointDetail(target.Id).Inspections).Id);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void RemediationCanBeReassignedWithoutChangingPhotosOrLinkedIssues()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var source = environment.CreatePoint(store);
        var community = store.SaveCommunity(null, new("另一社区", "另一社区居民委员会"));
        var target = store.SavePoint(null, new("最终保留点位", community.Id, source.TypeId, "正确地点"));
        var inspection = store.CreateInspection(new(source.Id, Store.Today, "检查员", "issues",
            Issues: [new("污染", "整改", Store.Today, IssueTypeId: store.GetIssueTypes()[0].Id)]));
        var issue = Assert.Single(inspection.Issues);
        var before = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var type = store.GetActivityTypes().Single(value => value.Template == "remediation");
        var activity = store.SaveActivity(null, new("整治", type.Id, Store.Today, "治理污染",
            PointId: source.Id, Remediation: new(store.GetCampaignTypes()[0].Id, BeforePhotoIds: [before.Id]),
            IssueIds: [issue.Id]));
        var activityId = activity.Activity.Id;

        var movedInspection = store.ReassignInspectionPoint(inspection.Inspection.Id, new(target.Id));
        var movedActivity = store.ReassignActivityPoint(activityId, new(target.Id));
        Assert.Equal(target.Id, movedInspection.Inspection.PointId);
        Assert.Equal(target.Id, movedActivity.Activity.PointId);
        Assert.Equal(target.Name, movedActivity.Activity.PointName);
        Assert.Equal(community.Name, movedActivity.Activity.CommunityName);
        Assert.Equal(target.Address, movedActivity.Activity.Location);
        Assert.Equal(issue.Id, Assert.Single(movedActivity.Activity.RelatedIssues).Id);
        Assert.Equal(before.Id, Assert.Single(movedActivity.Photos).Id);
        using (var preserved = store.OpenPhoto(before.Id)) Assert.True(preserved.CanRead);
        Assert.Empty(store.GetPointDetail(source.Id).Activities!);
        Assert.Equal(activityId, Assert.Single(store.GetPointDetail(target.Id).Activities!).Id);

        store.SetPointDisabled(source.Id, new(true));
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.ReassignActivityPoint(activityId,
            new(source.Id))).StatusCode);
        Assert.Equal(target.Id, store.GetActivity(activityId).PointId);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
