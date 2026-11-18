using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;
public sealed class CommunityReportingTests
{
    [Fact]
    public void PrintableReportNumbersRowsWithoutChangingExcelSourceAndPreservesLongContent()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var description = string.Concat(Enumerable.Repeat("楼道内堆放纸箱，请清理并保持出口畅通。", 30));
        var report = new ReportDto("问题整改台账", "全部网格", ["编号", "场所", "问题", "状态"], [["a0123456789012345678901234567890b", "幸福路18号", description, "待整改"]]);
        using var stream = new MemoryStream();
        WordExporter.WriteCommunityReport(stream, report, store.GetSettings()); stream.Position = 0;
        using var zip = new ZipArchive(stream); using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var document = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var rows = document.Descendants(w + "tr").ToArray();
        Assert.Equal("序号", string.Concat(rows[0].Descendants(w + "tc").First().Descendants(w + "t").Select(x => x.Value)));
        Assert.Equal(description, string.Concat(rows.Skip(1).SelectMany(r => r.Elements(w + "tc").ElementAt(2).Descendants(w + "t")).Select(x => x.Value)));
        var widths = document.Descendants(w + "gridCol").Select(x => int.Parse(x.Attribute(w + "w")!.Value)).ToArray();
        Assert.True(widths[2] > widths[0] * 3);
        Assert.Equal("编号", report.Headers[0]); Assert.StartsWith("a012345", report.Rows[0][0]);
        Assert.All(rows.Skip(1), r => Assert.Equal("1", string.Concat(r.Elements(w + "tc").First().Descendants(w + "t").Select(x => x.Value))));
    }
    private static JsonElement Data(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    [Fact]
    public void RemindersRespectTypeCompletionAndVoidsAndSelectedExportsStayScoped()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var type = store.SaveCommunityRecord("inspection-type", null, new("", "每月安全检查", Data("""{"frequency":"monthly","items":[]}""")));
        Assert.Contains(store.GetTypeReminders(), x => x.PointId == point.Id && x.TypeId == type.Id);
        var incomplete = store.CreateInspection(new(point.Id, Store.Today, "张三", "incomplete", InspectionTypeId: type.Id, IncompleteReason: "未开门"));
        Assert.Contains(store.GetTypeReminders(), x => x.PointId == point.Id && x.TypeId == type.Id);
        var normal = store.CreateInspection(new(point.Id, Store.Today, "张三", "normal", InspectionTypeId: type.Id));
        Assert.DoesNotContain(store.GetTypeReminders(), x => x.PointId == point.Id && x.TypeId == type.Id);
        var summary = store.GetStatistics(Store.Today, Store.Today, point.CommunityId);
        Assert.Equal(2, summary.Total.Inspections); Assert.Equal(1, summary.Total.Incomplete); Assert.Equal(1, summary.Total.CheckedPlaces);
        var selected = store.GetReport(new("inspections", Ids: [incomplete.Inspection.Id]));
        Assert.Single(selected.Rows); Assert.Equal(incomplete.Inspection.Id, selected.Rows[0][0]);
        store.DeleteInspection(normal.Inspection.Id);
        Assert.Contains(store.GetTypeReminders(), x => x.PointId == point.Id && x.TypeId == type.Id);
        Assert.Equal(0, store.GetStatistics(Store.Today, Store.Today, "").Total.CheckedPlaces);
    }
    [Fact]
    public void UseFieldImagesAndCampaignLicenseKeepTheirBusinessRoles()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var point = env.CreatePoint(store);
        var purpose = store.SaveCommunityRecord("purpose-type", null, new("", "证照使用",
            Data("""{"icon":"店","fields":[{"id":"permit","label":"营业许可证","type":"image","required":false}]}""")));
        var permit = store.UploadPhoto(WordExporterTests.Jpeg(80, 90), "point");
        var site = store.UploadPhoto(WordExporterTests.Jpeg(80, 90), "point");
        store.SaveCommunityRecord("use", null, new(point.Id, "持证商户",
            Data(JsonSerializer.Serialize(new { purposeId = purpose.Id, position = "一楼", state = "使用中",
                photoIds = new[] { permit.Id, site.Id }, values = new { permit = permit.Id } }))));
        var report = store.GetReport(new("uses"));
        Assert.Contains("营业许可证：已附图片", Assert.Single(report.Rows)[5]);
        var place = store.ExportPlaceWord(point.Id);
        using (var package = ZipFile.OpenRead(place.FilePath))
        {
            using var reader = new StreamReader(package.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("营业许可证", xml);
            Assert.Contains("字段图片", xml);
            Assert.Equal(2, package.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
        }

        var type = store.GetCampaignTypes()[0];
        store.SaveCampaignType(type.Id, new(type.Name, [new("license", "施工许可证", "image", false)]));
        var license = store.UploadPhoto(WordExporterTests.Jpeg(80, 90), "before");
        var before = store.UploadPhoto(WordExporterTests.Jpeg(80, 90), "before");
        var after = store.UploadPhoto(WordExporterTests.Jpeg(80, 90), "after");
        var campaign = store.SaveCampaign(null, new("现场整治", type.Id, point.Id, Store.Today, "施工",
            BeforePhotoIds: [license.Id, before.Id], AfterPhotoIds: [after.Id],
            ExtraValues: Data(JsonSerializer.Serialize(new { license = license.Id }))));
        foreach (var export in new[] { store.ExportCampaignWord(campaign.Campaign.Id),
            store.ExportCampaignLedgerWord(Store.Today, Store.Today, includePhotos: true) })
        {
            using var package = ZipFile.OpenRead(export.FilePath);
            using var reader = new StreamReader(package.GetEntry("word/document.xml")!.Open());
            var xml = reader.ReadToEnd();
            Assert.Contains("施工许可证", xml);
            Assert.Contains("整治前", xml);
            Assert.Contains("整治后", xml);
            Assert.Equal(3, package.Entries.Count(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void PhotoDossiersAndProgressSurviveWordExport()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        store.SaveCommunityRecord("use", null, new(point.Id, "便民商店", Data($$"""{"position":"一楼东侧","purposeId":"purpose-shop","state":"使用中","photoIds":["{{photo.Id}}"]}""")));
        var place = store.ExportPlaceWord(point.Id);
        using (var zip = ZipFile.OpenRead(place.FilePath))
        {
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            Assert.Contains("便民商店", reader.ReadToEnd()); Assert.Contains(zip.Entries, x => x.FullName.StartsWith("word/media/"));
        }
        var record = store.CreateInspection(new(point.Id, Store.Today, "张三", "issues", Issues: [new("通道堆物", "清理", Store.Today)]));
        var issue = record.Issues[0];
        store.OperateIssue(issue.Id, new("transfer", Store.Today, "已转相关单位处理", ResponsibleUnit: "物业", Revision: issue.Revision));
        var word = store.ExportInspectionWord(record.Inspection.Id, false);
        using var file = ZipFile.OpenRead(word.FilePath); using var text = new StreamReader(file.GetEntry("word/document.xml")!.Open());
        var xml = text.ReadToEnd(); Assert.Contains("已转相关单位处理", xml); Assert.Contains("物业", xml);
    }
}
