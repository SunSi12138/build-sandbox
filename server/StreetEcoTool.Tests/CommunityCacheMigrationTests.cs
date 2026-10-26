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

    [Fact]
    public void VersionFiveUpgradePreservesDataAndMakesBeforeUpgradeBackup()
    {
        using var env = new V2Environment(); string id; string path;
        using (var store = env.Open()) { id = env.CreatePoint(store).Id; path = store.DatabasePath; }
        V2Environment.Execute(path, "DROP VIEW active_issues; DROP VIEW active_inspections; ALTER TABLE inspections DROP COLUMN voided; ALTER TABLE inspections DROP COLUMN void_reason; ALTER TABLE issues DROP COLUMN voided; ALTER TABLE issues DROP COLUMN void_reason; PRAGMA user_version=5;");
        using var updated = env.Open();
        Assert.Equal(id, updated.GetPoint(id).Id); Assert.Equal(6, V2Environment.Number(path, "PRAGMA user_version;"));
        Assert.Single(Directory.GetFiles(updated.BackupDir, "before-v6-*.db"));
        Assert.Equal(5, V2Environment.Number(Directory.GetFiles(updated.BackupDir, "before-v6-*.db")[0], "PRAGMA user_version;"));
    }
}
