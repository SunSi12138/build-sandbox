using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityArchiveDraftTests
{
    private static JsonElement Data(string value) { using var d = JsonDocument.Parse(value); return d.RootElement.Clone(); }
    [Fact]
    public void ArchiveRemovesLiveCountsAndRestoreRetainsOriginalPhotos()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(80, 60), "site");
        var record = store.CreateInspection(new(point.Id, Store.Today, "张三", "issues", Issues: [new("发现问题", "整改", Store.Today, PhotoIds: [photo.Id])]));
        var issue = record.Issues[0]; var id = record.Inspection.Id;
        store.SetArchived("issue", issue.Id, new(true, "重复录入", issue.Revision));
        Assert.Empty(store.ListIssues(new()).Items); Assert.Equal(0, store.GetPoint(point.Id).OpenIssueCount);
        Assert.Equal("voided", store.GetInspection(id).RectifyStatus);
        Assert.Empty(store.GetReport(new("issues")).Rows);
        store.SetArchived("inspection", id, new(true, "登记有误", store.GetInspection(id).Revision));
        Assert.Empty(store.ListInspections(new()).Items); Assert.Null(store.GetPoint(point.Id).LastInspectionDate);
        Assert.Throws<ApiException>(() => store.UpdateInspection(id, new(Store.Today, "李四", Revision: store.GetInspection(id).Revision)));
        store.SetArchived("inspection", id, new(false, "核对无误", store.GetInspection(id).Revision));
        Assert.True(store.GetIssue(issue.Id).Voided);
        store.SetArchived("issue", issue.Id, new(false, "继续办理", store.GetIssue(issue.Id).Revision));
        Assert.Single(store.ListIssues(new()).Items); Assert.Equal(1, store.GetPoint(point.Id).OpenIssueCount);
        using var bytes = store.OpenPhoto(photo.Id); Assert.True(bytes.Length > 0);
        Assert.Empty(store.GetArchivedRecords());
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
