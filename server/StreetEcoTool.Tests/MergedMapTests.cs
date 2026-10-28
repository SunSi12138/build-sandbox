using Xunit;

namespace StreetEcoTool.Tests;

public sealed class MergedMapTests
{
    [Fact]
    public void MapAndMatterTagFiltersReturnAllMatchesAndKeepInspectionGeometrySnapshot()
    {
        using var environment = new V2Environment(); using var store = environment.Open();
        var point = environment.CreatePoint(store);
        point = store.SavePoint(point.Id, new(point.Name, point.CommunityId, point.TypeId, point.Address, Shape: new("Point", [[125,43]])));
        var tag = store.SaveTag(null, new("地图事项"));
        var matter = store.SaveMatter(null, new("现场核查", "public", Store.Today, "内容"));
        for (var i=0;i<32;i++) store.CreateInspection(new(point.Id,Store.Today,"甲","normal",MatterIds:[matter.Id],TagIds:[tag.Id]));
        store.CreateInspection(new(point.Id,Store.Today,"乙","normal"));
        store.SavePoint(point.Id, new(point.Name,point.CommunityId,point.TypeId,point.Address,Shape:new("Point",[[126,44]]),TagIds:[tag.Id]));
        var result = store.ListInspections(new(TagIds:[tag.Id],MatterId:matter.Id,Map:true));
        Assert.Equal(32,result.Total);Assert.Equal(32,result.Items.Length);Assert.Equal(1,result.Page);
        Assert.All(result.Items, item => { Assert.Equal(new double[]{125,43},item.Shape!.Coordinates[0]); Assert.Equal(125,item.Longitude); Assert.Single(item.Tags);Assert.Single(item.Matters); });
        Assert.Equal(result.Items.Select(x=>x.Id).Order(),store.GetLedgerInspections(Store.Today,Store.Today,filters:new(TagIds:[tag.Id],MatterId:matter.Id)).Select(x=>x.Id).Order());
        var mapPoints=store.ListPoints(new(TagIds:[tag.Id],Map:true));Assert.Equal(point.Id,Assert.Single(mapPoints.Items).Id);
    }
}
