using System.IO.Compression;
using System.Xml.Linq;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PointCampaignTests
{
    private static CampaignWriteRequest Campaign(Store store, string? pointId, PointWriteRequest? newPoint = null) =>
        new("清理作业", store.GetCampaignTypes()[0].Id, pointId, Store.Today, "清理垃圾。", NewPoint: newPoint);

    private static PointWriteRequest Temporary(PointDto point) =>
        new("临时河岸", point.CommunityId, point.TypeId, "河岸道路", Contact: "张三 李四", Longitude: 88.893426, Latitude: 29.274331);

    [Fact]
    public void CampaignUsesPointSnapshotsAndAppearsInHistoryWithoutCreatingInspection()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var request = Campaign(store, point.Id);
        var detail = store.SaveCampaign(null, request);
        Assert.Equal(point.Id, detail.Campaign.PointId);
        Assert.Equal(point.Name, detail.Campaign.PointName);
        Assert.Equal(point.Address, detail.Campaign.Location);
        Assert.Equal(point.CommunityId, detail.Campaign.CommunityId);
        Assert.Empty(store.GetPointDetail(point.Id).Inspections);
        Assert.Equal(detail.Campaign.Id, Assert.Single(store.GetPointDetail(point.Id).Campaigns!).Id);
        Assert.Equal(detail.Campaign.Id, Assert.Single(store.ListCampaigns(new(PointId: point.Id)).Items).Id);
        Assert.Null(store.GetPoint(point.Id).LastInspectionDate);
        store.SavePoint(point.Id, new("改名", point.CommunityId, point.TypeId, "新地址", Contact: "王五", Revision: point.Revision));
        store.SaveCommunity(point.CommunityId, new("新社区", "新社区居民委员会"));
        var updated = store.SaveCampaign(detail.Campaign.Id, request with { Content = "修改活动内容" });
        Assert.Equal(point.Name, updated.Campaign.PointName);
        Assert.Equal(point.Address, updated.Campaign.Location);
        Assert.Equal(point.CommunityName, updated.Campaign.CommunityName);
        Assert.Empty(updated.Campaign.Participants);
        Assert.Contains(point.Name, DocumentText(store.ExportCampaignWord(detail.Campaign.Id).FilePath));
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.DeletePoint(point.Id)).StatusCode);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void PointSelectionIsExclusiveAndExistingCampaignCannotChangePoint()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var other = store.SavePoint(null, new("乙", point.CommunityId, point.TypeId, "乙路"));
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveCampaign(null, Campaign(store, null))).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveCampaign(null, Campaign(store, point.Id, Temporary(point)))).StatusCode);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.SaveCampaign(null, Campaign(store, "missing"))).StatusCode);
        var saved = store.SaveCampaign(null, Campaign(store, point.Id));
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveCampaign(saved.Campaign.Id, Campaign(store, other.Id))).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveCampaign(saved.Campaign.Id, Campaign(store, null, Temporary(point)))).StatusCode);
        Assert.Equal(point.Id, store.GetCampaign(saved.Campaign.Id).PointId);
        store.SetPointDisabled(point.Id, new(true, Revision: store.GetPoint(point.Id).Revision));
        Assert.Equal(409, Assert.Throws<ApiException>(() => store.SaveCampaign(null, Campaign(store, point.Id))).StatusCode);
        Assert.Equal("补充", store.SaveCampaign(saved.Campaign.Id, Campaign(store, point.Id) with { Notes = "补充" }).Campaign.Notes);
    }

    [Fact]
    public void TemporaryCampaignAndPhotosAreOneTransactionAndCanBePromoted()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var before = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var wrong = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "site");
        var request = Campaign(store, null, Temporary(point));
        Assert.Throws<ApiException>(() => store.SaveCampaign(null, request with { BeforePhotoIds = [before.Id], AfterPhotoIds = [wrong.Id] }));
        Assert.Empty(store.ListCampaigns(new()).Items);
        Assert.Empty(store.ListPoints(new(Temporary: "true")).Items);
        Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos WHERE campaign_id IS NOT NULL;"));
        var created = store.SaveCampaign(null, request with { BeforePhotoIds = [before.Id] });
        var temporary = store.GetPoint(created.Campaign.PointId);
        Assert.True(temporary.Temporary);
        Assert.Equal("irregular", temporary.Frequency);
        Assert.Equal(88.893426, temporary.Longitude);
        Assert.Equal(created.Campaign.Id, Assert.Single(store.GetPointDetail(temporary.Id).Campaigns!).Id);
        Assert.DoesNotContain(store.ListPoints(new(Missed: true)).Items, p => p.Id == temporary.Id);
        store.PromotePoint(temporary.Id, new("monthly"));
        Assert.False(store.GetCampaign(created.Campaign.Id).PointTemporary);
        store.DeleteCampaign(created.Campaign.Id);
        Assert.False(store.GetPoint(temporary.Id).Temporary);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TemporaryPointLivesUntilLastInspectionAndCampaignAreBothRemoved(bool deleteInspectionFirst)
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var campaignPhoto = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var campaign = store.SaveCampaign(null, Campaign(store, null, Temporary(point)) with { BeforePhotoIds = [campaignPhoto.Id] });
        var id = campaign.Campaign.PointId;
        var pointPhoto = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        store.AttachPointPhotos(id, new([pointPhoto.Id]));
        var inspection = store.CreateInspection(new(id, Store.Today, "张三", "normal"));
        if (deleteInspectionFirst) store.DeleteInspection(inspection.Inspection.Id);
        else store.DeleteCampaign(campaign.Campaign.Id);
        Assert.Equal(id, store.GetPoint(id).Id);
        using (var photo = store.OpenPhoto(pointPhoto.Id)) Assert.True(photo.CanRead);
        if (deleteInspectionFirst) store.DeleteCampaign(campaign.Campaign.Id);
        else store.DeleteInspection(inspection.Inspection.Id);
        Assert.Equal(404, Assert.Throws<ApiException>(() => store.GetPoint(id)).StatusCode);
        Assert.Throws<ApiException>(() => store.OpenPhoto(pointPhoto.Id));
        Assert.Throws<ApiException>(() => store.OpenPhoto(campaignPhoto.Id));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void FinalTemporaryRecordOnlyRecyclesPointWithoutIndependentUse(bool deleteInspection, bool hasUse)
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var ordinary = environment.CreatePoint(store);
        var independentUse = store.SaveCommunityRecord("use", null, new(ordinary.Id, "长期商铺",
            JsonDocument.Parse("""{"unitId":"","position":"沿街","purposeId":"purpose-shop","state":"使用中"}""").RootElement.Clone()));
        var temporary = deleteInspection
            ? store.CreateInspection(new(null, Store.Today, "张三", "normal", NewPoint: Temporary(ordinary))).Inspection.PointId
            : store.SaveCampaign(null, Campaign(store, null, Temporary(ordinary))).Campaign.PointId;
        var point = store.GetPoint(temporary);
        store.SavePoint(point.Id, new(point.Name, point.CommunityId, point.TypeId, point.Address,
            Shape: new("Point", [[88.893426, 29.274331]]), ShapeRevision: 0, Revision: point.Revision));
        if (hasUse)
            store.SaveCommunityRecord("use", null, new(temporary, "临时商户",
                JsonDocument.Parse("""{"unitId":"","position":"现场","purposeId":"purpose-shop","state":"使用中"}""").RootElement.Clone()));
        if (deleteInspection)
            store.DeleteInspection(Assert.Single(store.GetPointDetail(temporary).Inspections).Id);
        else
            store.DeleteCampaign(Assert.Single(store.GetPointDetail(temporary).Campaigns!).Id);
        if (hasUse)
        {
            Assert.Equal(temporary, store.GetPoint(temporary).Id);
            Assert.Equal(2, store.GetReport(new("uses")).Rows.Length);
        }
        else
        {
            Assert.Equal(404, Assert.Throws<ApiException>(() => store.GetPoint(temporary)).StatusCode);
            Assert.Equal(0, V2Environment.Number(store.DatabasePath,
                $"SELECT COUNT(*) FROM map_shapes WHERE kind='point' AND record_id='{temporary}';"));
            Assert.Single(store.GetReport(new("uses")).Rows);
        }
        Assert.Contains(store.GetReport(new("uses")).Rows, row => row[0] == independentUse.Id);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("plan")]
    public void TemporaryPointSurvivesIndependentUnitOrPlan(string kind)
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var ordinary = environment.CreatePoint(store);
        var campaign = store.SaveCampaign(null, Campaign(store, null, Temporary(ordinary)));
        var pointId = campaign.Campaign.PointId;
        if (kind == "unit")
            store.SaveCommunityRecord("unit", null, new(pointId, "临时单元", JsonDocument.Parse("{}").RootElement.Clone()));
        else
        {
            var body = JsonSerializer.Serialize(new { inspector = "张三", @from = Store.Today, to = Store.Today,
                typeId = store.ListCommunityRecords("inspection-type")[0].Id, pointIds = new[] { pointId } });
            store.SaveCommunityRecord("plan", null, new("", "临时安排", JsonDocument.Parse(body).RootElement.Clone()));
        }
        store.DeleteCampaign(campaign.Campaign.Id);
        Assert.Equal(pointId, store.GetPoint(pointId).Id);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void ThreeLedgersFilterByPointIdAndPreserveLogicalCountsAndPhotos()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var a = environment.CreatePoint(store);
        var b = store.SavePoint(null, new("不应出现的乙点位", a.CommunityId, a.TypeId, "乙地址"));
        var c = store.SavePoint(null, new("丙点位", a.CommunityId, a.TypeId, "丙地址"));
        foreach (var point in new[] { a, b, c })
        {
            store.CreateInspection(new(point.Id, Store.Today, "张三 李四", "normal"));
            store.CreateInspection(new(point.Id, Store.Today, "张三 李四", "issues", Issues: [new(point.Name + "问题", "整改", Store.Today)]));
            var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
            store.SaveCampaign(null, Campaign(store, point.Id) with { Name = point.Name + "整治", ParticipantCount = 2, BeforePhotoIds = [photo.Id] });
        }
        var points = new[] { a.Id, c.Id, a.Id };
        var inspection = store.ExportInspectionLedgerWord(Store.Today, Store.Today, pointIds: points);
        var issues = store.ExportLedgerWord(Store.Today, Store.Today, pointIds: points);
        var campaigns = store.ExportCampaignLedgerWord(Store.Today, Store.Today, includePhotos: true, pointIds: points);
        foreach (var file in new[] { inspection, issues, campaigns })
        {
            var text = DocumentText(file.FilePath);
            Assert.Contains(a.Name, text); Assert.Contains(c.Name, text);
            Assert.DoesNotContain(b.Name, text);
        }
        Assert.Contains("共 4 次排查", DocumentText(inspection.FilePath));
        Assert.Contains("共 2 条", DocumentText(issues.FilePath));
        Assert.Contains("共 2 次整治", DocumentText(campaigns.FilePath));
        using (var zip = ZipFile.OpenRead(campaigns.FilePath)) Assert.Equal(2, zip.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
        Assert.Throws<ApiException>(() => store.ExportLedgerWord(Store.Today, Store.Today, pointIds: ["missing"]));
        Assert.Throws<ApiException>(() => store.ExportInspectionLedgerWord(Store.Today, Store.Today, pointIds: [""]));
        Assert.Empty(store.GetLedgerCampaigns("2000-01-01", "2000-01-02", pointIds: [a.Id]));
    }

    private static string DocumentText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var entry = zip.GetEntry("word/document.xml")!.Open();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        return string.Concat(XDocument.Load(entry).Descendants(w + "t").Select(e => e.Value));
    }
}
