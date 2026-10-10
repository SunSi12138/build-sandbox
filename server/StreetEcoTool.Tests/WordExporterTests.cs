using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class WordExporterTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";

    [Fact]
    public void RecordIsACompletePackageWithEscapedTextAllIssuesAndRemarks()
    {
        var detail = Inspection();
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        var text = Text(document);
        Assert.Contains(detail.Inspection.PointName, text);
        Assert.Contains(detail.Inspection.CommunityName, text);
        Assert.Contains(detail.Inspection.Address, text);
        Assert.Contains(detail.Inspection.Inspector, text);
        Assert.Contains(detail.Inspection.Notes, text);
        foreach (var issue in detail.Issues)
        {
            Assert.Contains(issue.Description, text);
            Assert.Contains(issue.Requirement, text);
            Assert.Contains(issue.Deadline, text);
            Assert.Contains(issue.ResponsibleUnit, text);
        }
        Assert.Contains("发现问题", text);
        Assert.Empty(package.Media);
    }

    [Fact]
    public void NormalRecordDoesNotRequireAnyIssueOrPhoto()
    {
        var detail = Inspection() with { Issues = [], Photos = [] };
        detail = detail with { Inspection = detail.Inspection with { Result = "normal", IssueCount = 0, OpenIssueCount = 0 } };
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])));
        ValidatePackage(package);
        Assert.Contains("正常", Text(package.Xml("word/document.xml")));
        Assert.Empty(package.Media);
        Assert.Empty(package.Xml("word/document.xml").Descendants(W + "drawing"));
    }

    [Fact]
    public void PortraitRecordUsesRequestedFontsSizesBordersAndPageNumberField()
    {
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, Inspection(), Settings(), [])));
        var document = package.Xml("word/document.xml");
        var styles = package.Xml("word/styles.xml");
        var page = Assert.Single(document.Descendants(W + "pgSz"));
        Assert.InRange(Number(page, W + "w"), 11890, 11920);
        Assert.InRange(Number(page, W + "h"), 16820, 16855);
        Assert.NotEqual("landscape", (string?)page.Attribute(W + "orient"));
        var fonts = styles.Descendants(W + "rFonts").SelectMany(element => element.Attributes().Select(attribute => attribute.Value)).ToArray();
        Assert.Contains("方正小标宋简体", fonts);
        Assert.Contains("仿宋", fonts);
        Assert.Contains("宋体", fonts);
        var fontTable = package.Xml("word/fontTable.xml");
        var titleFont = Assert.Single(fontTable.Descendants(W + "font"), font => (string?)font.Attribute(W + "name") == "方正小标宋简体");
        Assert.Equal("宋体", (string?)titleFont.Element(W + "altName")?.Attribute(W + "val"));
        Assert.Contains(styles.Descendants(W + "sz"), size => (string?)size.Attribute(W + "val") == "44");
        Assert.Contains(styles.Descendants(W + "sz"), size => (string?)size.Attribute(W + "val") == "32");
        var title = document.Descendants(W + "p").First(paragraph => Text(paragraph).Contains("排查记录单", StringComparison.Ordinal));
        Assert.Equal("center", Alignment(title, styles));
        var borders = document.Descendants(W + "tblBorders").First();
        foreach (var edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            Assert.Equal("single", (string?)borders.Element(W + edge)?.Attribute(W + "val"));
        var footer = DocumentRelationships(package).Single(relationship => relationship.Value.Kind.EndsWith("/footer", StringComparison.Ordinal)).Value.Target;
        var footerXml = package.Xml(footer);
        var instructions = string.Join(" ", footerXml.Descendants(W + "instrText").Select(element => element.Value)
            .Concat(footerXml.Descendants(W + "fldSimple").Select(element => (string?)element.Attribute(W + "instr") ?? "")));
        Assert.Contains("PAGE", instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoticeNumberIsOptionalAndAlignedRightWhenPresent(bool numbered)
    {
        var detail = Inspection();
        detail = detail with { Inspection = detail.Inspection with { NoticeNumber = numbered ? "环保〔2026〕手填 8 号" : "" } };
        using var package = new Package(Render(stream => WordExporter.WriteNotice(stream, detail, Settings(), [])));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        var numberParagraphs = document.Descendants(W + "p").Where(paragraph => Text(paragraph).Contains("编号：", StringComparison.Ordinal)).ToArray();
        if (numbered)
        {
            var number = Assert.Single(numberParagraphs);
            Assert.Contains(detail.Inspection.NoticeNumber, Text(number));
            Assert.Equal("right", Alignment(number, package.Xml("word/styles.xml")));
        }
        else Assert.Empty(numberParagraphs);
        var text = Text(document);
        Assert.Contains(detail.Inspection.CommunityTitle + "：", text);
        Assert.Contains("整改情况", text);
        Assert.Contains("负责人签字", text);
        Assert.Contains("盖章", text);
        Assert.Contains(Settings().IssuingUnit, text);
    }

    [Theory]
    [InlineData("经{日期}对{点位}排查。<原样> & 引号\"测试\"。{问题}请于{期限}前完成。")]
    [InlineData("<原样> & 引号\"测试\"。请按要求整改。")]
    [InlineData("<原样> & 引号\"测试\"。{问题}再次说明：{问题}请于{期限}前完成。")]
    public void NoticeTemplateAlwaysIncludesEveryIssueExactlyOnceWithoutXmlInjection(string template)
    {
        var detail = Inspection();
        using var package = new Package(Render(stream => WordExporter.WriteNotice(stream, detail, Settings() with { NoticeTemplate = template }, [])));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        var text = Text(document);
        Assert.Contains("<原样> & 引号\"测试\"。", text);
        Assert.Empty(document.Descendants("原样"));
        foreach (var issue in detail.Issues)
            Assert.Single(Regex.Matches(text, Regex.Escape(issue.Description)));
        foreach (var placeholder in new[] { "{日期}", "{点位}", "{问题}", "{期限}" }) Assert.DoesNotContain(placeholder, text);
        Assert.Contains(detail.Issues.Select(issue => issue.Deadline).Order(StringComparer.Ordinal).Last(), text);
        Assert.Contains("各项问题请按表列期限分别完成", text);
    }

    [Fact]
    public void PhotosAreTwoPerRowKeepSofAspectRatioAndNeverEscapePageMargins()
    {
        using var files = new TemporaryFiles();
        var detail = Inspection();
        WordPhoto[] photos = [files.Photo('a', "site", 1600, 900, detail.Issues[0].Id), files.Photo('b', "site", 900, 1600, detail.Issues[1].Id), files.Photo('c', "site", 700, 700, detail.Issues[0].Id)];
        // Deliberately wrong cached dimensions: the file's SOF is the authority.
        photos[1] = photos[1] with { Photo = photos[1].Photo with { Width = 50, Height = 20 } };
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), photos)));
        ValidatePackage(package);
        Assert.Equal(3, package.Media.Length);
        var document = package.Xml("word/document.xml");
        var drawings = document.Descendants(W + "drawing").ToArray();
        Assert.Equal(3, drawings.Length);
        var imageRows = document.Descendants(W + "tr").Where(row => row.Descendants(W + "drawing").Any()).ToArray();
        Assert.Contains(imageRows, row => row.Descendants(W + "drawing").Count() == 2);
        Assert.All(imageRows, row => { Assert.Equal(2, row.Elements(W + "tc").Count()); Assert.InRange(row.Descendants(W + "drawing").Count(), 1, 2); });
        var page = Assert.Single(document.Descendants(W + "pgSz"));
        var margin = Assert.Single(document.Descendants(W + "pgMar"));
        var halfPageEmu = (Number(page, W + "w") - Number(margin, W + "left") - Number(margin, W + "right")) * 635L / 2;
        var relationships = DocumentRelationships(package);
        foreach (var drawing in drawings)
        {
            var extent = Assert.Single(drawing.Descendants(Wp + "extent"));
            var cx = Number(extent, "cx");
            var cy = Number(extent, "cy");
            Assert.InRange(cx, 1, halfPageEmu);
            Assert.True(cy > 0);
            var image = Assert.Single(drawing.Descendants(A + "blip"));
            var id = (string?)image.Attribute(R + "embed") ?? throw new InvalidDataException("Image relationship missing.");
            var dimensions = JpegMetadata.Read(package.Bytes(relationships[id].Target));
            Assert.InRange(Math.Abs(cx / (double)cy - dimensions.Width / (double)dimensions.Height), 0, 0.005);
        }
        var ids = document.Descendants(Wp + "docPr").Select(element => (string?)element.Attribute("id")).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("0", ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordAndNoticeIncludeOnlySitePhotos(bool notice)
    {
        using var files = new TemporaryFiles();
        var detail = Inspection();
        WordPhoto[] photos = [files.Photo('a', "site", 640, 480, detail.Issues[0].Id), files.Photo('b', "after", 800, 600, detail.Issues[0].Id), files.Photo('c', "review", 1200, 900, detail.Issues[0].Id)];
        using var package = new Package(Render(stream =>
        {
            if (notice) WordExporter.WriteNotice(stream, detail, Settings(), photos);
            else WordExporter.WriteRecord(stream, detail, Settings(), photos);
        }));
        var media = Assert.Single(package.Media);
        Assert.Equal(new JpegDimensions(640, 480), JpegMetadata.Read(package.Bytes(media)));
        Assert.Single(package.Xml("word/document.xml").Descendants(W + "drawing"));
    }

    [Fact]
    public void LedgerIsLandscapeWithNineColumnsFivePointTableFontAndAccurateSummary()
    {
        var issues = LedgerIssues();
        var ledger = new WordLedger("2026-10-01", "2026-10-31", "东苑社区", issues, [], false);
        using var package = new Package(Render(stream => WordExporter.WriteLedger(stream, ledger, Settings())));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        var page = Assert.Single(document.Descendants(W + "pgSz"));
        Assert.Equal("landscape", (string?)page.Attribute(W + "orient"));
        Assert.InRange(Number(page, W + "w"), 16820, 16855);
        Assert.InRange(Number(page, W + "h"), 11890, 11920);
        var table = document.Descendants(W + "tbl").First();
        Assert.Equal(9, table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count());
        var header = table.Elements(W + "tr").First();
        Assert.Equal(new[] { "序号", "排查日期", "点位", "社区", "问题描述", "整改期限", "状态", "反馈情况", "复查结论" }, header.Elements(W + "tc").Select(Text).ToArray());
        Assert.NotNull(header.Element(W + "trPr")?.Element(W + "tblHeader"));
        Assert.All(table.Descendants(W + "sz"), size => Assert.Contains((string?)size.Attribute(W + "val"), new[] { "21", "24" }));
        var text = Regex.Replace(Text(document), @"\s", "");
        Assert.Contains("共3条，已闭环1，未闭环2，逾期1", text);
        Assert.Contains("已清理，等待复查", text);
        Assert.Contains("整改到位，予以闭环", text);
        Assert.Empty(package.Media);
    }

    [Fact]
    public void LedgerWithoutPhotosNeverOpensPhotoFilesAndEmptyLedgerStillExports()
    {
        var photo = new WordPhoto(new PhotoDto(new string('a', 32), 1, 1, "site", "/photos/unused", "2026-10-10T10:00:00", IssueId: "issue-1"), "/this-file-must-not-be-opened.jpg");
        using (var package = new Package(Render(stream => WordExporter.WriteLedger(stream, new("2026-10-01", "2026-10-31", "全部社区", LedgerIssues(), [photo], false), Settings()))))
        {
            ValidatePackage(package);
            Assert.Empty(package.Media);
        }
        using var empty = new Package(Render(stream => WordExporter.WriteLedger(stream, new("2026-10-01", "2026-10-31", "全部社区", [], [], true), Settings())));
        ValidatePackage(empty);
        Assert.Contains("共0条，已闭环0，未闭环0，逾期0", Regex.Replace(Text(empty.Xml("word/document.xml")), @"\s", ""));
    }

    [Fact]
    public void LedgerPhotoAppendixUsesSharedSitePhotosPerIssueAndIncludesFeedbackAndReview()
    {
        using var files = new TemporaryFiles();
        var issues = Inspection().Issues;
        var shared = files.Photo('a', "site", 640, 480, null, "inspection-1");
        WordPhoto[] photos = [shared, files.Photo('b', "after", 800, 600, issues[0].Id), files.Photo('c', "review", 900, 1200, issues[0].Id)];
        using var package = new Package(Render(stream => WordExporter.WriteLedger(stream, new("2026-10-01", "2026-10-31", "东苑社区", issues, photos, true), Settings())));
        ValidatePackage(package);
        Assert.Equal(3, package.Media.Length); // Shared photo has one media part even though displayed twice.
        var document = package.Xml("word/document.xml");
        Assert.Equal(4, document.Descendants(W + "drawing").Count());
        Assert.Contains("现场", Text(document));
        Assert.Contains("整改后", Text(document));
        Assert.Contains("复查", Text(document));
        Assert.True(document.Descendants(W + "pageBreakBefore").Count() >= issues.Length);
    }

    [Fact]
    public void InvalidXmlCharactersAreRemovedWhileSupplementaryUnicodeSurvives()
    {
        var detail = Inspection();
        detail = detail with { Inspection = detail.Inspection with { Notes = "现场已清理🌿\u0001\ud800继续巡查" } };
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])));
        ValidatePackage(package);
        Assert.Contains("现场已清理🌿继续巡查", Text(package.Xml("word/document.xml")));
    }

    [Fact]
    public void FileNamesPreserveChineseDateAndAreSafeOnBothOperatingSystems()
    {
        var name = WordExporter.FileName("整改通知单", "河道/水体:*?\"<>|\\\r\n..", "2026-10-10");
        Assert.StartsWith("整改通知单_", name, StringComparison.Ordinal);
        Assert.EndsWith("_20261010.docx", name, StringComparison.Ordinal);
        foreach (var invalid in "<>:\"/\\|?*\r\n") Assert.DoesNotContain(invalid.ToString(), name, StringComparison.Ordinal);
        Assert.Equal(name, Path.GetFileName(name));
        var longName = WordExporter.FileName("整改通知单", new string('街', 300), "2026-10-10");
        Assert.InRange(Encoding.UTF8.GetByteCount(longName), 1, 255);
        Assert.EndsWith("_20261010.docx", longName, StringComparison.Ordinal);
    }

    private static SettingsDto Settings() => new("示例街道办事处", "示例街道办事处", "李巡查", "经我街道于{日期}对你社区辖区内{点位}开展环保排查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并书面反馈。", 75);

    private static InspectionDetailDto Inspection()
    {
        var inspection = new InspectionDto("inspection-1", "point-1", "河岸 <一号> & 餐饮点", "community-1", "东苑社区", "东苑社区居民委员会", "type-1", "餐饮油烟", "建设路 12 号 & 沿河一侧", "张负责人", "13800000000", "2026-10-10", "李巡查", "issues", "备注 <原样> & 不应成为 XML 节点。", "手填-8", 2, 2);
        IssueDto[] issues = [Issue("issue-1", "裸土覆盖不到位 <需整改> & 扬尘明显。", "及时覆盖裸土，保持湿润。", "2026-10-12"), Issue("issue-2", "垃圾桶周边散落杂物。", "清扫收集点并安排保洁。", "2026-10-15")];
        return new(inspection, issues, []);
    }

    private static IssueDto Issue(string id, string description, string requirement, string deadline, string status = "pending", bool overdue = false) =>
        new(id, "inspection-1", "point-1", "河岸 <一号> & 餐饮点", "community-1", "东苑社区", "2026-10-10", description, requirement, deadline, "东苑社区居民委员会", status, null, "", null, "", null, overdue);

    private static IssueDto[] LedgerIssues() =>
    [
        Issue("issue-1", "逾期问题", "立即整改", "2026-10-08", overdue: true),
        Issue("issue-2", "等待复查的问题", "按期整改", "2026-10-13", "feedback") with { FeedbackDate = "2026-10-10", FeedbackNotes = "已清理，等待复查" },
        Issue("issue-3", "已经闭环的问题", "已完成", "2026-10-09", "closed", true) with { FeedbackDate = "2026-10-09", FeedbackNotes = "已完成整改", ReviewDate = "2026-10-10", ReviewConclusion = "整改到位，予以闭环", ClosedDate = "2026-10-10" }
    ];

    private static byte[] Render(Action<Stream> write)
    {
        using var stream = new MemoryStream();
        write(stream);
        Assert.True(stream.CanWrite, "Exporter must leave the caller-owned stream open.");
        return stream.ToArray();
    }

    private static string Text(XContainer element) => string.Concat(element.Descendants(W + "t").Select(text => text.Value));
    private static long Number(XElement element, XName attribute) => long.Parse((string?)element.Attribute(attribute) ?? throw new InvalidDataException("Required numeric attribute missing."), CultureInfo.InvariantCulture);
    private static string Alignment(XElement paragraph, XDocument styles)
    {
        var direct = (string?)paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val");
        if (direct is not null) return direct;
        var id = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (id is not null && visited.Add(id))
        {
            var style = styles.Descendants(W + "style").SingleOrDefault(item => (string?)item.Attribute(W + "styleId") == id);
            var align = (string?)style?.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val");
            if (align is not null) return align;
            id = (string?)style?.Element(W + "basedOn")?.Attribute(W + "val");
        }
        return "left";
    }

    private static Dictionary<string, (string Kind, string Target)> DocumentRelationships(Package package) => package.Xml("word/_rels/document.xml.rels")
        .Descendants(Rel + "Relationship").ToDictionary(element => (string)element.Attribute("Id")!, element => ((string)element.Attribute("Type")!, Resolve("word/document.xml", (string)element.Attribute("Target")!)), StringComparer.Ordinal);

    private static string Resolve(string source, string target) => Uri.UnescapeDataString(new Uri(new Uri("https://package.invalid/" + source), target).AbsolutePath.TrimStart('/'));

    private static void ValidatePackage(Package package)
    {
        var names = package.Archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var required in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels" }) Assert.Contains(required, names);
        foreach (var name in names.Where(name => name.EndsWith(".xml", StringComparison.Ordinal) || name.EndsWith(".rels", StringComparison.Ordinal))) _ = package.Xml(name);
        var contentTypes = package.Xml("[Content_Types].xml");
        Assert.Equal(Ct + "Types", contentTypes.Root?.Name);
        foreach (var name in names.Where(name => !name.EndsWith('/')))
        {
            var extension = Path.GetExtension(name).TrimStart('.');
            Assert.True(name == "[Content_Types].xml" || contentTypes.Descendants(Ct + "Override").Any(item => (string?)item.Attribute("PartName") == "/" + name)
                || contentTypes.Descendants(Ct + "Default").Any(item => (string?)item.Attribute("Extension") == extension), $"Missing content type: {name}");
        }
        foreach (var name in names.Where(name => name.EndsWith(".rels", StringComparison.Ordinal)))
        {
            var relations = package.Xml(name).Descendants(Rel + "Relationship").ToArray();
            Assert.Equal(relations.Length, relations.Select(relation => (string?)relation.Attribute("Id")).Distinct(StringComparer.Ordinal).Count());
            var source = name == "_rels/.rels" ? "" : name.Replace("/_rels/", "/", StringComparison.Ordinal)[..^5];
            foreach (var relation in relations)
            {
                Assert.NotEqual("External", (string?)relation.Attribute("TargetMode"));
                var target = Resolve(source, (string?)relation.Attribute("Target") ?? "");
                Assert.Contains(target, names);
            }
        }
        var document = package.Xml("word/document.xml");
        Assert.Equal(W + "document", document.Root?.Name);
        var relationships = DocumentRelationships(package);
        foreach (var reference in document.Descendants().Attributes().Where(attribute => attribute.Name.Namespace == R)) Assert.Contains(reference.Value, relationships.Keys);
        var body = document.Root?.Element(W + "body") ?? throw new InvalidDataException("Word body missing.");
        Assert.Single(body.Elements(W + "sectPr"));
        Assert.Equal(W + "sectPr", body.Elements().Last().Name);
        Assert.All(document.Descendants(W + "tc"), cell => Assert.Equal(W + "p", cell.Elements().Last().Name));
        foreach (var media in package.Media)
        {
            Assert.Contains(contentTypes.Descendants(Ct + "Default"), item => (string?)item.Attribute("Extension") == Path.GetExtension(media).TrimStart('.') && (string?)item.Attribute("ContentType") == "image/jpeg");
            _ = JpegMetadata.Read(package.Bytes(media));
        }
    }

    private sealed class Package : IDisposable
    {
        private readonly MemoryStream _stream;
        public ZipArchive Archive { get; }
        public string[] Media => Archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)).Select(entry => entry.FullName).ToArray();
        public Package(byte[] bytes) { _stream = new MemoryStream(bytes); Archive = new ZipArchive(_stream, ZipArchiveMode.Read); }
        public XDocument Xml(string name) { using var input = Entry(name).Open(); return XDocument.Load(input); }
        public byte[] Bytes(string name) { using var input = Entry(name).Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
        private ZipArchiveEntry Entry(string name) => Archive.GetEntry(name) ?? throw new InvalidDataException("Missing package part: " + name);
        public void Dispose() { Archive.Dispose(); _stream.Dispose(); }
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "StreetEcoWordTests-" + Guid.NewGuid().ToString("N"));
        public TemporaryFiles() => Directory.CreateDirectory(_directory);
        public WordPhoto Photo(char idCharacter, string phase, int width, int height, string? issueId = null, string? inspectionId = null)
        {
            var id = new string(idCharacter, 32);
            var path = Path.Combine(_directory, id + ".jpg");
            File.WriteAllBytes(path, Jpeg(width, height));
            return new(new PhotoDto(id, width, height, phase, "/photos/" + id, "2026-10-10T10:30:00", inspectionId, issueId), path);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }

    internal static byte[] Jpeg(int width, int height) =>
    [
        0xff, 0xd8, 0xff, 0xc0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0,
        0xff, 0xda, 0, 8, 1, 1, 0, 0, 63, 0, 0x11, 0xff, 0, 0x22, 0xff, 0xd9
    ];
}
