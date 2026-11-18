using System.Text;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class ImportPreviewTests
{
    [Fact]
    public void MinimalCsvUsesCurrentDefaultTypeAndRequiredFieldsCanBeEnteredInPreview()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var grid = store.GetCommunities()[0];
        var types = store.GetPointTypes();
        var defaultType = types.First(x => x.Name == "永久建筑");
        var csv = Encoding.UTF8.GetBytes("名称,网格,地址\n测试商铺," + grid.Name + ",十字路口5号\n");
        var preview = store.PreviewImport(csv, "minimal.csv", "point", "wgs84");
        Assert.Equal(defaultType.Name, Assert.Single(preview.Rows).Type);
        Assert.Equal(1, store.ApplyImport(new(preview.Rows)).Added);
        Assert.Equal(defaultType.Id, Assert.Single(store.ListPoints(new(Q: "测试商铺")).Items).TypeId);

        store.SavePointType(defaultType.Id, new(defaultType.Name, [new("license", "许可证编号", "text", true)]));
        var again = store.PreviewImport(Encoding.UTF8.GetBytes("名称,网格,地址\n另一商铺," + grid.Name + ",新地址6号\n"),
            "fields.csv", "point", "wgs84");
        Assert.Contains("必填扩展字段", Assert.Single(again.Rows).Warning);
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.ApplyImport(new(again.Rows))).StatusCode);
        using var fields = JsonDocument.Parse("""{"license":"证号A-021"}""");
        var filled = again.Rows[0] with { ExtraValues = fields.RootElement.Clone() };
        Assert.Equal(1, store.ApplyImport(new([filled])).Added);
        Assert.Equal("证号A-021", Assert.Single(store.ListPoints(new(Q: "另一商铺")).Items).ExtraValues!.Value.GetProperty("license").GetString());
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }
}
