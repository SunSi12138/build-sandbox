using Xunit;

namespace StreetEcoTool.Tests;

public sealed class MapSettingsTests
{
    [Fact]
    public void LayerSettingsPersistInDatabaseWithoutChangingOtherLayers()
    {
        using var env = new V2Environment(); TileProvider saved;
        using (var store = env.Open())
        {
            var first = Assert.Single(store.GetMapSettings().Providers);
            var other = store.CreateMapProvider(first with { Name = "另一个图层", Folder = "other" });
            var provider = first with
            {
                Name = "我的底图", Folder = Path.Combine(env.Root, "地图缓存"), Scheme = "xyz",
                UrlTemplate = "https://example.invalid/{z}/{x}/{y}?key={key}&mk={mk}", ApiKey = "test-key", MapKey = "test-map", ForceRefresh = true
            };
            saved = store.SaveMapProvider(provider.Id, provider);
            Assert.Equal(provider, saved); Assert.True(Directory.Exists(provider.Folder));
            Assert.Equal(other, store.GetMapSettings().Providers.Single(item => item.Id == other.Id));
            Assert.False(File.Exists(Path.Combine(env.Root, "map-providers.local.json")));
        }
        using var reopened = env.Open();
        Assert.Equal(saved, reopened.GetMapSettings().Providers.Single(item => item.Id == saved.Id));
    }

    [Fact]
    public void InvalidSettingsDoNotChangeDatabaseAndRelativeFoldersUseTiles()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var provider = Assert.Single(store.GetMapSettings().Providers) with { Name = "本地缓存", Folder = "custom-cache" };
        store.SaveMapProvider(provider.Id, provider);
        Assert.True(Directory.Exists(Path.Combine(env.Root, "tiles", "custom-cache")));
        foreach (var invalid in new[]
        {
            provider with { Name = "" }, provider with { Scheme = "other" }, provider with { ForceRefresh = true },
            provider with { UrlTemplate = "file:///tmp/{z}/{x}/{y}" }, provider with { UrlTemplate = "https://example.invalid/{z}/{x}/{y}?key={key}" }
        })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveMapProvider(provider.Id, invalid)).StatusCode);
            Assert.Equal(provider, Assert.Single(store.GetMapSettings().Providers));
        }
    }

    [Fact]
    public async Task LayerOrderEnabledStateAndSettingsRestoreTogetherWhileDeletingKeepsCache()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var first = Assert.Single(store.GetMapSettings().Providers);
        var second = store.CreateMapProvider(first with { Name = "标注", Folder = "labels", ApiKey = "saved-key" });
        store.ReorderMapProviders(new([second.Id, first.Id]));
        Assert.Equal(new[] { second.Id, first.Id }, store.GetMapSettings().Providers.Select(item => item.Id));
        Assert.Equal(new[] { first.Id, second.Id }, store.GetMapLayers().Select(item => item.Id));
        store.SetMapProviderEnabled(second.Id, new(false));
        Assert.Equal(first.Id, Assert.Single(store.GetMapLayers()).Id);
        var expected = store.GetMapSettings().Providers;
        var backup = store.CreateBackupZip();
        var cached = Path.Combine(env.Root, "tiles", "labels", "keep.jpg"); File.WriteAllText(cached, "cache");
        store.DeleteMapProvider(second.Id); Assert.Equal("cache", File.ReadAllText(cached));
        store.SetMapProviderEnabled(first.Id, new(false)); Assert.Empty(store.GetMapLayers());
        using var input = File.OpenRead(backup);
        var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
        Assert.True(store.Restore(new(preview.Token, true)).Ok);
        Assert.Equal(expected, store.GetMapSettings().Providers);
        Assert.Equal(first.Id, Assert.Single(store.GetMapLayers()).Id);
        Assert.Equal("cache", File.ReadAllText(cached));
    }
}
