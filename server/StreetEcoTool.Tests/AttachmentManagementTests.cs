using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class AttachmentManagementTests
{
    private static byte[] Pdf => Encoding.UTF8.GetBytes("%PDF-1.7\n中文报告 original bytes\n%%EOF");
    private static byte[] Read(Store store, string id) { using var source = store.OpenAttachment(id); using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray(); }

    [Fact]
    public void AttachmentOriginalBytesMetadataAndRenameArePreservedWhilePointDeletionIsProtected()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var file = store.UploadAttachment(point.Id, "  检测报告.PDF  ", Pdf, "原始报告");
        Assert.Equal("检测报告.PDF", file.Name); Assert.Equal("application/pdf", file.ContentType); Assert.True(file.CanPreview);
        Assert.Equal(Pdf.Length, file.Size); Assert.Equal(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant(), file.Sha256);
        Assert.Equal(Pdf, Read(store, file.Id)); Assert.Equal(file.Id, Assert.Single(store.GetPointDetail(point.Id).Attachments!).Id);
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeletePoint(point.Id)).StatusCode);
        var renamed = store.UpdateAttachment(file.Id, new("复核报告.pdf", "补充说明"));
        Assert.Equal("复核报告.pdf", renamed.Name); Assert.Equal("补充说明", renamed.Notes); Assert.Equal(file.Sha256, renamed.Sha256);
        Assert.Throws<ApiException>(() => store.UpdateAttachment(file.Id, new("误改.docx")));
        Assert.Equal("复核报告.pdf", store.GetAttachment(file.Id).Name);
        var other = store.UploadAttachment(point.Id, "另一个.txt", [1, 2, 3]);
        store.DeleteAttachment(file.Id);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.OpenAttachment(file.Id)).StatusCode);
        Assert.Equal(other.Id, Assert.Single(store.GetAttachments(point.Id)).Id); Assert.Equal(new byte[] { 1, 2, 3 }, Read(store, other.Id));
        Assert.Single(Directory.EnumerateFiles(store.AttachmentsDir, "*", SearchOption.AllDirectories));
        store.DeleteAttachment(other.Id); store.DeletePoint(point.Id);
        Assert.Empty(Directory.EnumerateFiles(store.AttachmentsDir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../report.pdf")]
    [InlineData("folder/report.pdf")]
    [InlineData("folder\\report.pdf")]
    [InlineData("report.exe")]
    [InlineData(".")]
    [InlineData(" ")]
    public void InvalidAttachmentNamesLeaveNoRowsOrFiles(string name)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, name, Pdf));
        Assert.Empty(store.GetAttachments(point.Id)); Assert.Empty(Directory.EnumerateFiles(store.AttachmentsDir, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".pdf", "%PDF-", "application/pdf")]
    [InlineData(".txt", "text", "text/plain")]
    [InlineData(".zip", "PK\u0003\u0004", "application/zip")]
    [InlineData(".docx", "PK\u0003\u0004", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".xlsx", "PK\u0003\u0004", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public void SupportedDocumentSignaturesUseExpectedContentType(string extension, string header, string contentType)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var attachment = store.UploadAttachment(point.Id, "文件" + extension, Encoding.UTF8.GetBytes(header));
        Assert.Equal(contentType, attachment.ContentType); Assert.Equal(extension == ".pdf", attachment.CanPreview);
        Assert.Equal(Encoding.UTF8.GetBytes(header), Read(store, attachment.Id));
    }

    [Theory]
    [InlineData(".doc", "D0CF11E0A1B11AE1", "application/msword", false)]
    [InlineData(".xls", "D0CF11E0A1B11AE1", "application/vnd.ms-excel", false)]
    [InlineData(".png", "89504E470D0A1A0A", "image/png", true)]
    [InlineData(".jpg", "FFD8FF", "image/jpeg", true)]
    [InlineData(".jpeg", "FFD8FF", "image/jpeg", true)]
    public void BinaryAttachmentFormatsPreserveOriginalHeaderAndRejectMismatchedBytes(string extension, string hex, string contentType, bool preview)
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var bytes = Convert.FromHexString(hex);
        var attachment = store.UploadAttachment(point.Id, "原文件" + extension, bytes);
        Assert.Equal(bytes, Read(store, attachment.Id)); Assert.Equal(contentType, attachment.ContentType); Assert.Equal(preview, attachment.CanPreview);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, "伪造" + extension, [1, 2, 3])).StatusCode);
        Assert.Equal(attachment.Id, Assert.Single(store.GetAttachments(point.Id)).Id);
    }

    [Fact]
    public void AttachmentLimitsIncludeExactMaximumAndRejectedWritesLeaveExistingFileUntouched()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var exact = new byte[AttachmentStorage.MaximumBytes]; exact[0] = 65; exact[^1] = 90;
        var file = store.UploadAttachment(point.Id, "边界.txt", exact);
        Assert.Equal(AttachmentStorage.MaximumBytes, file.Size); Assert.Equal(exact, Read(store, file.Id));
        Assert.Equal(413, Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, "过大.txt", new byte[AttachmentStorage.MaximumBytes + 1])).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, "空.txt", [])).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, "伪造.pdf", [1, 2, 3])).StatusCode);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.UploadAttachment("missing", "缺失.pdf", Pdf)).StatusCode);
        Assert.Throws<ApiException>(() => store.UploadAttachment(point.Id, "备注无效.pdf", Pdf, new string('长', 2001)));
        Assert.Equal(file.Id, Assert.Single(store.GetAttachments(point.Id)).Id);
        Assert.Single(Directory.EnumerateFiles(store.AttachmentsDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AttachmentPathRejectsTraversalIdentityMismatchAndSymbolicLinks()
    {
        using var env = new V2Environment(); var root = Path.Combine(env.Root, "attachment-test"); Directory.CreateDirectory(root);
        var id = new string('a', 32);
        var valid = "202610/" + id + ".pdf";
        Assert.Equal(Path.Combine(root, "202610", id + ".pdf"), AttachmentStorage.ResolvePath(root, id, valid));
        foreach (var path in new[] { "../" + id + ".pdf", "202613/" + id + ".pdf", "202610/other.pdf", "/202610/" + id + ".pdf", "202610/../" + id + ".pdf" })
            Assert.Throws<InvalidDataException>(() => AttachmentStorage.ResolvePath(root, id, path));
        if (OperatingSystem.IsWindows()) return; // Windows symlink creation requires privileges unavailable on ordinary CI runners.
        var outside = Path.Combine(env.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "202610"), outside);
        Assert.Throws<InvalidDataException>(() => AttachmentStorage.ResolvePath(root, id, valid));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void InterruptedAttachmentDeletionRecoversReferencedFileOnReopen()
    {
        using var env = new V2Environment(); string id; string attachmentDirectory;
        using (var store = env.Open())
        {
            var point = env.CreatePoint(store); var attachment = store.UploadAttachment(point.Id, "报告.pdf", Pdf); id = attachment.Id; attachmentDirectory = store.AttachmentsDir;
            var path = Assert.Single(Directory.EnumerateFiles(store.AttachmentsDir, "*", SearchOption.AllDirectories));
            File.Move(path, path + ".pending-delete");
        }
        using (var reopened = env.Open())
        {
            Assert.Equal(Pdf, Read(reopened, id));
            Assert.DoesNotContain(Directory.EnumerateFiles(attachmentDirectory, "*", SearchOption.AllDirectories), f => f.EndsWith(".pending-delete", StringComparison.Ordinal));
            reopened.DeleteAttachment(id);
        }
        using var final = env.Open();
        Assert.Equal(404, Assert.Throws<ApiException>(() => final.GetAttachment(id)).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(attachmentDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void RemovingLastTemporaryActivityPreservesPointThatOwnsAttachment()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var community = store.SaveCommunity(null, new("社区", "社区"));
        var activity = store.SaveActivity(null, new("清理", store.GetActivityTypes().Single(t => t.Template == "remediation").Id, Store.Today, "情况",
            NewPoint: new("临时点位", community.Id, store.GetPointTypes()[0].Id, "地点"), Remediation: new(store.GetCampaignTypes()[0].Id)));
        var point = activity.Activity.PointId!; var file = store.UploadAttachment(point, "报告.pdf", Pdf);
        store.DeleteActivity(activity.Activity.Id);
        Assert.True(store.GetPoint(point).Temporary); Assert.Equal(Pdf, Read(store, file.Id));
        Assert.Equal(file.Id, Assert.Single(store.GetAttachments(point)).Id);
    }
}
