using System.Globalization;
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
    public void LedgerPreservesFeedbackReviewAndReturnHistoryAfterCurrentFieldsAreCleared()
    {
        using var fixture = new StoreFixture();
        var today = DateOnly.ParseExact(Store.Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        string Day(int offset) => today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var inspection = fixture.Store.CreateInspection(new(fixture.Point.Id, Day(-3), "测试排查人", "issues", Issues:
            [new("反复出现的环境问题", "清理现场并防止再次堆放", Day(0))]));
        var issue = Assert.Single(inspection.Issues);
        const string feedback = "第一次反馈：已完成垃圾清理并设置围挡。";
        const string conclusion = "第一次复查：现场清理符合整改要求。";
        const string reason = "再次发现新增堆放，退回继续整改并加强日常巡查。";
        fixture.Store.RecordFeedback(issue.Id, new(Day(-2), feedback));
        fixture.Store.ReviewAndClose(issue.Id, new(Day(-1), conclusion));
        var returned = fixture.Store.ReturnIssue(issue.Id, new(reason));
        Assert.Equal("pending", returned.Issue.Status);
        Assert.Null(returned.Issue.FeedbackDate);
        Assert.Empty(returned.Issue.FeedbackNotes);
        Assert.Null(returned.Issue.ReviewDate);
        Assert.Empty(returned.Issue.ReviewConclusion);
        Assert.Null(returned.Issue.ClosedDate);

        var ledger = fixture.Store.ExportLedgerWord(Day(-3), Day(0));
        // Reassemble logical columns: continuation context between physical rows
        // must not make a complete historical field appear to be truncated.
        using var archive = ZipFile.OpenRead(ledger.FilePath);
        using var input = archive.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(input);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var table = xml.Descendants(w + "tbl").Single(t => t.Element(w + "tblGrid")?.Elements().Count() == 9);
        var rows = table.Elements(w + "tr").Skip(1).SkipLast(1).ToArray();
        var text = string.Join("\n", Enumerable.Range(0, 9).Select(column => string.Concat(rows.SelectMany(row => row.Elements(w + "tc").ElementAt(column).Descendants(w + "t")).Select(t => t.Value))))
            + string.Concat(table.Elements(w + "tr").Last().Descendants(w + "t").Select(t => t.Value));
        Assert.Contains("待整改", text);
        Assert.Contains("历次反馈", text);
        Assert.Contains(Day(-2), text);
        Assert.Contains(feedback, text);
        Assert.Contains("复查 " + Day(-1), text);
        Assert.Contains(conclusion, text);
        Assert.Contains("退回 " + Day(0), text);
        Assert.Contains(reason, text);
        Assert.True(text.IndexOf(conclusion, StringComparison.Ordinal) < text.IndexOf(reason, StringComparison.Ordinal));
        Assert.Contains("共 1 条，已闭环 0，待复查 0，待整改 1，其中逾期 0", text);
        Assert.Empty(MediaDimensions(ledger.FilePath));
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

    [Fact]
    public void RectificationRecordRequiresAllIssuesClosedAndPreservesReturnActions()
    {
        using var fixture = new StoreFixture();
        var detail = fixture.Store.CreateInspection(new(fixture.Point.Id, Store.Today, "测试排查人", "issues", Issues:
            [new("问题甲", "清理甲", Store.Today), new("问题乙", "清理乙", Store.Today)]));
        var rejected = Assert.Throws<ApiException>(() => fixture.Store.ExportRectificationWord(detail.Inspection.Id));
        Assert.Equal(400, rejected.StatusCode); Assert.Equal("尚有 2 项问题未闭环", rejected.Message);
        foreach (var issue in detail.Issues)
        {
            fixture.Store.RecordFeedback(issue.Id, new(Store.Today, "首轮反馈"));
            fixture.Store.ReviewAndClose(issue.Id, new(Store.Today, "首轮复查"));
        }
        var id = detail.Issues[0].Id;
        fixture.Store.ReturnIssue(id, new("再次发现堆放"));
        var returned = Assert.Throws<ApiException>(() => fixture.Store.ExportRectificationWord(detail.Inspection.Id));
        Assert.Equal("尚有 1 项问题未闭环", returned.Message);
        fixture.Store.RecordFeedback(id, new(Store.Today, "再次清理"));
        fixture.Store.ReviewAndClose(id, new(Store.Today, "再次复查通过"));
        var document = fixture.Store.ExportRectificationWord(detail.Inspection.Id);
        Assert.StartsWith("整改记录单_", document.FileName, StringComparison.Ordinal);
        var text = DocumentText(document.FilePath);
        foreach (var value in new[] { "问题甲", "问题乙", "办理过程", "首轮反馈", "首轮复查", "再次发现堆放", "再次清理", "再次复查通过" }) Assert.Contains(value, text);
    }

    [Fact]
    public void InspectionLedgerIncludesNormalAndProblemRecordsWithSnapshotNames()
    {
        using var fixture = new StoreFixture();
        _ = fixture.Store.CreateInspection(new(fixture.Point.Id, Store.Today, "测试排查人", "normal"));
        var detail = fixture.CreateIssue();
        fixture.Store.RecordFeedback(detail.Issues[0].Id, new(Store.Today, "反馈完成"));
        fixture.Store.SavePoint(fixture.Point.Id, new("新点位名", fixture.Community.Id, fixture.Point.TypeId, "新地址"));
        var document = fixture.Store.ExportInspectionLedgerWord(Store.Today, Store.Today, fixture.Community.Id);
        var text = DocumentText(document.FilePath);
        Assert.Contains("共 2 次排查，正常 1，发现问题 1；问题共 1 项，已闭环 0", text);
        Assert.Contains("待复查", text);
        Assert.Contains(fixture.Point.Name, text);
        Assert.DoesNotContain("新点位名", text, StringComparison.Ordinal);
        Assert.Empty(MediaDimensions(document.FilePath));
    }

    [Fact]
    public void CampaignExportsUseSnapshotNamesOriginalPhotosAndQuantities()
    {
        using var fixture = new StoreFixture();
        var type = fixture.Store.GetCampaignTypes()[0];
        var before = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(1600, 900), "before");
        var after = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(1200, 800), "after");
        var process = fixture.Store.UploadPhoto(WordExporterTests.Jpeg(900, 1200), "process");
        var request = new CampaignWriteRequest("河道整治", type.Id, fixture.Point.Id, Store.Today, "清理道路垃圾。",
            ParticipantCount: 12, VehicleCount: 1, GarbageTons: 2.35m, AreaSqm: 100.50m, Outcome: "道路已清洁。",
            BeforePhotoIds: [before.Id], ProcessPhotoIds: [process.Id], AfterPhotoIds: [after.Id]);
        var detail = fixture.Store.SaveCampaign(null, request);
        fixture.Store.SaveCommunity(fixture.Community.Id, new("新社区名称", "新社区抬头"));
        fixture.Store.SaveCampaignType(type.Id, new("新整治类型"));
        var record = fixture.Store.ExportCampaignWord(detail.Campaign.Id);
        Assert.StartsWith("整治记录单_", record.FileName, StringComparison.Ordinal);
        Assert.Equal(new[] { "1200x800", "1600x900", "900x1200" }, MediaDimensions(record.FilePath));
        var text = DocumentText(record.FilePath);
        Assert.Contains(fixture.Community.Name, text); Assert.Contains(type.Name, text);
        Assert.DoesNotContain("新社区名称", text, StringComparison.Ordinal); Assert.DoesNotContain("新整治类型", text, StringComparison.Ordinal);
        var ledger = fixture.Store.ExportCampaignLedgerWord(Store.Today, Store.Today, fixture.Community.Id, type.Id, true);
        Assert.Equal(new[] { "1200x800", "1600x900", "900x1200" }, MediaDimensions(ledger.FilePath));
        Assert.Contains("累计参加 12 人、出动车辆 1 辆、清运垃圾 2.35 吨、清理面积 100.50 ㎡", DocumentText(ledger.FilePath));
        var noPhotos = fixture.Store.ExportCampaignLedgerWord(Store.Today, Store.Today, includePhotos: false);
        Assert.Empty(MediaDimensions(noPhotos.FilePath));
        Assert.Equal(400, Assert.Throws<ApiException>(() => fixture.Store.ExportCampaignLedgerWord(Store.Today, Store.Today, typeId: "missing")).StatusCode);
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
            Store = new Store(Path.Combine(Directory, "data"));
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
