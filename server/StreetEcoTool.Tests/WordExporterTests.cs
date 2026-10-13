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
    public void DuplicateProblemBlockIsRejectedWithoutReplacingStoredTemplate()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var original = store.GetSettings();
        var invalid = original with { NoticeTemplate = "正文{问题}再次{问题}" };
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveSettings(invalid)).StatusCode);
        Assert.Equal(original, store.GetSettings());
        using var destination = new MemoryStream();
        Assert.Throws<ApiException>(() => WordExporter.WriteNotice(destination, Inspection(), invalid, []));
        Assert.Equal(0, destination.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SixVariablesAndLegitimateBusinessTextArePreserved(bool explicitBlock)
    {
        var detail = Inspection();
        detail = detail with { Inspection = detail.Inspection with { PointName = "让居民了解{问题}的现场点位", Notes = "请妥善保管上传的现场原文。" } };
        var settings = Settings() with { NoticeTemplate = "{街道全称}：{社区}\r\n经{日期}对{点位}排查，A&B <原文>。\n请于{期限}前妥善办理。" + (explicitBlock ? "\n{问题}" : "") };
        using var notice = new Package(Render(stream => WordExporter.WriteNotice(stream, detail, settings, [])));
        ValidatePackage(notice);
        var text = Text(notice.Xml("word/document.xml"));
        Assert.Contains(settings.StreetName, text);
        Assert.Contains(detail.Inspection.CommunityName, text);
        Assert.Contains(detail.Inspection.PointName, text);
        Assert.Contains("A&B <原文>。", text);
        Assert.Contains("妥善办理。", text);
        foreach (var issue in detail.Issues) Assert.Single(Regex.Matches(text, Regex.Escape(issue.Description)));
        using var record = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, settings, [])));
        Assert.Contains(detail.Inspection.Notes, Text(record.Xml("word/document.xml")));
    }

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
        Assert.DoesNotContain("各项问题请按表列期限分别完成", text, StringComparison.Ordinal);
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
        var table = Assert.Single(document.Descendants(W + "tbl"), item => item.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == 9);
        Assert.Equal(9, table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count());
        var header = table.Elements(W + "tr").First();
        Assert.Equal(new[] { "序号", "排查日期", "点位名称", "所属社区", "问题描述", "整改期限", "状态", "反馈情况", "复查结论" }, header.Elements(W + "tc").Select(Text).ToArray());
        Assert.NotNull(header.Element(W + "trPr")?.Element(W + "tblHeader"));
        Assert.All(table.Descendants(W + "sz"), size => Assert.Contains((string?)size.Attribute(W + "val"), new[] { "21", "24" }));
        var text = Regex.Replace(Text(document), @"\s", "");
        Assert.Contains("共3条，已闭环1，待复查1，待整改1，其中逾期1", text);
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
        Assert.Contains("共0条，已闭环0，待复查0，待整改0，其中逾期0", Regex.Replace(Text(empty.Xml("word/document.xml")), @"\s", ""));
    }

    [Fact]
    public void OversizedLedgerRowsContinueWithTheSameNumberAndPreserveEveryCharacterAndLineBreak()
    {
        var description = string.Concat(Enumerable.Repeat("逐段核验🌿：围挡内堆放材料，需覆盖裸露区域并清运垃圾。\r\n", 150));
        var feedback = "反馈首行\n" + new string('核', 3400) + "\n反馈末行🌿";
        var issue = Issue("long", description, "完整保留整改要求", "2026-10-12", "feedback") with
            { FeedbackDate = "2026-10-11", FeedbackNotes = feedback };
        using var package = new Package(Render(stream => WordExporter.WriteLedger(stream,
            new("2026-10-01", "2026-10-31", "东苑社区", [issue, Issue("short", "普通问题保持一行", "清理", "2026-10-12")], [], false), Settings())));
        ValidatePackage(package);
        var table = Assert.Single(package.Xml("word/document.xml").Descendants(W + "tbl"), item => item.Element(W + "tblGrid")?.Elements().Count() == 9);
        var rows = table.Elements(W + "tr").ToArray();
        var continued = rows.Where(row => row.Elements(W + "tc").Count() == 9 && Text(row.Elements(W + "tc").First()) == "1").ToArray();
        Assert.True(continued.Length > 1);
        Assert.Equal(description.Replace("\r\n", "\n", StringComparison.Ordinal), string.Concat(continued.Select(row => CellLines(row.Elements(W + "tc").ElementAt(4)))));
        Assert.Equal("2026-10-11\n" + feedback, string.Concat(continued.Select(row => CellLines(row.Elements(W + "tc").ElementAt(7)))));
        Assert.All(continued, row =>
        {
            Assert.Equal("1", Text(row.Elements(W + "tc").First()));
            Assert.NotNull(row.Element(W + "trPr")?.Element(W + "cantSplit"));
            Assert.True(CellLines(row.Elements(W + "tc").ElementAt(4)).EnumerateRunes().Count() < 600);
        });
        Assert.Single(rows, row => row.Elements(W + "tc").Count() == 9 && Text(row.Elements(W + "tc").First()) == "2");
        Assert.NotNull(rows[0].Element(W + "trPr")?.Element(W + "tblHeader"));
        Assert.Single(rows[^1].Elements(W + "tc"));
        Assert.Equal("共 2 条，已闭环 0，待复查 1，待整改 1，其中逾期 0", Text(rows[^1]));
    }

    [Fact]
    public void OversizedRecordIssueRowsPreserveDescriptionAndRequirementInIndependentColumns()
    {
        var description = new string('查', 3400) + "🌿末行\n";
        var requirement = "首行\n" + new string('整', 3600) + "\n最后要求";
        var detail = Inspection();
        detail = detail with { Issues = [detail.Issues[0] with { Description = description, Requirement = requirement }] };
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])));
        ValidatePackage(package);
        var table = Assert.Single(package.Xml("word/document.xml").Descendants(W + "tbl"), item => item.Element(W + "tblGrid")?.Elements().Count() == 5);
        var rows = table.Elements(W + "tr").Skip(1).ToArray();
        Assert.True(rows.Length > 1);
        Assert.Equal(description, string.Concat(rows.Select(row => CellLines(row.Elements(W + "tc").ElementAt(1)))));
        Assert.Equal(requirement, string.Concat(rows.Select(row => CellLines(row.Elements(W + "tc").ElementAt(2)))));
        Assert.All(rows, row => Assert.Equal("1", Text(row.Elements(W + "tc").First())));
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
        Assert.Contains("整改前", Text(document));
        Assert.Contains("整改后", Text(document));
        Assert.Contains("复查", Text(document));
        Assert.Single(document.Descendants(W + "pageBreakBefore"));
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

    [Fact]
    public void AllSevenExportsUseTheV2StylesAndCompleteSingleSpacedStructures()
    {
        var detail = ClosedInspection();
        var campaign = Campaign();
        byte[][] documents =
        [
            Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])),
            Render(stream => WordExporter.WriteNotice(stream, detail, Settings(), [])),
            Render(stream => WordExporter.WriteRectificationRecord(stream, detail, Settings(), [])),
            Render(stream => WordExporter.WriteCampaignRecord(stream, new(campaign, []), Settings(), [])),
            Render(stream => WordExporter.WriteLedger(stream, new("2026-10-01", "2026-10-31", "全部社区", detail.Issues, [], false), Settings())),
            Render(stream => WordExporter.WriteInspectionLedger(stream, new("2026-10-01", "2026-10-31", "全部社区", [detail.Inspection], "2026-10-12"), Settings())),
            Render(stream => WordExporter.WriteCampaignLedger(stream, new("2026-10-01", "2026-10-31", "全部社区", [campaign], [], false, "2026-10-12"), Settings()))
        ];
        foreach (var bytes in documents)
        {
            using var package = new Package(bytes);
            ValidatePackage(package);
            var styles = package.Xml("word/styles.xml");
            (string Id, string Font, string Size, bool Bold)[] expected =
            [
                ("Normal", "仿宋", "32", false), ("Title", "方正小标宋简体", "44", false),
                ("Heading1", "黑体", "32", false), ("Heading2", "仿宋", "32", true),
                ("TableHeader", "黑体", "21", false), ("TableText", "仿宋", "21", false), ("Footer", "宋体", "18", false)
            ];
            foreach (var item in expected)
            {
                var style = Assert.Single(styles.Descendants(W + "style"), element => (string?)element.Attribute(W + "styleId") == item.Id);
                var properties = style.Element(W + "rPr")!;
                var font = properties.Element(W + "rFonts")!;
                foreach (var script in new[] { "ascii", "hAnsi", "eastAsia", "cs" }) Assert.Equal(item.Font, (string?)font.Attribute(W + script));
                Assert.Equal("eastAsia", (string?)font.Attribute(W + "hint"));
                Assert.Equal(item.Size, (string?)properties.Element(W + "sz")?.Attribute(W + "val"));
                Assert.Equal(item.Size, (string?)properties.Element(W + "szCs")?.Attribute(W + "val"));
                var bold = properties.Element(W + "b");
                Assert.Equal(item.Bold, bold is not null && (string?)bold.Attribute(W + "val") != "0");
                Assert.NotNull(style.Element(W + "pPr")?.Element(W + "spacing"));
            }
            Assert.Equal("640", (string?)styles.Descendants(W + "style").Single(style => (string?)style.Attribute(W + "styleId") == "Normal").Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "firstLine"));
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            Assert.Equal(Settings().StreetName, package.Xml("docProps/core.xml").Root?.Element(dc + "creator")?.Value);
            var text = Text(package.Xml("word/document.xml"));
            foreach (var prohibited in new[] { "上传", "留痕", "暂无照片", "无。", "附后照片" }) Assert.DoesNotContain(prohibited, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InspectionInfoMergesAddressAndContactAndOmitsEmptyPhotoAndRemarkSections()
    {
        var detail = Inspection();
        detail = detail with { Inspection = detail.Inspection with { Notes = "" } };
        using var package = new Package(Render(stream => WordExporter.WriteRecord(stream, detail, Settings(), [])));
        var document = package.Xml("word/document.xml");
        var table = document.Descendants(W + "tbl").First();
        Assert.Equal(4, table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count());
        var rows = table.Elements(W + "tr").ToArray();
        Assert.Equal(5, rows.Length);
        foreach (var index in new[] { 2, 4 })
        {
            Assert.Equal(2, rows[index].Elements(W + "tc").Count());
            Assert.Equal("3", (string?)rows[index].Elements(W + "tc").Last().Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val"));
        }
        Assert.Contains(detail.Inspection.Contact, Text(rows[4]));
        Assert.Contains(detail.Inspection.Phone, Text(rows[4]));
        Assert.DoesNotContain("现场照片", Text(document), StringComparison.Ordinal);
        Assert.DoesNotContain("备注", Text(document), StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeUsesChineseBodyDatesAndKeepsTheReceiptAsOneGroup()
    {
        using var package = new Package(Render(stream => WordExporter.WriteNotice(stream, Inspection(), Settings(), [])));
        var document = package.Xml("word/document.xml");
        Assert.Contains("2026年10月10日", Text(document));
        Assert.Contains("2026年10月15日", Text(document));
        var bodyParagraphs = document.Root!.Element(W + "body")!.Elements(W + "p").ToArray();
        var recipient = Assert.Single(bodyParagraphs, paragraph => Text(paragraph) == Inspection().Inspection.CommunityTitle + "：");
        Assert.Equal("0", (string?)recipient.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "firstLine"));
        var receipt = Assert.Single(document.Descendants(W + "tbl"), table => Text(table).Contains("负责人签字及日期", StringComparison.Ordinal));
        var rows = receipt.Elements(W + "tr").ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows.Take(2).SelectMany(row => row.Descendants(W + "pPr")), properties => Assert.NotNull(properties.Element(W + "keepNext")));
        Assert.All(rows[^1].Descendants(W + "pPr"), properties => Assert.Null(properties.Element(W + "keepNext")));
        var caption = receipt.ElementsBeforeSelf(W + "p").Last();
        Assert.Equal("整改情况回执", Text(caption));
        var heading = package.Xml("word/styles.xml").Descendants(W + "style").Single(style => (string?)style.Attribute(W + "styleId") == "Heading1");
        Assert.NotNull(heading.Element(W + "pPr")?.Element(W + "keepNext"));
    }

    [Fact]
    public void RectificationRecordRejectsEveryUnclosedIssueAndNormalInspections()
    {
        var pending = Inspection();
        var error = Assert.Throws<ApiException>(() => Render(stream => WordExporter.WriteRectificationRecord(stream, pending, Settings(), [])));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("尚有 2 项问题未闭环", error.Message);
        var normal = pending with { Issues = [] };
        Assert.Equal(400, Assert.Throws<ApiException>(() => Render(stream => WordExporter.WriteRectificationRecord(stream, normal, Settings(), []))).StatusCode);
    }

    [Fact]
    public void RectificationRecordIncludesLatestDatesAllPhasesAndReturnedHistoryWithoutPhotoCaptions()
    {
        using var files = new TemporaryFiles();
        var detail = ClosedInspection();
        var issue = detail.Issues[0];
        IssueActionDto[] actions =
        [
            new("action-1", "feedback", "2026-10-10", "首轮反馈文本", []),
            new("action-2", "review", "2026-10-10", "首轮复查文本", []),
            new("action-3", "return", "2026-10-11", "退回原因文本", []),
            new("action-4", "feedback", "2026-10-12", "第二轮反馈文本", []),
            new("action-5", "review", "2026-10-13", "第二轮复查文本", [])
        ];
        detail = detail with { IssueDetails = [new(issue, detail.Inspection, [], actions)] };
        WordPhoto[] photos = [files.Photo('a', "site", 640, 480, issue.Id), files.Photo('b', "after", 600, 800, issue.Id), files.Photo('c', "review", 800, 600, issue.Id), files.Photo('d', "site", 700, 500, detail.Issues[1].Id)];
        using var package = new Package(Render(stream => WordExporter.WriteRectificationRecord(stream, detail, Settings(), photos)));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        var text = Text(document);
        Assert.Contains("整改完成日期2026-10-14", text);
        Assert.Contains("2026年10月14日", text);
        Assert.Contains("办理过程", text);
        foreach (var action in actions) Assert.Contains(action.Notes, text);
        Assert.Equal(4, package.Media.Length);
        Assert.Equal(4, document.Descendants(W + "drawing").Count());
        var imageRows = document.Descendants(W + "tr").Where(row => row.Descendants(W + "drawing").Any()).ToArray();
        Assert.All(imageRows, row => Assert.Equal("", Text(row)));
        var oneSided = Assert.Single(imageRows, row => row.Descendants(W + "drawing").Count() == 1 && row.Descendants(A + "blip").Any(blip => (string?)blip.Attribute(R + "embed") == "rPhoto4"));
        Assert.Equal("", Text(oneSided.Elements(W + "tc").Last()));
    }

    [Fact]
    public void CampaignRecordKeepsFullContentAndSparseQuantitiesIncludingZero()
    {
        var campaign = Campaign() with { Content = new string('清', 2000), Outcome = "", Notes = "", ParticipantCount = 0, VehicleCount = null, GarbageTons = null, AreaSqm = null };
        using var package = new Package(Render(stream => WordExporter.WriteCampaignRecord(stream, new(campaign, []), Settings(), [])));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        Assert.Contains(campaign.Content, Text(document));
        var quantities = Assert.Single(document.Descendants(W + "tbl"), table => Text(table).Contains("参加人数（人）", StringComparison.Ordinal));
        Assert.Equal(1, quantities.Element(W + "tblGrid")?.Elements(W + "gridCol").Count());
        Assert.Equal("0", Text(quantities.Elements(W + "tr").Last()));
        Assert.DoesNotContain("整治照片", Text(document), StringComparison.Ordinal);
        Assert.DoesNotContain("备注", Text(document), StringComparison.Ordinal);
    }

    [Fact]
    public void InspectionAndCampaignLedgersHaveMergedLastSummaryRowsAndExactTotals()
    {
        var detail = ClosedInspection();
        var normal = detail.Inspection with { Id = "normal", Result = "normal", IssueCount = 0, ClosedCount = 0, OpenIssueCount = 0, RectifyStatus = "none" };
        using (var package = new Package(Render(stream => WordExporter.WriteInspectionLedger(stream,
            new("2026-10-01", "2026-10-31", "全部社区", [detail.Inspection, normal], "2026-10-12"), Settings()))))
        {
            ValidatePackage(package);
            var main = Assert.Single(package.Xml("word/document.xml").Descendants(W + "tbl"), table => table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == 10);
            var summary = Assert.Single(main.Elements(W + "tr").Last().Elements(W + "tc"));
            Assert.Equal("10", (string?)summary.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val"));
            Assert.Contains("共 2 次排查，正常 1，发现问题 1；问题共 2 项，已闭环 2", Text(summary));
            Assert.NotNull(summary.Descendants(W + "b").FirstOrDefault());
        }
        var first = Campaign() with { Content = new string('清', 100), Outcome = "完成。剩余文字不显示。", GarbageTons = 1.25m, AreaSqm = 25.35m };
        var second = first with { Id = "campaign-2", Name = "第二场", ParticipantCount = null, VehicleCount = 2, GarbageTons = 0.10m, AreaSqm = null };
        using var campaigns = new Package(Render(stream => WordExporter.WriteCampaignLedger(stream,
            new("2026-10-01", "2026-10-31", "全部社区", [first, second], [], false, "2026-10-12"), Settings())));
        ValidatePackage(campaigns);
        var table = Assert.Single(campaigns.Xml("word/document.xml").Descendants(W + "tbl"), item => item.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() == 11);
        var cells = table.Elements(W + "tr").Skip(1).First().Elements(W + "tc").ToArray();
        Assert.Equal(new string('清', 60) + "…", Text(cells[^1]));
        Assert.Equal("right", (string?)cells[8].Element(W + "p")?.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val"));
        var merged = Assert.Single(table.Elements(W + "tr").Last().Elements(W + "tc"));
        Assert.Equal("11", (string?)merged.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val"));
        Assert.Contains("共 2 次整治，累计参加 12 人、出动车辆 3 辆、清运垃圾 1.35 吨、清理面积 25.35 ㎡", Text(merged));
    }

    [Fact]
    public void CampaignLedgerPhotoOptionUsesAllThreePhasesAndOriginalJpegFiles()
    {
        using var files = new TemporaryFiles();
        var campaign = Campaign();
        WordPhoto[] photos = [files.Photo('a', "before", 1600, 900), files.Photo('b', "after", 900, 1600), files.Photo('c', "process", 800, 600)];
        photos = photos.Select(photo => photo with { Photo = photo.Photo with { CampaignId = campaign.Id, HasThumb = true } }).ToArray();
        using var package = new Package(Render(stream => WordExporter.WriteCampaignLedger(stream,
            new("2026-10-01", "2026-10-31", "全部社区", [campaign], photos, true, "2026-10-12"), Settings())));
        ValidatePackage(package);
        var document = package.Xml("word/document.xml");
        Assert.Equal(3, package.Media.Length);
        Assert.Contains("整治前", Text(document)); Assert.Contains("整治后", Text(document)); Assert.Contains("整治中", Text(document));
        Assert.Single(document.Descendants(W + "pageBreakBefore"));
        foreach (var photo in photos) Assert.Contains(package.Media, media => package.Bytes(media).SequenceEqual(File.ReadAllBytes(photo.FilePath)));
    }

    private static InspectionDetailDto ClosedInspection()
    {
        var detail = Inspection();
        var issues = detail.Issues.Select((issue, index) => issue with { Status = "closed", FeedbackDate = "2026-10-12", FeedbackNotes = "完成清理", ReviewDate = index == 0 ? "2026-10-13" : "2026-10-14", ReviewConclusion = "复查通过", ClosedDate = index == 0 ? "2026-10-13" : "2026-10-14", Overdue = false }).ToArray();
        return detail with { Issues = issues, Inspection = detail.Inspection with { OpenIssueCount = 0, ClosedCount = 2, RectifyStatus = "closed" } };
    }

    private static CampaignDto Campaign() => new("campaign-1", "沿河环境整治", "campaign-type-1", "河道清理", "community-1", "东苑社区", "河道沿岸", "2026-10-10", "2026-10-12", "示例街道办事处", "社区工作人员", 12, 1, 1.20m, 240.50m, "清理河道沿岸垃圾。", "河道沿岸清洁。", "现场备注", 0);

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
    private static string CellLines(XContainer element) => string.Concat(element.Descendants()
        .Where(node => node.Name == W + "t" || node.Name == W + "br").Select(node => node.Name == W + "br" ? "\n" : node.Value));
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
        foreach (var part in names.Where(name => name.EndsWith(".xml", StringComparison.Ordinal)))
        {
            var xml = package.Xml(part);
            Assert.All(xml.Descendants(W + "spacing"), spacing =>
            {
                Assert.Equal("240", (string?)spacing.Attribute(W + "line"));
                Assert.Equal("auto", (string?)spacing.Attribute(W + "lineRule"));
            });
            Assert.All(xml.Descendants(W + "rFonts"), font =>
            {
                Assert.Equal("eastAsia", (string?)font.Attribute(W + "hint"));
                Assert.All(new[] { "ascii", "hAnsi", "cs" }, script => Assert.Equal((string?)font.Attribute(W + "eastAsia"), (string?)font.Attribute(W + script)));
            });
        }
        Assert.Empty(package.Xml("word/settings.xml").Descendants(W + "updateFields"));
        var document = package.Xml("word/document.xml");
        Assert.Equal(W + "document", document.Root?.Name);
        Assert.All(document.Descendants(W + "p"), paragraph => Assert.NotNull(paragraph.Element(W + "pPr")?.Element(W + "spacing")));
        Assert.All(document.Descendants(W + "tr"), row => Assert.NotNull(row.Element(W + "trPr")?.Element(W + "cantSplit")));
        foreach (var table in document.Descendants(W + "tbl"))
        {
            var grid = table.Element(W + "tblGrid");
            Assert.NotNull(grid);
            var columns = grid.Elements(W + "gridCol").Count();
            Assert.True(columns > 0);
            foreach (var row in table.Elements(W + "tr"))
                Assert.Equal(columns, row.Elements(W + "tc").Sum(cell => int.Parse((string?)cell.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val") ?? "1", CultureInfo.InvariantCulture)));
            foreach (var border in table.Element(W + "tblPr")!.Element(W + "tblBorders")!.Elements())
            { Assert.Equal("4", (string?)border.Attribute(W + "sz")); Assert.Equal("000000", (string?)border.Attribute(W + "color")); }
            var margins = table.Element(W + "tblPr")!.Element(W + "tblCellMar")!;
            foreach (var edge in new[] { "top", "bottom" }) Assert.Equal("50", (string?)margins.Element(W + edge)?.Attribute(W + "w"));
            foreach (var edge in new[] { "left", "right" }) Assert.Equal("100", (string?)margins.Element(W + edge)?.Attribute(W + "w"));
            foreach (var header in table.Elements(W + "tr").Where(row => row.Element(W + "trPr")?.Element(W + "tblHeader") is not null))
                Assert.All(header.Elements(W + "tc"), cell => Assert.Equal("F2F2F2", (string?)cell.Element(W + "tcPr")?.Element(W + "shd")?.Attribute(W + "fill")));
        }
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
