using System.IO.Compression;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class FreshDomainBackupTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("changed")]
    [InlineData("traversal")]
    [InlineData("unregistered")]
    public async Task InvalidAttachmentArchiveIsRejectedBeforeChangingLiveRecords(string defect)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var original = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7\noriginal report");
        var attachment = store.UploadAttachment(point.Id, "报告.pdf", original);
        var backup = store.CreateBackupZip();
        using var edited = new MemoryStream();
        using (var source = ZipFile.OpenRead(backup))
        using (var target = new ZipArchive(edited, ZipArchiveMode.Create, true))
        {
            foreach (var entry in source.Entries)
            {
                if (defect == "missing" && entry.FullName.StartsWith("attachments/", StringComparison.Ordinal)) continue;
                using var output = target.CreateEntry(entry.FullName).Open();
                if (defect == "changed" && entry.FullName.StartsWith("attachments/", StringComparison.Ordinal)) output.Write(System.Text.Encoding.UTF8.GetBytes("%PDF-1.7\nmodified report"));
                else { using var input = entry.Open(); input.CopyTo(output); }
            }
            if (defect is "traversal" or "unregistered")
            {
                var path = defect == "traversal" ? "attachments/../../escape.pdf" : "attachments/202610/" + new string('b', 32) + ".pdf";
                using var output = target.CreateEntry(path).Open(); output.Write(original);
            }
        }
        edited.Position = 0;
        await Assert.ThrowsAsync<ApiException>(() => store.PrepareRestoreAsync(edited, CancellationToken.None));
        Assert.Equal(attachment.Id, Assert.Single(store.GetAttachments(point.Id)).Id);
        using var file = store.OpenAttachment(attachment.Id); using var reader = new StreamReader(file);
        Assert.Equal("%PDF-1.7\noriginal report", reader.ReadToEnd());
        Assert.Equal(point.Name, store.GetPoint(point.Id).Name);
        Assert.False(File.Exists(Path.Combine(env.Root, "escape.pdf")));
    }

    [Fact]
    public async Task FullBackupRestoresMatterHistoryTagRelationsAndUnifiedActivityTemplate()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var tag = store.SaveTag(null, new("汛期治理")); store.SetTags("point", point.Id, [tag.Id]);
        var matter = store.SaveMatter(null, new("交办事项", "assigned", Store.Today, "排查并清理", PointId: point.Id, Handler: "张三", TagIds: [tag.Id]));
        var inspection = store.CreateTypedInspection(new(point.Id, Store.Today, "李四", "issues", Source: "matter", MatterIds: [matter.Id], TagIds: [tag.Id], Issues: [new("河道垃圾", "清理干净", Store.Today)]));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var activity = store.SaveActivity(null, new("清理河道", store.GetActivityTypes().Single(t => t.Template == "remediation").Id, Store.Today, "清运垃圾", PointId: point.Id,
            Remediation: new(store.GetCampaignTypes()[0].Id, GarbageTons: 2.25m, BeforePhotoIds: [photo.Id]), TagIds: [tag.Id], MatterIds: [matter.Id], IssueIds: [inspection.Issues[0].Id]));
        store.AddMatterAction(matter.Id, new("reply", Store.Today, "已组织清理"));
        store.SaveTag(tag.Id, new("汛期治理", true));
        var attachment = store.UploadAttachment(point.Id, "报告.pdf", System.Text.Encoding.UTF8.GetBytes("%PDF-1.7\noriginal report"));
        var backup = store.CreateBackupZip();
        store.DeleteAttachment(attachment.Id);
        store.SetTags("point", point.Id, []); store.SetMatterLinks("issue", inspection.Issues[0].Id, []); store.DeleteActivity(activity.Activity.Id);
        store.SaveTag(tag.Id, new("被修改的标签"));
        using var input = File.OpenRead(backup);
        var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
        Assert.Equal(1, preview.PointCount); Assert.Equal(1, preview.InspectionCount); Assert.Equal(1, preview.IssueCount); Assert.Equal(1, preview.PhotoCount);
        Assert.Equal(1, preview.AttachmentCount); Assert.Equal(1, preview.MatterCount); Assert.Equal(1, preview.TagCount);
        Assert.True(store.Restore(new(preview.Token, true)).Ok);
        Assert.Equal(attachment.Sha256, Assert.Single(store.GetAttachments(point.Id)).Sha256);
        using (var restoredFile = store.OpenAttachment(attachment.Id))
        using (var reader = new StreamReader(restoredFile)) Assert.Equal("%PDF-1.7\noriginal report", reader.ReadToEnd());
        var restored = store.GetMatterDetail(matter.Id);
        Assert.Equal("processing", restored.Matter.Status); Assert.Equal(1, restored.Matter.InspectionCount); Assert.Equal(1, restored.Matter.IssueCount); Assert.Equal(1, restored.Matter.ActivityCount);
        Assert.Equal("已组织清理", Assert.Single(restored.Actions).Notes);
        Assert.Equal(matter.Id, Assert.Single(store.GetIssue(inspection.Issues[0].Id).Matters).Id);
        var restoredActivity = Assert.Single(restored.Activities);
        Assert.Equal("remediation", restoredActivity.Template); Assert.Equal(2.25m, restoredActivity.Remediation!.GarbageTons);
        Assert.Equal(inspection.Issues[0].Id, Assert.Single(restoredActivity.RelatedIssues).Id);
        Assert.Equal("汛期治理", Assert.Single(store.GetPoint(point.Id).Tags).Name);
        Assert.True(Assert.Single(store.GetTags()).Disabled); Assert.Equal(4, Assert.Single(store.GetTags()).UsageCount);
        Assert.Equal(photo.Id, Assert.Single(store.GetActivityDetail(activity.Activity.Id).Photos).Id);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
