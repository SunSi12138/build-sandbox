using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityManagementTests
{
    private static JsonElement Data(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    [Fact]
    public void StaleCoreChangesCannotOverwritePointIssueOrInspection()
    {
        using var environment = new V2Environment(); using var store = environment.Open(); var point = environment.CreatePoint(store);
        var request = new PointWriteRequest("新名称", point.CommunityId, point.TypeId, point.Address, Revision: point.Revision);
        store.SavePoint(point.Id, request);
        Assert.Throws<ApiException>(() => store.SavePoint(point.Id, request with { Name = "旧页面覆盖" }));
        var inspection = store.CreateInspection(new(point.Id, Store.Today, "张三", "issues", Issues: [new("消防通道堵塞", "清理", Store.Today)]));
        var issue = inspection.Issues[0];
        store.OperateIssue(issue.Id, new("assign", Store.Today, "安排跟进", Follower: "李四", Revision: issue.Revision));
        Assert.Throws<ApiException>(() => store.UpdateIssue(issue.Id, new("覆盖", "清理", Store.Today, "社区", issue.Revision)));
        Assert.Throws<ApiException>(() => store.UpdateInspection(inspection.Inspection.Id, new(Store.Today, "改人", Revision: inspection.Inspection.Revision)));
        Assert.Equal("张三", store.GetInspection(inspection.Inspection.Id).Inspector);
        Assert.Equal("新名称", store.GetPoint(point.Id).Name);
    }

    [Fact]
    public void MergePreservesInspectionSnapshotsUsesPhotosAndPlanScope()
    {
        using var environment = new V2Environment(); using var store = environment.Open(); var source = environment.CreatePoint(store);
        var target = store.SavePoint(null, new("保留场所", source.CommunityId, source.TypeId, "新地址"));
        var use = store.SaveCommunityRecord("use", null, new(source.Id, "老商店", Data("""{"position":"一楼东侧","purposeId":"purpose-shop","state":"使用中","photoIds":[]}""")));
        var inspection = store.CreateInspection(new(source.Id, Store.Today, "张三", "normal", UseId: use.Id));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point"); store.AttachPointPhotos(source.Id, new([photo.Id]));
        var type = store.ListCommunityRecords("inspection-type")[0].Id;
        var plan = store.SaveCommunityRecord("plan", null, new("", "安排", Data($$"""{"inspector":"张三","from":"{{Store.Today}}","to":"{{Store.Today}}","typeId":"{{type}}","pointIds":["{{source.Id}}","{{target.Id}}"]}""")));
        store.MergePoints(new(source.Id, target.Id, source.Revision, target.Revision, "重复录入"));
        Assert.Equal(target.Id, store.GetInspection(inspection.Inspection.Id).PointId);
        Assert.Equal(source.Name, store.GetInspection(inspection.Inspection.Id).PointName);
        Assert.Single(store.ListCommunityRecords("use", target.Id));
        Assert.Single(store.GetPointDetail(target.Id).Photos!);
        Assert.Single(store.ListCommunityRecords("plan")[0].Data.GetProperty("pointIds").EnumerateArray());
        Assert.Equal(target.Id, store.GetPointDetail(source.Id).MergedInto);
        Assert.Throws<ApiException>(() => store.SetPointDisabled(source.Id, new(false, store.GetPoint(source.Id).Revision)));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void MergeRetainsFieldImageOwnershipAndCopiesOnlyCompatibleEmptyTextFields()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var type = store.GetPointTypes()[0];
        store.SavePointType(type.Id, new(type.Name, [
            new("shared", "相同字段", "text", false), new("missing", "待补字段", "text", false),
            new("license", "来源证照", "image", false)
        ]));
        var source = env.CreatePoint(store);
        var target = store.SavePoint(null, new("保留场所", source.CommunityId, source.TypeId, "目标地址",
            ExtraValues: Data("""{"shared":"目标已有值"}""")));
        var permit = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        var ordinary = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        source = store.SavePoint(source.Id, new(source.Name, source.CommunityId, source.TypeId, source.Address,
            Revision: source.Revision, PhotoIds: [permit.Id, ordinary.Id],
            ExtraValues: Data(JsonSerializer.Serialize(new { shared = "不应覆盖", missing = "来源补入", license = permit.Id }))));
        var preview = store.PreviewMerge(source.Id, target.Id);
        Assert.Contains(preview.Fields!, field => field.FieldId == "shared" && field.Decision == "保留目标值");
        Assert.Contains(preview.Fields!, field => field.FieldId == "missing" && field.Decision == "补入目标空字段");
        Assert.Contains(preview.Fields!, field => field.FieldId == "license" && field.Decision.Contains("原档案"));
        var merged = store.MergePoints(new(source.Id, target.Id, source.Revision, target.Revision, "重复"));
        Assert.Equal("目标已有值", merged.ExtraValues!.Value.GetProperty("shared").GetString());
        Assert.Equal("来源补入", merged.ExtraValues!.Value.GetProperty("missing").GetString());
        Assert.Equal(permit.Id, store.GetPoint(source.Id).ExtraValues!.Value.GetProperty("license").GetString());
        Assert.Equal(source.Id, store.GetPointDetail(source.Id).Photos!.Single().PointId);
        Assert.Equal(permit.Id, store.GetPointDetail(source.Id).Photos!.Single().Id);
        Assert.Equal(ordinary.Id, Assert.Single(store.GetPointDetail(target.Id).Photos!).Id);
        using (var docx = System.IO.Compression.ZipFile.OpenRead(store.ExportPlaceWord(source.Id).FilePath))
            Assert.Single(docx.Entries, x => x.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void MergeDifferentPointTypesNeverTreatsSameIdOrLabelAsEquivalent()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var typeA = store.GetPointTypes()[0];
        store.SavePointType(typeA.Id, new(typeA.Name, [new("same", "来源名称", "text", false)]));
        var typeB = store.GetPointTypes()[1];
        store.SavePointType(typeB.Id, new(typeB.Name, [new("same", "来源名称", "text", false)]));
        var source = env.CreatePoint(store);
        source = store.SavePoint(source.Id, new(source.Name, source.CommunityId, source.TypeId, source.Address,
            Revision: source.Revision, ExtraValues: Data("""{"same":"来源不同语义"}""")));
        var target = store.SavePoint(null, new("目标场所", source.CommunityId, typeB.Id, "另外位置"));
        var preview = store.PreviewMerge(source.Id, target.Id);
        Assert.Contains(preview.Fields!, x => x.FieldId == "same" && x.Decision.Contains("定义不同"));
        var merged = store.MergePoints(new(source.Id, target.Id, source.Revision, target.Revision, "核重"));
        Assert.Null(merged.ExtraValues);
        Assert.Equal("来源不同语义", store.GetPoint(source.Id).ExtraValues!.Value.GetProperty("same").GetString());
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void HandoffMovesOnlyUnfinishedWorkAndRejectsOutdatedPreview()
    {
        using var environment = new V2Environment(); using var store = environment.Open(); var point = environment.CreatePoint(store);
        var from = store.SaveCommunityRecord("staff", null, new("", "张三", Data("{}")));
        var to = store.SaveCommunityRecord("staff", null, new("", "李四", Data("{}")));
        var type = store.ListCommunityRecords("inspection-type")[0].Id;
        var plan = store.SaveCommunityRecord("plan", null, new("", "待办安排", Data($$"""{"inspector":"张三","from":"{{Store.Today}}","to":"{{Store.Today}}","typeId":"{{type}}","pointIds":["{{point.Id}}"]}""")));
        var inspection = store.CreateInspection(new(point.Id, Store.Today, "张三", "issues", Issues: [new("问题", "整改", Store.Today)]));
        var issue = inspection.Issues[0];
        store.OperateIssue(issue.Id, new("assign", Store.Today, "安排", Follower: from.Name, Revision: issue.Revision));
        var preview = store.PreviewHandoff(from.Id, to.Id);
        store.OperateIssue(issue.Id, new("progress", Store.Today, "现场跟进", Revision: store.GetIssue(issue.Id).Revision));
        Assert.Throws<ApiException>(() => store.ApplyHandoff(new(from.Id, to.Id, preview.Version, "岗位调整")));
        preview = store.PreviewHandoff(from.Id, to.Id);
        Assert.Equal(new HandoffResult(1, 1), store.ApplyHandoff(new(from.Id, to.Id, preview.Version, "岗位调整", true)));
        Assert.Equal("李四", store.ListCommunityRecords("plan").Single().Data.GetProperty("inspector").GetString());
        Assert.Equal("张三", store.GetInspection(inspection.Inspection.Id).Inspector);
        Assert.Empty(store.PreviewHandoff(from.Id, to.Id).Issues);
        Assert.Throws<ApiException>(() => store.ApplyHandoff(new(from.Id, to.Id, preview.Version, "重复交接")));
    }
}
