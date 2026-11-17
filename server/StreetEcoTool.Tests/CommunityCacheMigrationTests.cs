using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CommunityCacheMigrationTests
{
    [Fact]
    public async Task CacheImportCopiesSourceAndPublishesWorkingTmsLayer()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var source = Path.Combine(env.Root, "source"); var directory = Path.Combine(source, "2", "1"); Directory.CreateDirectory(directory);
        var bytes = WordExporterTests.Jpeg(32, 32); File.WriteAllBytes(Path.Combine(directory, "1.jpg"), bytes);
        var preview = store.PreviewCacheImport(source); Assert.Equal(1, preview.Files);
        var request = new CacheStartRequest(preview.Token, "测试影像", "tms"); var job = store.StartCacheImport(request);
        for (var i = 0; i < 300 && job.State == "copying"; i++) { await Task.Delay(10); job = store.CacheImportStatus(job.Id); }
        Assert.Equal("complete", job.State); Assert.NotNull(job.LayerId);
        Assert.Equal(job.Id, store.StartCacheImport(request).Id);
        var target = store.FindTile(job.LayerId!, 2, 1, 2); Assert.NotNull(target); Assert.Equal(bytes, File.ReadAllBytes(target!));
        Assert.True(File.Exists(Path.Combine(directory, "1.jpg")));
        var layer = store.GetMapLayers().Single(x => x.Id == job.LayerId);
        store.SaveMapLayer(layer with { IsDefault = true }); Assert.True(store.GetMapLayers().Single(x => x.Id == "jlyh").Enabled);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void OtherVersionsAreRejectedWithoutChangingData(int version)
    {
        using var env = new V2Environment(); string path;
        using (var store = env.Open()) { env.CreatePoint(store); path = store.DatabasePath; }
        V2Environment.Execute(path, $"PRAGMA user_version={version};");
        Assert.Throws<ApiException>(() => env.Open());
        Assert.Equal(version, V2Environment.Number(path, "PRAGMA user_version;"));
        Assert.Equal(1, V2Environment.Number(path, "SELECT COUNT(*) FROM points;"));
    }

    [Fact]
    public void ReopeningDoesNotRewriteUserConfiguration()
    {
        using var env = new V2Environment(); string path;
        using (var store = env.Open()) { path = store.DatabasePath; }
        V2Environment.Execute(path, "UPDATE map_layers SET sort_order=123 WHERE id='tdt-cva'; UPDATE community_records SET name='自定义商店' WHERE id='purpose-shop';");
        using var reopened = env.Open();
        Assert.Equal(123, reopened.GetMapLayers().Single(x => x.Id == "tdt-cva").SortOrder);
        Assert.Equal("自定义商店", reopened.ListCommunityRecords("purpose-type").Single(x => x.Id == "purpose-shop").Name);
        Assert.Equal(Store.SchemaVersion, V2Environment.Number(path, "PRAGMA user_version;"));
    }
}
