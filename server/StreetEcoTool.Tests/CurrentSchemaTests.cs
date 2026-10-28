using System.Security.Cryptography;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class CurrentSchemaTests
{
    private static string[] Fingerprint(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
        .Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void EmptyDatabaseInitializesCurrentSchemaOnceAndReopensWithoutChangingData()
    {
        using var environment = new V2Environment();
        string pointId;
        using (var store = environment.Open())
        {
            Assert.Equal(Store.SchemaVersion, V2Environment.Number(store.DatabasePath, "PRAGMA user_version;"));
            Assert.Equal(Store.DatabaseApplicationId, V2Environment.Number(store.DatabasePath, "PRAGMA application_id;"));
            Assert.Equal(7, store.GetCampaignTypes().Length);
            Assert.Single(store.GetActivityTypes(), type => type.Template == "remediation" && type.BuiltIn);
            Assert.Equal(0, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('campaigns','campaign_types');"));
            Assert.Equal(5, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('activities','activity_remediations','matters','tags','attachments');"));
            var point = environment.CreatePoint(store); pointId = point.Id;
            var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
            store.AttachPointPhotos(point.Id, new([photo.Id]));
            File.WriteAllText(Path.Combine(store.BackupDir, "before-manual.db"), "preserved");
        }
        var before = Fingerprint(environment.Root);
        using (var reopened = environment.Open())
        {
            Assert.Equal(1, reopened.GetPoint(pointId).PhotoCount);
            V2Environment.AssertForeignKeys(reopened.DatabasePath);
        }
        Assert.Equal(before, Fingerprint(environment.Root));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(0)]
    public void NoncurrentOrUnversionedPopulatedDatabaseIsRefusedWithoutChanges(int version)
    {
        using var environment = new V2Environment();
        using (var store = environment.Open())
        {
            environment.CreatePoint(store);
            store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "site");
        }
        var db = Path.Combine(environment.Data, "app.db");
        V2Environment.Execute(db, "PRAGMA user_version=" + version + ";");
        var before = Fingerprint(environment.Root);
        Assert.Equal(409, Assert.Throws<ApiException>(() => environment.Open()).StatusCode);
        Assert.Equal(before, Fingerprint(environment.Root));
    }

    [Theory]
    [InlineData("photos", true)]
    [InlineData("backup", true)]
    [InlineData(".temporary", true)]
    [InlineData(".restore-staging", true)]
    [InlineData(".restore-journal.json", false)]
    [InlineData(".restore-journal.json.tmp", false)]
    public void UnsupportedLayoutNeverMovesOrDeletesExistingFiles(string name, bool directory)
    {
        using var environment = new V2Environment();
        using (var store = environment.Open()) environment.CreatePoint(store);
        var path = Path.Combine(environment.Data, name);
        if (directory) { Directory.CreateDirectory(path); path = Path.Combine(path, "preserve"); }
        File.WriteAllText(path, "do not discard");
        var before = Fingerprint(environment.Root);
        Assert.Throws<ApiException>(() => environment.Open());
        Assert.Throws<ApiException>(() => environment.Open());
        Assert.Equal(before, Fingerprint(environment.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CurrentBackupPreviewIncludesRemediationCountAndRestoresUnifiedActivities(int count)
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        for (var i = 0; i < count; i++) store.SaveCampaign(null, new("活动" + i, store.GetCampaignTypes()[0].Id, point.Id, Store.Today, "内容"));
        using var input = File.OpenRead(store.CreateBackupZip());
        var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
        Assert.Equal(count, preview.RemediationCount);
        Assert.Equal(Store.SchemaVersion, preview.SchemaVersion);
        var result = store.Restore(new(preview.Token, true));
        Assert.True(result.Ok);
        Assert.Equal(count, store.ListCampaigns(new()).Total);
        Assert.True(Directory.Exists(Path.Combine(store.RootDir, result.PreservedBackup)));
    }
}
