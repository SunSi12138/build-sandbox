using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class FieldSnapshotTests
{
    private static JsonElement Data(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    [Fact]
    public void FieldLabelsAndValuesStayTogetherAcrossDictionaryEdits()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var pointType = store.GetPointTypes()[0];
        store.SavePointType(pointType.Id, new(pointType.Name,
            [new("license", "原场所字段", "text", false)]));
        var point = environment.CreatePoint(store);
        var savedPoint = store.SavePoint(point.Id, new(point.Name, point.CommunityId, point.TypeId,
            point.Address, Revision: point.Revision, ExtraValues: Data("""{"license":"场所原值"}""")));
        var inspectionType = store.SaveCommunityRecord("inspection-type", null, new("", "检查类型",
            Data("""{"fields":[{"id":"risk","label":"原检查字段","type":"text","required":false}]}""")));
        var savedInspection = store.CreateInspection(new(point.Id, Store.Today, "张三", "normal",
            InspectionTypeId: inspectionType.Id, ExtraValues: Data("""{"risk":"检查原值"}""")));
        var campaignType = store.GetCampaignTypes()[0];
        store.SaveCampaignType(campaignType.Id, new(campaignType.Name,
            [new("result", "原整治字段", "text", false)]));
        var savedCampaign = store.SaveCampaign(null, new("现场整治", campaignType.Id, point.Id,
            Store.Today, "处理现场", ExtraValues: Data("""{"result":"整治原值"}""")));

        store.SavePointType(pointType.Id, new("已更名场所类型", [new("updated", "新场所字段", "text", false)]));
        store.SaveCommunityRecord("inspection-type", inspectionType.Id,
            new("", "已停用检查类型", Data("""{"fields":[]}"""), inspectionType.Revision, true));
        store.SaveCampaignType(campaignType.Id, new("已更名整治类型", []));
        var editedPoint = store.SavePoint(point.Id, new(savedPoint.Name, savedPoint.CommunityId,
            savedPoint.TypeId, savedPoint.Address, Notes: "只改备注", Revision: savedPoint.Revision));
        var editedInspection = store.UpdateInspection(savedInspection.Inspection.Id,
            new(Store.Today, "张三", Notes: "只改备注", Revision: savedInspection.Inspection.Revision));
        var editedCampaign = store.SaveCampaign(savedCampaign.Campaign.Id,
            new(savedCampaign.Campaign.Name, campaignType.Id, point.Id, Store.Today, "处理现场", Notes: "只改备注"));

        Assert.Equal("原场所字段", Assert.Single(editedPoint.FieldSnapshot!).Label);
        Assert.Equal("场所原值", editedPoint.ExtraValues!.Value.GetProperty("license").GetString());
        Assert.Equal("原检查字段", Assert.Single(editedInspection.Inspection.FieldSnapshot!).Label);
        Assert.Equal("检查原值", editedInspection.Inspection.ExtraValues!.Value.GetProperty("risk").GetString());
        Assert.Equal("原整治字段", Assert.Single(editedCampaign.Campaign.FieldSnapshot!).Label);
        Assert.Equal("整治原值", editedCampaign.Campaign.ExtraValues!.Value.GetProperty("result").GetString());
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
