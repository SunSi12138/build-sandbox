using System.IO.Compression;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class UnifiedActivityTests
{
    [Fact]
    public void RemediationBuiltInIdentitySurvivesRenameAndTemplateCannotBeChangedAfterSave()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var builtIn = Assert.Single(store.GetActivityTypes(), t => t.Template == "remediation");
        Assert.True(builtIn.BuiltIn);
        var renamed = store.SaveActivityType(builtIn.Id, new("集中环境治理"));
        Assert.True(renamed.BuiltIn); Assert.Equal("remediation", renamed.Template);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeleteActivityType(builtIn.Id)).StatusCode);
        var ordinary = store.SaveActivityType(null, new("业务交流")); Assert.Equal("general", ordinary.Template); Assert.False(ordinary.BuiltIn);
        var request = new ActivityWriteRequest("清理现场", builtIn.Id, Store.Today, "治理内容", PointId: point.Id, Remediation: new(store.GetCampaignTypes()[0].Id));
        var remediation = store.SaveActivity(null, request);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SaveActivity(remediation.Activity.Id, request with { TypeId = ordinary.Id, Remediation = null })).StatusCode);
        var general = store.SaveActivity(null, new("交流", ordinary.Id, Store.Today, "交流情况"));
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SaveActivity(general.Activity.Id, request)).StatusCode);
        Assert.Equal("remediation", store.GetActivity(remediation.Activity.Id).Template);
        Assert.Equal("general", store.GetActivity(general.Activity.Id).Template);
        Assert.Equal(2, store.ListActivities(new()).Total);
    }

    [Fact]
    public void UnifiedRemediationOwnsStagedPhotosLinksIssuesAndAppearsInPointAndMatterDetails()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var matter = store.SaveMatter(null, new("交办清理", "assigned", Store.Today, "清理要求"));
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "检查员", "issues", Issues: [new("垃圾", "清理", Store.Today)]));
        var before = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var process = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "process");
        var after = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "after");
        var category = store.GetCampaignTypes()[0]; var type = store.GetActivityTypes().Single(t => t.Template == "remediation");
        var activity = store.SaveActivity(null, new("河道清理", type.Id, Store.Today, "开展清理", PointId: point.Id,
            ParticipantCount: 7, Remediation: new(category.Id, 2, 1.235m, 12.345m, [before.Id], [process.Id], [after.Id]), MatterIds: [matter.Id], IssueIds: [inspection.Issues[0].Id]));
        Assert.Equal(new[] { "after", "before", "process" }, activity.Photos.Select(p => p.Phase).Order());
        Assert.All(activity.Photos, p => Assert.Equal(activity.Activity.Id, p.ActivityId));
        Assert.Equal(3, activity.Activity.PhotoCount); Assert.Equal(1.24m, activity.Activity.Remediation!.GarbageTons); Assert.Equal(12.35m, activity.Activity.Remediation.AreaSqm);
        Assert.Equal(inspection.Issues[0].Id, Assert.Single(activity.Activity.RelatedIssues).Id);
        Assert.Equal(activity.Activity.Id, Assert.Single(store.GetPointDetail(point.Id).Activities!).Id);
        var detail = store.GetMatterDetail(matter.Id); Assert.Equal(1, detail.Matter.ActivityCount); Assert.Equal(activity.Activity.Id, Assert.Single(detail.Activities).Id);
        Assert.Equal("pending", store.GetIssue(inspection.Issues[0].Id).Status);
        var filtered = store.ListActivities(new(Template: "remediation", PointId: point.Id, RemediationTypeId: category.Id, MatterId: matter.Id));
        Assert.Equal(1, filtered.Total); Assert.Equal(activity.Activity.Id, Assert.Single(filtered.Items).Id);
        Assert.Empty(store.ListActivities(new(Template: "general")).Items);
        var export = store.ExportCampaignWord(activity.Activity.Id);
        using (var zip = ZipFile.OpenRead(export.FilePath)) Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
        store.DeleteActivity(activity.Activity.Id);
        Assert.Equal(0, store.GetMatter(matter.Id).ActivityCount); Assert.Empty(store.GetPointDetail(point.Id).Activities!);
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM activity_remediations;"));
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM activity_issues;"));
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos;"));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void RejectedPhotoReplacementPreservesExistingFilesAndCrossOwnerPhotoOwnership()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var type = store.GetActivityTypes().Single(t => t.Template == "remediation");
        var before = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var request = new ActivityWriteRequest("整治", type.Id, Store.Today, "内容", PointId: point.Id,
            Remediation: new(store.GetCampaignTypes()[0].Id, BeforePhotoIds: [before.Id]));
        var activity = store.SaveActivity(null, request);
        Assert.Throws<ApiException>(() => store.SaveActivity(null, request));
        Assert.Throws<ApiException>(() => store.SaveActivity(activity.Activity.Id, request with { Remediation = request.Remediation! with { BeforePhotoIds = ["missing"] } }));
        Assert.Equal(before.Id, Assert.Single(store.GetActivityDetail(activity.Activity.Id).Photos).Id);
        Assert.Equal(1, store.ListActivities(new()).Total);
        Assert.Equal(1, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos;"));
        Assert.Throws<ApiException>(() => store.SaveActivity(activity.Activity.Id, request with { Remediation = request.Remediation! with { ProcessPhotoIds = [before.Id] } }));
        Assert.Throws<ApiException>(() => store.SaveActivity(activity.Activity.Id, request with { PhotoIds = [before.Id] }));
        Assert.Equal("before", Assert.Single(store.GetActivityDetail(activity.Activity.Id).Photos).Phase);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Theory]
    [InlineData("missing-remediation")]
    [InlineData("general-with-remediation")]
    [InlineData("missing-point")]
    [InlineData("invalid-category")]
    [InlineData("negative-vehicle")]
    [InlineData("excess-vehicle")]
    [InlineData("negative-tons")]
    [InlineData("excess-area")]
    [InlineData("negative-people")]
    [InlineData("excess-people")]
    [InlineData("future-date")]
    [InlineData("bad-range")]
    [InlineData("invalid-matter")]
    [InlineData("invalid-issue")]
    public void InvalidActivityDoesNotLeaveDomainOrRelationRows(string invalid)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var general = store.GetActivityTypes().First(t => t.Template == "general"); var type = store.GetActivityTypes().Single(t => t.Template == "remediation");
        var remediation = new RemediationWriteRequest(store.GetCampaignTypes()[0].Id);
        var request = new ActivityWriteRequest("活动", type.Id, Store.Today, "内容", PointId: point.Id, Remediation: remediation);
        request = invalid switch {
            "missing-remediation" => request with { Remediation = null },
            "general-with-remediation" => request with { TypeId = general.Id },
            "missing-point" => request with { PointId = null },
            "invalid-category" => request with { Remediation = remediation with { TypeId = "missing" } },
            "negative-vehicle" => request with { Remediation = remediation with { VehicleCount = -1 } },
            "excess-vehicle" => request with { Remediation = remediation with { VehicleCount = 100000001 } },
            "negative-tons" => request with { Remediation = remediation with { GarbageTons = -0.01m } },
            "excess-area" => request with { Remediation = remediation with { AreaSqm = 1000000000000.01m } },
            "negative-people" => request with { ParticipantCount = -1 },
            "excess-people" => request with { ParticipantCount = 100000001 },
            "future-date" => request with { Date = DateOnly.Parse(Store.Today).AddDays(1).ToString("yyyy-MM-dd") },
            "bad-range" => request with { EndDate = DateOnly.Parse(Store.Today).AddDays(-1).ToString("yyyy-MM-dd") },
            "invalid-matter" => request with { MatterIds = ["missing"] },
            _ => request with { IssueIds = ["missing"] }
        };
        Assert.Throws<ApiException>(() => store.SaveActivity(null, request));
        Assert.Empty(store.ListActivities(new()).Items);
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM activity_remediations;"));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100000000, 1000000000000)]
    public void ActivityQuantityInclusiveBoundariesAreAccepted(int count, long quantity)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var type = store.GetActivityTypes().Single(t => t.Template == "remediation");
        var activity = store.SaveActivity(null, new("整治", type.Id, Store.Today, "内容", ParticipantCount: count, PointId: point.Id,
            Remediation: new(store.GetCampaignTypes()[0].Id, count, quantity, quantity)));
        Assert.Equal(count, activity.Activity.ParticipantCount); Assert.Equal(count, activity.Activity.Remediation!.VehicleCount);
        Assert.Equal((decimal)quantity, activity.Activity.Remediation.GarbageTons); Assert.Equal((decimal)quantity, activity.Activity.Remediation.AreaSqm);
    }
}
