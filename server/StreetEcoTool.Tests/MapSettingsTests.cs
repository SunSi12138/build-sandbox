using Microsoft.Data.Sqlite;
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
            pmtiles with { Preload = -1 },
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
    public void VectorStyleDirectoryIsOptionalPersistentAndReadWithoutCopying()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var original = Assert.Single(store.GetMapSettings().Providers);
        var styleDirectory = Path.Combine(env.Root, "tiles", "custom-style");
        Directory.CreateDirectory(Path.Combine(styleDirectory, "font", "Test Font"));
        Directory.CreateDirectory(Path.Combine(styleDirectory, "sprite"));
        File.WriteAllText(Path.Combine(styleDirectory, "style.json"),
            """{"version":8,"sources":{"base":{"type":"vector"}},"layers":[]}""");
        File.WriteAllBytes(Path.Combine(styleDirectory, "font", "Test Font", "0-255.pbf"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(styleDirectory, "sprite", "png.json"), "{}");

        var vector = store.CreateMapProvider(original with
        {
            Id = "", Name = "外部矢量样式", Folder = "vector-pbf", Scheme = "xyz",
            Format = "mvt", StyleDirectory = "custom-style"
        });
        Assert.True(vector.HasStyle);
        Assert.Equal("custom-style", vector.StyleDirectory);
        Assert.Equal($"/api/map/style/{vector.Id}/style.json", store.GetMapLayers().Single(layer => layer.Id == vector.Id).StyleUrl);
        using (var reopened = env.Open())
            Assert.Equal("custom-style", reopened.GetMapSettings().Providers.Single(layer => layer.Id == vector.Id).StyleDirectory);

        var archive = store.CreateMapProvider(vector with
        {
            Id = "", Name = "外部 PMTiles 样式", Folder = "archive/labels.pmtiles", Format = "pmtiles"
        });
        Assert.True(archive.HasStyle);
        Assert.Equal($"/api/map/style/{archive.Id}/style.json", store.GetMapLayers().Single(layer => layer.Id == archive.Id).StyleUrl);

        File.Delete(Path.Combine(styleDirectory, "style.json"));
        Assert.False(store.GetMapSettings().Providers.Single(layer => layer.Id == vector.Id).HasStyle);
        Assert.Null(store.GetMapLayers().Single(layer => layer.Id == vector.Id).StyleUrl);
        Assert.Equal("custom-style", store.GetMapSettings().Providers.Single(layer => layer.Id == vector.Id).StyleDirectory);
        Assert.False(Directory.Exists(Path.Combine(env.Root, "map-styles")));
        var raster = store.SaveMapProvider(vector.Id, vector with { Format = "raster" });
        Assert.Empty(raster.StyleDirectory);
        Assert.False(raster.HasStyle);
    }

    [Fact]
    public void EachLayerHigherZoomDepthZeroToThreePersistsWithoutSchemaChanges()
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
                Preload = 2
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
            provider with { Name = "" }, provider with { Scheme = "other" },
            provider with { Format = "unknown" }, provider with { Preload = 4 },
            provider with { Format = "pmtiles", Folder = "archive", Scheme = "xyz" }
        })
        {
            Assert.Equal(400, Assert.Throws<ApiException>(() => store.SaveMapProvider(provider.Id, invalid)).StatusCode);
            Assert.Equal(provider, Assert.Single(store.GetMapSettings().Providers));
        }
    }

    [Fact]
    public async Task LegacyOnlineColumnsRemainCompatibleButCannotBeReactivated()
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var original = Assert.Single(store.GetMapSettings().Providers);
        const string oldUrl = "https://example.invalid/{z}/{x}/{y}?key={key}";
        // Simulate a database row saved by the former online-enabled version.
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE map_providers SET url_template=$url,api_key=$key,map_key=$map,force_refresh=1
                WHERE id=$id;
                """;
            update.Parameters.AddWithValue("$url", "hb-preload-2+" + oldUrl);
            update.Parameters.AddWithValue("$key", "historic-service-secret");
            update.Parameters.AddWithValue("$map", "historic-map-secret");
            update.Parameters.AddWithValue("$id", original.Id);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        var existing = Assert.Single(store.GetMapSettings().Providers);
        Assert.Equal(oldUrl, existing.UrlTemplate);
        Assert.True(existing.ForceRefresh);
        Assert.Equal(2, existing.Preload);
        var check = await store.CheckMapLayerAsync(existing.Id, CancellationToken.None);
        Assert.Equal("empty", check.Status);
        Assert.False(check.HasUrl);

        var edited = store.SaveMapProvider(existing.Id, existing with
        {
            Folder = "offline-after-upgrade", Name = "只读本地旧图层",
            // New online settings sent by clients are ignored, not written.
            UrlTemplate = "https://different.invalid/new", ApiKey = "", MapKey = "", ForceRefresh = false
        });
        Assert.Equal(oldUrl, edited.UrlTemplate);
        Assert.Equal("historic-service-secret", edited.ApiKey);
        Assert.Equal("historic-map-secret", edited.MapKey);
        Assert.True(edited.ForceRefresh);
        Assert.Equal(2, edited.Preload);

        var created = store.CreateMapProvider(edited with
        {
            Id = "", Name = "新建离线图层", Folder = "local-only",
            UrlTemplate = "https://example.invalid/never-fetch", ApiKey = "inert", ForceRefresh = true
        });
        Assert.Equal("", created.UrlTemplate);
        Assert.Equal("", created.ApiKey);
        Assert.False(created.ForceRefresh);
        using var reopened = env.Open();
        var persisted = reopened.GetMapSettings().Providers.Single(item => item.Id == edited.Id);
        Assert.Equal(oldUrl, persisted.UrlTemplate);
        Assert.Equal(2, persisted.Preload);
        Assert.Equal("historic-service-secret", persisted.ApiKey);
        Assert.False((await reopened.CheckMapLayerAsync(edited.Id, CancellationToken.None)).HasUrl);
        V2Environment.AssertForeignKeys(store.DatabasePath);
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
