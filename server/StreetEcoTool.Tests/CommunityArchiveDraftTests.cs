using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityArchiveDraftTests
{
    private static JsonElement Data(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    [Fact]
    public void IssueArchiveRestoresAndInspectionDeletionRemovesAssociatedData()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var record = store.CreateInspection(new(point.Id, Store.Today, "张三", "issues", Issues: [new("发现问题", "整改", Store.Today, PhotoIds: [photo.Id])]));
        var issue = record.Issues[0]; var id = record.Inspection.Id;
        store.SetArchived("issue", issue.Id, new(true, "重复录入", issue.Revision));
        Assert.Empty(store.ListIssues(new()).Items); Assert.Equal(0, store.GetPoint(point.Id).OpenIssueCount);
        Assert.Equal("voided", store.GetInspection(id).RectifyStatus);
        Assert.Empty(store.GetReport(new("issues")).Rows);
        store.SetArchived("issue", issue.Id, new(false, "继续办理", store.GetIssue(issue.Id).Revision));
        Assert.Single(store.ListIssues(new()).Items); Assert.Equal(1, store.GetPoint(point.Id).OpenIssueCount);
        using (var bytes = store.OpenPhoto(photo.Id)) Assert.True(bytes.Length > 0);
        store.DeleteInspection(id);
        Assert.Empty(store.ListInspections(new()).Items); Assert.Empty(store.ListIssues(new()).Items);
        Assert.Null(store.GetPoint(point.Id).LastInspectionDate);
        Assert.Throws<ApiException>(() => store.GetInspection(id));
        Assert.Throws<ApiException>(() => store.OpenPhoto(photo.Id));
        Assert.Empty(store.GetArchivedRecords());
    }

    [Fact]
    public void SharedDraftRetainsInspectionAndTemporaryPointFieldPhotosUntilSubmission()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var ordinary = env.CreatePoint(store);
        var pointType = store.GetPointTypes().First(x => x.Id == ordinary.TypeId);
        store.SavePointType(pointType.Id, new(pointType.Name, [new("place-image", "场所证照", "image", false)]));
        var inspectionType = store.SaveCommunityRecord("inspection-type", null,
            new("", "现场扩展字段", Data("""{"fields":[{"id":"check-image","label":"检查证照","type":"image","required":false}]}""")));
        var site = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var issue = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var field = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var pointPhoto = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "point");
        var draftBody = Data(JsonSerializer.Serialize(new
        {
            submissionKey = "multi-location-draft",
            photos = new[] { new { id = site.Id, width = 80, height = 60 } },
            issues = new[] { new { photos = new[] { new { id = issue.Id, width = 80, height = 60 } } } },
            scope = new { extraPhotoIds = new[] { field.Id }, extraValues = new Dictionary<string, string> { ["check-image"] = field.Id } },
            newPoint = new { photoIds = new[] { pointPhoto.Id }, extraValues = new Dictionary<string, string> { ["place-image"] = pointPhoto.Id } }
        }));
        var draft = store.SaveCommunityRecord("draft", null, new("", "带图片字段的草稿", draftBody));
        Assert.Equal(4, store.GetRecordPhotos(draft.Id).Length);
        V2Environment.Execute(store.DatabasePath, "UPDATE photos SET uploaded_at='2000-01-01T00:00:00';");
        store.CleanupOrphanPhotos();
        Assert.Equal(4, store.GetRecordPhotos(draft.Id).Length);
        foreach (var photo in new[] { site, issue, field, pointPhoto })
        {
            using var stream = store.OpenPhoto(photo.Id);
            Assert.True(stream.Length > 0);
        }
        var extra = Data(JsonSerializer.Serialize(new Dictionary<string, string> { ["check-image"] = field.Id }));
        var pointExtra = Data(JsonSerializer.Serialize(new Dictionary<string, string> { ["place-image"] = pointPhoto.Id }));
        var submitted = store.CreateInspection(new(null, Store.Today, "张三", "issues",
            Issues: [new("检查问题", "立即整改", Store.Today, PhotoIds: [issue.Id])],
            PhotoIds: [site.Id, field.Id],
            NewPoint: new("临时点位", ordinary.CommunityId, pointType.Id, "现场地址",
                PhotoIds: [pointPhoto.Id], ExtraValues: pointExtra),
            InspectionTypeId: inspectionType.Id, ExtraValues: extra, SubmissionKey: "multi-location-draft",
            DraftId: draft.Id, DraftRevision: draft.Revision));
        Assert.Empty(store.GetRecordPhotos(draft.Id));
        Assert.Equal(3, submitted.Photos.Length);
        Assert.Equal(pointPhoto.Id, Assert.Single(store.GetPointDetail(submitted.Inspection.PointId).Photos!).Id);
        Assert.Equal(field.Id, submitted.Inspection.ExtraValues!.Value.GetProperty("check-image").GetString());
        Assert.Equal(pointPhoto.Id, store.GetPoint(submitted.Inspection.PointId).ExtraValues!.Value.GetProperty("place-image").GetString());
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public async Task SharedDraftOwnsPhotosAndBackupRestoresThemBeforeAtomicSubmission()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var data = Data($$"""{"submissionKey":"shared-test","pointId":"{{point.Id}}","photos":[{"id":"{{photo.Id}}","width":80,"height":60}],"issues":[]}""");
        var draft = store.SaveCommunityRecord("draft", null, new("", "检查草稿", data));
        Assert.Throws<ApiException>(() => store.DeletePhoto(photo.Id));
        V2Environment.Execute(store.DatabasePath, "UPDATE photos SET uploaded_at='2000-01-01T00:00:00';"); store.CleanupOrphanPhotos();
        Assert.Single(store.GetRecordPhotos(draft.Id));
        var zip = store.CreateBackupZip();
        using var targetEnv = new V2Environment(); using var target = targetEnv.Open();
        await using var stream = File.OpenRead(zip);
        var preview = await target.PrepareRestoreAsync(stream, default); target.Restore(new(preview.Token, true));
        Assert.Single(target.GetRecordPhotos(draft.Id));
        var create = new InspectionCreateRequest(point.Id, Store.Today, "张三", "normal", PhotoIds: [photo.Id], SubmissionKey: "shared-test", DraftId: draft.Id, DraftRevision: draft.Revision);
        Assert.Throws<ApiException>(() => target.CreateInspection(create with { DraftRevision = 0 }));
        Assert.Single(target.GetRecordPhotos(draft.Id));
        var result = target.CreateInspection(create);
        Assert.Empty(target.ListCommunityRecords("draft")); Assert.Single(result.Photos);
        Assert.Equal(result.Inspection.Id, target.CreateInspection(create).Inspection.Id);
        Assert.Single(target.ListInspections(new()).Items);
        V2Environment.AssertForeignKeys(target.DatabasePath);
    }
}
