using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class WordCustomFieldTests
{
    private static JsonElement Data(object value)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return json.RootElement.Clone();
    }

    private static string WordXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
        Assert.Single(archive.Entries, item => item.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        return reader.ReadToEnd();
    }

    [Fact]
    public void ThreeDocumentsContainHistoricalCustomLabelsValuesAndNamedImages()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var pointType = store.GetPointTypes()[0];
        store.SavePointType(pointType.Id, new(pointType.Name, [
            new("risk", "建筑危险等级", "text", false, true),
            new("count", "楼层数量", "number", false),
            new("permit", "建筑许可证", "image", false)
        ]));
        var point = env.CreatePoint(store);
        var buildingImage = store.UploadPhoto(WordExporterTests.Jpeg(110, 80), "point");
        var savedPoint = store.SavePoint(point.Id, new(point.Name, point.CommunityId, point.TypeId,
            point.Address, Revision: point.Revision, PhotoIds: [buildingImage.Id],
            ExtraValues: Data(new Dictionary<string, object> {
                ["risk"] = "第一行\\n第二行", ["count"] = 0, ["permit"] = buildingImage.Id
            })));
        var checkType = store.SaveCommunityRecord("inspection-type", null, new("", "扩展检查",
            Data(new { fields = new[] { new { id = "check-note", label = "检查原始说明", type = "text", required = false },
                new { id = "check-photo", label = "检查证件", type = "image", required = false } } })));
        var checkImage = store.UploadPhoto(WordExporterTests.Jpeg(120, 70), "site");
        var check = store.CreateInspection(new(point.Id, Store.Today, "张三", "normal",
            InspectionTypeId: checkType.Id, PhotoIds: [checkImage.Id], ExtraValues: Data(new Dictionary<string,string> {
                ["check-note"] = "检查编号A-133", ["check-photo"] = checkImage.Id
            })));
        var campaignType = store.GetCampaignTypes()[0];
        store.SaveCampaignType(campaignType.Id, new(campaignType.Name, [
            new("result", "整治独立字段", "text", false), new("license", "整治许可证", "image", false)
        ]));
        var campaignImage = store.UploadPhoto(WordExporterTests.Jpeg(140, 90), "before");
        var campaign = store.SaveCampaign(null, new("现场整治", campaignType.Id, point.Id, Store.Today, "清理",
            BeforePhotoIds: [campaignImage.Id], ExtraValues: Data(new Dictionary<string,string> {
                ["result"] = "整治编号Z-812", ["license"] = campaignImage.Id
            })));
        store.SavePointType(pointType.Id, new(pointType.Name, []));
        store.SaveCommunityRecord("inspection-type", checkType.Id, new("", checkType.Name, Data(new { fields = Array.Empty<object>() }), checkType.Revision, true));
        store.SaveCampaignType(campaignType.Id, new(campaignType.Name, []));
        var place = WordXml(store.ExportPlaceWord(savedPoint.Id).FilePath);
        Assert.Contains("建筑危险等级", place);
        Assert.Contains("第一行", place);
        Assert.Contains("第二行", place);
        Assert.Contains("楼层数量", place);
        Assert.Contains(">0<", place);
        Assert.Contains("建筑许可证", place);
        var inspection = WordXml(store.ExportInspectionWord(check.Inspection.Id, false).FilePath);
        Assert.Contains("检查原始说明", inspection);
        Assert.Contains("检查编号A-133", inspection);
        Assert.Contains("检查证件", inspection);
        var campaignXml = WordXml(store.ExportCampaignWord(campaign.Campaign.Id).FilePath);
        Assert.Contains("整治独立字段", campaignXml);
        Assert.Contains("整治编号Z-812", campaignXml);
        Assert.Contains("整治许可证", campaignXml);
    }
}
