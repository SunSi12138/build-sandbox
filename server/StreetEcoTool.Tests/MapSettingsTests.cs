using Xunit;

namespace StreetEcoTool.Tests;

public sealed class MapSettingsTests
{
    [Fact]
    public async Task OfflineLayerCheckUsesOnlyLocalCacheAndNeverChangesSettings()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var initial = Assert.Single(store.GetMapSettings().Providers);
        var offline = store.SaveMapProvider(initial.Id, initial with
        {
            Folder = "offline-check", UrlTemplate = "", ForceRefresh = false
        });
        var before = store.GetMapSettings();

        var empty = await store.CheckMapLayerAsync(offline.Id, CancellationToken.None);
        Assert.Equal("empty", empty.Status);
        Assert.False(empty.HasCache);
        Assert.False(empty.HasUrl);

        var tile = Path.Combine(env.Root, "tiles", offline.Folder, "12", "3059", "1702.png");
        Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
        File.WriteAllBytes(tile, [137, 80, 78, 71, 13, 10, 26, 10]);
        var cached = await store.CheckMapLayerAsync(offline.Id, CancellationToken.None);
        Assert.Equal("cached", cached.Status);
        Assert.True(cached.HasCache);
        Assert.False(cached.HasUrl);
        Assert.Equal(before.Providers, store.GetMapSettings().Providers);
        Assert.Equal(before.CacheRoot, store.GetMapSettings().CacheRoot);
    }

    [Fact]
    public async Task MvtLabelLayersReuseExistingSchemaAndOnlyReadMatchingCacheTypes()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var original = Assert.Single(store.GetMapSettings().Providers);
        var vector = store.SaveMapProvider(original.Id, original with
        {
            Scheme = "xyz", Format = "mvt", Folder = "vector-label-test", UrlTemplate = "", ForceRefresh = false
        });
        Assert.Equal("xyz", vector.Scheme);
        Assert.Equal("mvt", vector.Format);
        Assert.Equal("mvt", Assert.Single(store.GetMapSettings().Providers).Format);
        Assert.Equal("mvt", Assert.Single(store.GetMapLayers()).Kind);

        var cache = Path.Combine(env.Root, "tiles", vector.Folder, "12", "3000");
        Directory.CreateDirectory(cache);
        File.WriteAllBytes(Path.Combine(cache, "1700.png"), [137, 80, 78, 71, 13, 10, 26, 10]);

        var noVector = await store.CheckMapLayerAsync(vector.Id, CancellationToken.None);
        Assert.Equal("empty", noVector.Status);
        Assert.False(noVector.HasCache);
        // A valid MVT Tile envelope starts with protobuf field 3 (layers).
        File.WriteAllBytes(Path.Combine(cache, "1700.pbf"), [0x1a, 0x02, 0x78, 0x02]);
        var hasVector = await store.CheckMapLayerAsync(vector.Id, CancellationToken.None);
        Assert.Equal("cached", hasVector.Status);
        Assert.True(hasVector.HasCache);
        Assert.False(hasVector.HasUrl);

        var tms = store.SaveMapProvider(vector.Id, vector with { Scheme = "tms" });
        Assert.Equal("tms", tms.Scheme);
        Assert.Equal("mvt", Assert.Single(store.GetMapLayers()).Kind);
        var raster = store.SaveMapProvider(tms.Id, tms with { Scheme = "xyz", Format = "raster" });
        Assert.Equal("raster", Assert.Single(store.GetMapLayers()).Kind);
        var rasterCheck = await store.CheckMapLayerAsync(raster.Id, CancellationToken.None);
        Assert.Equal("cached", rasterCheck.Status);
        Assert.True(rasterCheck.HasCache);

        var invalid = raster with { Format = "unknown" };
        Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveMapProvider(raster.Id, invalid)).StatusCode);
        Assert.Equal("xyz", Assert.Single(store.GetMapSettings().Providers).Scheme);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public async Task PmtilesArchiveUsesExistingSchemaAndCanBeCheckedOffline()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var original = Assert.Single(store.GetMapSettings().Providers);
        var pmtiles = store.CreateMapProvider(original with
        {
            Id = "", Name = "离线 PMTiles 地名", Format = "pmtiles", Scheme = "xyz",
            Folder = "archive/labels.pmtiles", UrlTemplate = "", ForceRefresh = false
        });
        Assert.Equal("pmtiles", pmtiles.Format);
        Assert.Equal("xyz", pmtiles.Scheme);
        Assert.Equal("pmtiles", store.GetMapLayers().Single(layer => layer.Id == pmtiles.Id).Kind);
        var file = Path.Combine(env.Root, "tiles", "archive", "labels.pmtiles");
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)!));
        var missing = await store.CheckMapLayerAsync(pmtiles.Id, CancellationToken.None);
        Assert.Equal("empty", missing.Status);

        var header = new byte[16384];
        "PMTiles"u8.CopyTo(header);
        header[7] = 3;
        await File.WriteAllBytesAsync(file, header);
        var ready = await store.CheckMapLayerAsync(pmtiles.Id, CancellationToken.None);
        Assert.Equal("cached", ready.Status);
        Assert.True(ready.HasCache);
        Assert.False(ready.HasUrl);

        using var reopened = env.Open();
        var persisted = reopened.GetMapSettings().Providers.Single(layer => layer.Id == pmtiles.Id);
        Assert.Equal("pmtiles", persisted.Format);
        Assert.Equal("", persisted.UrlTemplate);
        foreach (var invalid in new[]
        {
            pmtiles with { Scheme = "tms" },
            pmtiles with { Folder = "archive" },
            pmtiles with { ForceRefresh = true },
            pmtiles with { Format = "other" }
        })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveMapProvider(pmtiles.Id, invalid)).StatusCode);
            Assert.Equal("pmtiles", store.GetMapSettings().Providers.Single(layer => layer.Id == pmtiles.Id).Format);
        }
        Assert.Equal("raster", store.GetMapSettings().Providers.Single(layer => layer.Id == original.Id).Format);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void EachLayerPreloadDepthZeroToThreePersistsWithoutSchemaChanges()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var original = Assert.Single(store.GetMapSettings().Providers);
        Assert.Equal(0, original.Preload);
        var raster = store.SaveMapProvider(original.Id, original with { Preload = 1 });
        var vector = store.CreateMapProvider(original with
        {
            Id = "", Name = "矢量地名", Folder = "vector-preload", Format = "mvt", Preload = 2
        });
        var archive = store.CreateMapProvider(original with
        {
            Id = "", Name = "单文件地名", Folder = "preload/labels.pmtiles",
            Scheme = "xyz", UrlTemplate = "", Format = "pmtiles", ForceRefresh = false, Preload = 3
        });
        var config = store.GetMapSettings();
        Assert.Equal(1, config.Providers.Single(item => item.Id == raster.Id).Preload);
        Assert.Equal(2, config.Providers.Single(item => item.Id == vector.Id).Preload);
        Assert.Equal(3, config.Providers.Single(item => item.Id == archive.Id).Preload);
        var layers = store.GetMapLayers();
        Assert.Equal(("raster", 1), (layers.Single(item => item.Id == raster.Id).Kind, layers.Single(item => item.Id == raster.Id).Preload));
        Assert.Equal(("mvt", 2), (layers.Single(item => item.Id == vector.Id).Kind, layers.Single(item => item.Id == vector.Id).Preload));
        Assert.Equal(("pmtiles", 3), (layers.Single(item => item.Id == archive.Id).Kind, layers.Single(item => item.Id == archive.Id).Preload));

        foreach (var invalid in new[] { -1, 4, 99 })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() =>
                store.SaveMapProvider(archive.Id, archive with { Preload = invalid })).StatusCode);
            Assert.Equal(3, store.GetMapSettings().Providers.Single(item => item.Id == archive.Id).Preload);
        }

        using var reopened = env.Open();
        Assert.Equal(1, reopened.GetMapSettings().Providers.Single(item => item.Id == raster.Id).Preload);
        Assert.Equal(2, reopened.GetMapSettings().Providers.Single(item => item.Id == vector.Id).Preload);
        Assert.Equal(3, reopened.GetMapSettings().Providers.Single(item => item.Id == archive.Id).Preload);
        Assert.Equal("pmtiles", reopened.GetMapLayers().Single(item => item.Id == archive.Id).Kind);
        var reset = store.SaveMapProvider(vector.Id, vector with { Preload = 0 });
        Assert.Equal(0, reset.Preload);
        Assert.Equal("mvt", store.GetMapLayers().Single(item => item.Id == vector.Id).Kind);
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

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
