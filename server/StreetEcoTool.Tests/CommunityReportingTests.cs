using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;
public sealed class CommunityReportingTests
{
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
        store.SetArchived("inspection", normal.Inspection.Id, new(true, "重复检查", normal.Inspection.Revision));
        Assert.Contains(store.GetTypeReminders(), x => x.PointId == point.Id && x.TypeId == type.Id);
        Assert.Equal(0, store.GetStatistics(Store.Today, Store.Today, "").Total.CheckedPlaces);
    }
    [Fact]
    public void PhotoDossiersAndProgressSurviveWordExport()
    {
        using var env = new V2Environment(); using var store = env.Open(); var point = env.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        store.SaveCommunityRecord("use", null, new(point.Id, "便民商店", Data($$"""{"purposes":["商店"],"state":"使用中","photoIds":["{{photo.Id}}"]}""")));
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
