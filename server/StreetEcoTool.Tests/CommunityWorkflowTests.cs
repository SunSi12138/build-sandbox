using System.Text;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityWorkflowTests
{
    private static JsonElement Data(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }

    [Fact]
    public void UsePhotoHistorySurvivesRemovalAndCannotBeStolen()
    {
        using var environment = new V2Environment(); using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var record = store.SaveCommunityRecord("use", null, new(point.Id, "便利店", Data($$"""{"unitId":"","purposes":["商店"],"state":"使用中","photoIds":["{{photo.Id}}"]}""")));
        Assert.Single(store.GetRecordPhotos(record.Id));
        Assert.Throws<ApiException>(() => store.AttachPointPhotos(point.Id, new([photo.Id])));
        Assert.Throws<ApiException>(() => store.DeletePhoto(photo.Id));
        Assert.Throws<ApiException>(() => store.DeletePoint(point.Id));
        V2Environment.Execute(store.DatabasePath, "UPDATE photos SET uploaded_at='2000-01-01T00:00:00';");
        store.SaveCommunityRecord("use", record.Id, new(point.Id, "便利店", Data("""{"unitId":"","purposes":["商店"],"state":"已结束","endDate":"2026-10-01","photoIds":[]}"""), record.Revision));
        store.CleanupOrphanPhotos();
        Assert.Empty(store.GetRecordPhotos(record.Id));
        using var original = store.OpenPhoto(photo.Id); Assert.True(original.Length > 0);
        Assert.Contains(photo.Id, store.CommunityHistory(record.Id).Last().After);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void ImportPreviewApplyAndRetryKeepAtomicRows()
    {
        using var environment = new V2Environment(); using var store = environment.Open();
        var grid = store.PreviewImport(Encoding.UTF8.GetBytes("名称,区域坐标\n第一网格,\n"), "grid.csv", "grid", "wgs84");
        Assert.Equal(new ImportResult(1, 0), store.ApplyImport(new(grid.Rows)));
        Assert.Equal(new ImportResult(0, 1), store.ApplyImport(new(grid.Rows)));
        using var sheet = new MemoryStream();
        ExcelReport.Write(sheet, new("导入", "", ["名称", "网格", "地址", "经度", "纬度"], [["门店甲", "第一网格", "一号路", "88.89", "29.27"], ["门店乙", "不存在的网格", "二号路", "", ""]]));
        var preview = store.PreviewImport(sheet.ToArray(), "places.xlsx", "point", "wgs84");
        Assert.Throws<ApiException>(() => store.ApplyImport(new(preview.Rows)));
        Assert.Equal(0, store.ListPoints(new()).Total);
        var corrected = preview.Rows.Select(x => x with { Grid = "第一网格" }).ToArray();
        Assert.Equal(new ImportResult(2, 0), store.ApplyImport(new(corrected)));
        Assert.Equal(new ImportResult(0, 2), store.ApplyImport(new(corrected)));
        Assert.Single(store.GetMapState().Features, x => x.Kind == "point");
    }

    [Fact]
    public void NestedUnitsAndIncompleteChecklistCannotLoseMeaning()
    {
        using var environment = new V2Environment(); using var store = environment.Open(); var point = environment.CreatePoint(store);
        var floor = store.SaveCommunityRecord("unit", null, new(point.Id, "一楼", Data("{}")));
        var room = store.SaveCommunityRecord("unit", null, new(point.Id, "101", Data($$"""{"parentUnitId":"{{floor.Id}}"}""")));
        Assert.Throws<ApiException>(() => store.SaveCommunityRecord("unit", floor.Id, new(point.Id, floor.Name, Data($$"""{"parentUnitId":"{{room.Id}}"}"""), floor.Revision)));
        Assert.Throws<ApiException>(() => store.SaveCommunityRecord("unit", floor.Id, new(point.Id, floor.Name, floor.Data, floor.Revision, true)));
        Assert.Throws<ApiException>(() => store.CreateInspection(new(point.Id, Store.Today, "张三", "normal", Checklist: [new("消防通道", "unchecked")])));
        var incomplete = store.CreateInspection(new(point.Id, Store.Today, "张三", "incomplete", Checklist: [new("消防通道", "unchecked")], IncompleteReason: "未开门"));
        Assert.Equal("incomplete", incomplete.Inspection.RectifyStatus);
        var plan = store.SaveCommunityRecord("plan", null, new("", "今日检查", Data($$"""{"inspector":"张三","from":"{{Store.Today}}","to":"{{Store.Today}}","typeId":"{{store.ListCommunityRecords("inspection-type")[0].Id}}","pointIds":["{{point.Id}}"]}""")));
        Assert.Equal("待检查", store.GetReport(new("plans", PlanId: plan.Id)).Rows[0][8]);
    }
}
