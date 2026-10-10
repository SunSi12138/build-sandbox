using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class WordStoreTests
{
    [Fact]
    public void NormalInspectionExportsRecordButRejectsUnnecessaryNotice()
    {
        using var fixture = new StoreFixture();
        var detail = fixture.Store.CreateInspection(new(fixture.Point.Id, Store.Today, "测试排查人", "normal"));
        var record = fixture.Store.ExportInspectionWord(detail.Inspection.Id, false);
        Assert.True(File.Exists(record.FilePath));
        Assert.StartsWith("排查记录单_", record.FileName, StringComparison.Ordinal);
        Assert.Contains("正常", DocumentText(record.FilePath));
        var error = Assert.Throws<ApiException>(() => fixture.Store.ExportInspectionWord(detail.Inspection.Id, true));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("无需整改通知单", error.Message);
    }

    [Fact]
    public void NoticeRequiresStreetNameAndUsesItWhenIssuingUnitIsEmpty()
    {
        using var fixture = new StoreFixture();
        var detail = fixture.CreateIssue();
        var settings = fixture.Store.GetSettings();
        fixture.Store.SaveSettings(settings with { StreetName = "", IssuingUnit = "" });
        var error = Assert.Throws<ApiException>(() => fixture.Store.ExportInspectionWord(detail.Inspection.Id, true));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("街道全称", error.Message);
        fixture.Store.SaveSettings(settings with { StreetName = "自定义街道办事处", IssuingUnit = "" });
        var notice = fixture.Store.ExportInspectionWord(detail.Inspection.Id, true);
        Assert.Contains("自定义街道办事处", DocumentText(notice.FilePath));
    }

    [Theory]
    [InlineData("", "2026-10-31", null)]
    [InlineData("2026-02-30", "2026-10-31", null)]
    [InlineData("2026-10-31", "2026-10-01", null)]
    [InlineData("2026-10-01", "2026-10-31", "missing-community")]
    [InlineData("2026-10-01", "2026-10-31", " ")]
    public void LedgerRejectsInvalidDatesRangesAndUnknownCommunities(string from, string to, string? community)
    {
        using var fixture = new StoreFixture();
        var error = Assert.Throws<ApiException>(() => fixture.Store.ExportLedgerWord(from, to, community));
        Assert.Equal(400, error.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.docx", SearchOption.AllDirectories));
    }

    [Fact]
    public void StoreGathersSiteOnlyForRecordAndNoticeAndDeduplicatesLedgerPhotos()
    {
        using var fixture = new StoreFixture();
        var overall = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(1600, 800), "site");
        var first = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(1200, 800), "site");
        var second = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(1000, 800), "site");
        var detail = fixture.Store.CreateInspection(new(fixture.Point.Id, Store.Today, "测试排查人", "issues", "现场备注", "手填-01",
            [new("问题甲", "要求甲", Store.Today, PhotoIds: [first.Id]), new("问题乙", "要求乙", Store.Today, PhotoIds: [second.Id])], [overall.Id]));
        var issue = detail.Issues[0];
        var after = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(800, 1200), "after");
        fixture.Store.RecordFeedback(issue.Id, new(Store.Today, "整改已完成", [after.Id]));
        var review = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(500, 700), "review");
        fixture.Store.ReviewAndClose(issue.Id, new(Store.Today, "复查通过", [review.Id]));
        fixture.Store.SaveCommunity(fixture.Community.Id, new("改名后的社区", "改名后的居民委员会"));
        fixture.Store.SavePoint(fixture.Point.Id, new("改名后的点位", fixture.Community.Id, fixture.Point.TypeId, "新地址", Frequency: "weekly"));

        foreach (var notice in new[] { false, true })
        {
            var file = fixture.Store.ExportInspectionWord(detail.Inspection.Id, notice);
            Assert.Equal(new[] { "1000x800", "1200x800", "1600x800" }, MediaDimensions(file.FilePath));
            var text = DocumentText(file.FilePath);
            Assert.Contains(fixture.Point.Name, text);
            Assert.Contains(fixture.Community.Name, text);
            Assert.DoesNotContain("改名后的点位", text);
            Assert.DoesNotContain("改名后的居民委员会", text);
        }
        var withoutPhotos = fixture.Store.ExportLedgerWord(Store.Today, Store.Today, includePhotos: false);
        Assert.Empty(MediaDimensions(withoutPhotos.FilePath));
        var withPhotos = fixture.Store.ExportLedgerWord(Store.Today, Store.Today, includePhotos: true);
        Assert.Equal(new[] { "1000x800", "1200x800", "1600x800", "500x700", "800x1200" }, MediaDimensions(withPhotos.FilePath));
    }

    [Fact]
    public void BrokenPhotoCannotLeaveAnIncompleteTemporaryWordFile()
    {
        using var fixture = new StoreFixture();
        var photo = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(640, 480), "site");
        var detail = fixture.CreateIssue(photo.Id);
        var photoPath = Directory.EnumerateFiles(fixture.Store.PhotosDir, photo.Id + ".jpg", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(photoPath, [0xff, 0xd8]);
        Assert.Throws<InvalidDataException>(() => fixture.Store.ExportInspectionWord(detail.Inspection.Id, false));
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory, "*.docx", SearchOption.AllDirectories));
    }

    [Fact]
    public void LedgerWithoutPhotoOptionWorksEvenWhenAPhotoFileNeedsRecovery()
    {
        using var fixture = new StoreFixture();
        var photo = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(640, 480), "site");
        var detail = fixture.CreateIssue(photo.Id);
        File.Delete(Directory.EnumerateFiles(fixture.Store.PhotosDir, photo.Id + ".jpg", SearchOption.AllDirectories).Single());
        var ledger = fixture.Store.ExportLedgerWord(Store.Today, Store.Today);
        Assert.Empty(MediaDimensions(ledger.FilePath));
        var error = Assert.Throws<ApiException>(() => fixture.Store.ExportInspectionWord(detail.Inspection.Id, false));
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("照片文件缺失", error.Message);
    }

    private static string DocumentText(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var input = (archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Missing document.xml")).Open();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Concat(XDocument.Load(input).Descendants(w + "t").Select(element => element.Value));
    }

    private static string[] MediaDimensions(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)).Select(entry =>
        {
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            var size = JpegMetadata.Read(bytes.ToArray());
            return $"{size.Width}x{size.Height}";
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "StreetEcoWordStoreTests-" + Guid.NewGuid().ToString("N"));
        public Store Store { get; }
        public CommunityDto Community { get; }
        public PointDto Point { get; }
        public StoreFixture()
        {
            Store = new Store(Directory);
            var settings = Store.GetSettings();
            Store.SaveSettings(settings with { StreetName = "测试街道办事处", IssuingUnit = "测试街道办事处", DefaultInspector = "测试排查人" });
            Community = Store.SaveCommunity(null, new("原始社区", "原始社区居民委员会"));
            Point = Store.SavePoint(null, new("原始点位", Community.Id, Store.GetPointTypes()[0].Id, "原始地址", Frequency: "weekly"));
        }
        public InspectionDetailDto CreateIssue(string? photoId = null) => Store.CreateInspection(new(Point.Id, StreetEcoTool.Store.Today, "测试排查人", "issues", Issues:
            [new("环境问题", "清理并保持", StreetEcoTool.Store.Today, PhotoIds: photoId is null ? [] : [photoId])]));
        public void Dispose() { Store.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
}
