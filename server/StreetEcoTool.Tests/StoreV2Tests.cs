using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class StoreV2Tests
{
    [Fact]
    public void RealVersion1DatabaseMigratesWithoutLosingHistoryPhotosOrUnattachedRows()
    {
        using var environment = new V2Environment();
        environment.CopyVersion1(legacyLayout: true);
        using (var store = new Store(environment.Data))
        {
            Assert.Equal(2L, V2Environment.Number(store.DatabasePath, "PRAGMA user_version;"));
            var detail = store.GetInspectionDetail("inspection-v1");
            Assert.Equal("旧版餐饮点", detail.Inspection.PointName);
            Assert.Equal("旧编号-001", detail.Inspection.NoticeNumber);
            Assert.Equal("油烟净化设施未运行。", Assert.Single(detail.Issues).Description);
            Assert.Equal("已完成维修。", Assert.Single(Assert.Single(detail.IssueDetails!).Actions).Notes);
            Assert.Equal(3, detail.Photos.Length);
            Assert.Equal(4L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos;"));
            foreach (var id in V2Environment.LegacyPhotoIds)
            {
                using var stream = store.OpenPhoto(id);
                Assert.True(stream.Length > 100);
                using var thumbFallback = store.OpenPhoto(id, true);
                Assert.Equal(stream.Length, thumbFallback.Length);
            }
            V2Environment.AssertForeignKeys(store.DatabasePath);
            var backup = Assert.Single(Directory.EnumerateFiles(store.BackupDir, "before-migrate-v1-*.db"));
            Assert.Equal(1L, V2Environment.Number(backup, "PRAGMA user_version;"));
            Assert.Equal(4L, V2Environment.Number(backup, "SELECT COUNT(*) FROM photos;"));
            Assert.False(Directory.Exists(Path.Combine(environment.Data, "photos")));
            Assert.All(Directory.EnumerateFiles(environment.Data), file => Assert.StartsWith("app.db", Path.GetFileName(file), StringComparison.Ordinal));
        }
        using var reopened = new Store(environment.Data);
        Assert.Single(Directory.EnumerateFiles(reopened.BackupDir, "before-migrate-v1-*.db"));
        Assert.Equal("feedback", reopened.GetIssue("issue-v1").Status);
    }

    [Fact]
    public void FailedMigrationRollsBackSchemaAndKeepsItsVersion1Snapshot()
    {
        using var environment = new V2Environment();
        environment.CopyVersion1(false);
        V2Environment.Execute(Path.Combine(environment.Data, "app.db"), "PRAGMA foreign_keys=OFF; DELETE FROM communities;");
        var error = Assert.Throws<IOException>(() => new Store(environment.Data));
        Assert.Contains("已回滚", error.Message);
        Assert.Equal(1L, V2Environment.Number(Path.Combine(environment.Data, "app.db"), "PRAGMA user_version;"));
        Assert.Equal(0L, V2Environment.Number(Path.Combine(environment.Data, "app.db"), "SELECT COUNT(*) FROM sqlite_master WHERE name='campaigns';"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(environment.Root, "backup"), "before-migrate-v1-*.db"));
        Assert.Equal(4L, V2Environment.Number(Path.Combine(environment.Data, "app.db"), "SELECT COUNT(*) FROM photos;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDirectoryMergeIsRepeatableAndPreservesCollidingFiles(bool bothLayouts)
    {
        using var environment = new V2Environment();
        var old = Path.Combine(environment.Data, "photos", "202610");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "same.jpg"), "old-original");
        File.WriteAllText(Path.Combine(old, "unique.jpg"), "unique-original");
        var target = Path.Combine(environment.Root, "photos", "202610");
        if (bothLayouts) { Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "same.jpg"), "new-original"); }
        Directory.CreateDirectory(Path.Combine(environment.Data, "backup"));
        File.WriteAllText(Path.Combine(environment.Data, "backup", "before-old.db"), "backup");
        Directory.CreateDirectory(Path.Combine(environment.Data, ".temporary"));
        File.WriteAllText(Path.Combine(environment.Data, ".temporary", "draft.tmp"), "draft");
        Store.MigrateLegacyLayout(environment.Data);
        Store.MigrateLegacyLayout(environment.Data);
        Assert.Equal("unique-original", File.ReadAllText(Path.Combine(target, "unique.jpg")));
        Assert.Equal(bothLayouts ? "new-original" : "old-original", File.ReadAllText(Path.Combine(target, "same.jpg")));
        if (bothLayouts) Assert.Equal("old-original", File.ReadAllText(Path.Combine(old, "same.jpg")));
        else Assert.False(Directory.Exists(Path.Combine(environment.Data, "photos")));
        Assert.Equal("backup", File.ReadAllText(Path.Combine(environment.Root, "backup", "before-old.db")));
        Assert.Equal("draft", File.ReadAllText(Path.Combine(environment.Root, "temp", "draft.tmp")));
    }

    [Fact]
    public void NewLayoutDoesNotMoveOrOverwriteExistingContent()
    {
        using var environment = new V2Environment();
        var photos = Path.Combine(environment.Root, "photos");
        Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(photos, "existing.txt"), "original");
        Store.MigrateLegacyLayout(environment.Data);
        Assert.Equal("original", File.ReadAllText(Path.Combine(photos, "existing.txt")));
        Assert.False(Directory.Exists(Path.Combine(environment.Data, "photos")));
    }

    [Fact]
    public void PreviousDaySnapshotIsImmutableAndExcludesLaterWrites()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        store.SaveSettings(store.GetSettings() with { StreetName = "午夜前" });
        var today = new DateOnly(2026, 10, 13);
        store.EnsurePreviousDaySnapshot(today);
        store.SaveSettings(store.GetSettings() with { StreetName = "午夜后" });
        store.EnsurePreviousDaySnapshot(today);
        var snapshot = Assert.Single(store.GetBackups().Items);
        Assert.Equal("2026-10-12", snapshot.Date);
        Assert.Equal("午夜前", V2Environment.Scalar(Path.Combine(store.BackupDir, snapshot.Name), "SELECT street_name FROM settings;"));
        Assert.Equal("午夜后", store.GetSettings().StreetName);
    }

    [Fact]
    public void MissedMidnightStartupCreatesOnlyYesterdayAndRetainsFourteenAutomaticSnapshots()
    {
        using var environment = new V2Environment();
        using (var beforeShutdown = environment.Open()) beforeShutdown.SaveSettings(beforeShutdown.GetSettings() with { StreetName = "关机前" });
        using var store = environment.Open();
        store.RunDailyMaintenance(new DateOnly(2026, 10, 25));
        Assert.Equal("2026-10-24", Assert.Single(store.GetBackups().Items).Date);
        Assert.Equal("关机前", V2Environment.Scalar(Path.Combine(store.BackupDir, "auto-2026-10-24.db"), "SELECT street_name FROM settings;"));
        var preserved = Path.Combine(store.BackupDir, "before-restore-manual.db");
        File.WriteAllText(preserved, "preserve");
        for (var day = 1; day <= 25; day++) store.EnsurePreviousDaySnapshot(new DateOnly(2026, 11, day));
        Assert.Equal(14, store.GetBackups().Items.Length);
        Assert.True(File.Exists(preserved));
        Assert.Equal("2026-11-24", store.GetBackups().Items[0].Date);
    }

    [Fact]
    public void TemporaryPointAndInspectionCommitTogetherOrRollbackTogether()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var community = store.SaveCommunity(null, new("社区", "社区居民委员会"));
        var point = new PointWriteRequest("临时点位", community.Id, store.GetPointTypes()[0].Id, "地点", Frequency: "weekly", Longitude: 88.12345678, Latitude: 29.12345678);
        Assert.Throws<ApiException>(() => store.CreateInspection(new(null, Store.Today, "检查人", "issues", Issues: [new("问题", "整改", Store.Today, PhotoIds: ["missing"])], NewPoint: point)));
        Assert.Empty(store.ListPoints(new(Temporary: "all")).Items);
        Assert.Empty(store.ListInspections(new()).Items);
        var inspection = store.CreateInspection(new(null, Store.Today, "检查人", "normal", NewPoint: point));
        Assert.True(inspection.Inspection.PointTemporary);
        Assert.Equal("irregular", store.GetPoint(inspection.Inspection.PointId).Frequency);
        Assert.Equal(88.123457, inspection.Inspection.Longitude);
        Assert.Empty(store.ListPoints(new()).Items);
        Assert.Single(store.ListPoints(new(Temporary: "true")).Items);
        var promoted = store.PromotePoint(inspection.Inspection.PointId, new("monthly"));
        Assert.False(promoted.Temporary);
        Assert.Equal("monthly", promoted.Frequency);
        Assert.Single(store.ListPoints(new()).Items);
        Assert.False(store.GetInspection(inspection.Inspection.Id).PointTemporary);
    }

    [Fact]
    public void RemovingTheFinalTemporaryInspectionAlsoRemovesItsPointAndEveryPhoto()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var community = store.SaveCommunity(null, new("社区", "社区居民委员会"));
        var site = store.UploadPhoto(WordExporterTests.Jpeg(200, 100), "site");
        store.UploadThumbnail(site.Id, WordExporterTests.Jpeg(100, 50));
        var pointPhoto = store.UploadPhoto(WordExporterTests.Jpeg(200, 100), "point");
        var inspection = store.CreateInspection(new(null, Store.Today, "检查人", "issues", Issues: [new("问题", "整改", Store.Today, PhotoIds: [site.Id])],
            NewPoint: new("临时点位", community.Id, store.GetPointTypes()[0].Id, "地点", PhotoIds: [pointPhoto.Id])));
        var second = store.CreateInspection(new(inspection.Inspection.PointId, Store.Today, "检查人", "normal"));
        store.DeleteInspection(second.Inspection.Id);
        Assert.True(store.GetPoint(inspection.Inspection.PointId).Temporary);
        var after = store.UploadPhoto(WordExporterTests.Jpeg(200, 100), "after");
        store.RecordInspectionFeedback(inspection.Inspection.Id, new(Store.Today, [new(inspection.Issues[0].Id, "已整改", [after.Id])]));
        store.DeleteInspection(inspection.Inspection.Id);
        Assert.Equal(0L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM points;"));
        Assert.Equal(0L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM issue_actions;"));
        Assert.Equal(0L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos;"));
        Assert.Empty(Directory.EnumerateFiles(store.PhotosDir, "*", SearchOption.AllDirectories));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void BatchFeedbackAndReviewRollbackEveryIssueActionAndPhotoOnLaterFailure()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var detail = store.CreateInspection(new(point.Id, Store.Today, "检查人", "issues", Issues: [new("问题1", "整改1", Store.Today), new("问题2", "整改2", Store.Today)]));
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(200, 100), "after");
        Assert.Throws<ApiException>(() => store.RecordInspectionFeedback(detail.Inspection.Id, new(Store.Today,
            [new(detail.Issues[0].Id, "第一项完成", [photo.Id]), new(detail.Issues[1].Id, "第二项完成", ["missing"])])));
        var unchanged = store.GetInspectionDetail(detail.Inspection.Id);
        Assert.All(unchanged.Issues, issue => Assert.Equal("pending", issue.Status));
        Assert.All(unchanged.IssueDetails!, issue => Assert.Empty(issue.Actions));
        Assert.Equal(0L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos WHERE issue_id IS NOT NULL;"));
        var feedback = store.RecordInspectionFeedback(detail.Inspection.Id, new(Store.Today,
            [new(detail.Issues[0].Id, "第一项完成", [photo.Id]), new(detail.Issues[1].Id, "第二项完成")]));
        Assert.Equal("feedback", feedback.Inspection.RectifyStatus);
        var reviewPhoto = store.UploadPhoto(WordExporterTests.Jpeg(200, 100), "review");
        Assert.Throws<ApiException>(() => store.ReviewInspection(detail.Inspection.Id, new(Store.Today,
            [new(detail.Issues[0].Id, "通过", [reviewPhoto.Id]), new(detail.Issues[1].Id, "通过", ["missing"])])));
        Assert.All(store.GetInspectionDetail(detail.Inspection.Id).Issues, issue => Assert.Equal("feedback", issue.Status));
        Assert.Equal(2L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM issue_actions;"));
        var closed = store.ReviewInspection(detail.Inspection.Id, new(Store.Today,
            [new(detail.Issues[0].Id, "通过", [reviewPhoto.Id]), new(detail.Issues[1].Id, "通过")]));
        Assert.Equal("closed", closed.Inspection.RectifyStatus);
        Assert.Equal(2, closed.Inspection.ClosedCount);
        Assert.Throws<ApiException>(() => store.RecordInspectionFeedback(detail.Inspection.Id, new(Store.Today, [new(detail.Issues[0].Id, "重复"), new(detail.Issues[0].Id, "重复")])));
    }

    [Fact]
    public void CountsAndProblemFiltersReportInspectionsAndMatchingIssuesSeparately()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        store.CreateInspection(new(point.Id, yesterday, "检查人", "issues", Issues:
            [new("逾期1", "整改", yesterday), new("逾期2", "整改", yesterday), new("即将到期", "整改", Store.Today)]));
        store.CreateInspection(new(point.Id, Store.Today, "检查人", "normal"));
        var overdue = store.ListInspections(new(Overdue: true));
        Assert.Single(overdue.Items);
        Assert.Equal(2, overdue.MatchingIssueCount);
        Assert.Equal(2, overdue.Items[0].OverdueCount);
        Assert.Equal(1, store.ListInspections(new(DueSoon: true)).MatchingIssueCount);
        var counts = store.GetInspectionCounts(new(Rectify: "closed"));
        Assert.Equal(2, counts.All);
        Assert.Equal(1, counts.None);
        Assert.Equal(1, counts.Pending);
        Assert.Equal(2, store.GetDashboard().Overdue.Count);
        Assert.Single(store.GetDashboard().Todos!);
    }

    [Fact]
    public void PointPhotosAreCappedAndAssociatedOldPhotosAreNeverOrphanCleaned()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var photos = Enumerable.Range(0, 10).Select(_ => store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point")).ToArray();
        var update = new PointWriteRequest(point.Name, point.CommunityId, point.TypeId, point.Address, PhotoIds: photos.Take(9).Select(photo => photo.Id).ToArray());
        var saved = store.SavePoint(point.Id, update);
        Assert.Equal(9, saved.PhotoCount);
        Assert.Equal(photos[0].Id, saved.CoverPhotoId);
        Assert.Throws<ApiException>(() => store.SavePoint(point.Id, update with { PhotoIds = [photos[9].Id] }));
        V2Environment.Execute(store.DatabasePath, "UPDATE photos SET uploaded_at='2000-01-01T00:00:00';");
        store.CleanupOrphanPhotos();
        Assert.Equal(9, store.GetPoint(point.Id).PhotoCount);
        Assert.Throws<ApiException>(() => store.OpenPhoto(photos[9].Id));
        store.DeletePoint(point.Id);
        Assert.Empty(Directory.EnumerateFiles(store.PhotosDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BackupIncludesThumbnailsButPreviewCountsOnlyOriginalsAndRestoresVersion1()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var photo = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "point");
        store.UploadThumbnail(photo.Id, WordExporterTests.Jpeg(60, 40));
        var archive = store.CreateBackupZip();
        using (var zip = ZipFile.OpenRead(archive)) Assert.Single(zip.Entries.Where(entry => entry.FullName.EndsWith(".thumb.jpg", StringComparison.Ordinal)));
        await using (var input = File.OpenRead(archive))
        {
            var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
            Assert.Equal(1, preview.PhotoCount);
            Assert.Equal(2, preview.SchemaVersion);
            store.Restore(new(preview.Token, true));
        }
        using (var thumb = store.OpenPhoto(photo.Id, true))
        {
            using var bytes = new MemoryStream(); thumb.CopyTo(bytes);
            Assert.Equal(new JpegDimensions(60, 40), JpegMetadata.Read(bytes.ToArray()));
        }
        var legacy = environment.CreateVersion1Zip();
        await using (var input = File.OpenRead(legacy))
        {
            var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
            Assert.Equal(1, preview.SchemaVersion);
            Assert.Equal(4, preview.PhotoCount);
            store.Restore(new(preview.Token, true));
        }
        Assert.Equal(2L, V2Environment.Number(store.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal("旧版餐饮点", store.GetPoint("point-v1").Name);
        Assert.Equal(4L, V2Environment.Number(store.DatabasePath, "SELECT COUNT(*) FROM photos;"));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void CampaignValidatesNumbersSnapshotsDictionariesAndDeletesPhotos()
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        var type = store.GetCampaignTypes()[0];
        var before = store.UploadPhoto(WordExporterTests.Jpeg(120, 80), "before");
        var request = new CampaignWriteRequest("整治活动", type.Id, point.CommunityId, "整治地点", Store.Today, "清理路面", ParticipantCount: 5,
            VehicleCount: 1, GarbageTons: 1.235m, AreaSqm: 2.225m, BeforePhotoIds: [before.Id]);
        var saved = store.SaveCampaign(null, request);
        Assert.Equal(1.24m, saved.Campaign.GarbageTons);
        Assert.Equal(2.23m, saved.Campaign.AreaSqm);
        Assert.Single(saved.Photos);
        store.SaveCampaignType(type.Id, new("已改类型"));
        store.SaveCommunity(point.CommunityId, new("已改社区", "已改社区居民委员会"));
        var edited = store.SaveCampaign(saved.Campaign.Id, request with { Content = "编辑内容" });
        Assert.Equal(type.Name, edited.Campaign.TypeName);
        Assert.Equal(point.CommunityName, edited.Campaign.CommunityName);
        Assert.Throws<ApiException>(() => store.DeleteCampaignType(type.Id));
        Assert.Throws<ApiException>(() => store.DeleteCommunity(point.CommunityId));
        Assert.Throws<ApiException>(() => store.SaveCampaign(null, request with { ParticipantCount = -1 }));
        Assert.Throws<ApiException>(() => store.SaveCampaign(null, request with { EndDate = "2000-01-01" }));
        Assert.Single(store.GetLedgerCampaigns(Store.Today, Store.Today));
        store.DeleteCampaign(saved.Campaign.Id);
        Assert.Empty(store.ListCampaigns(new()).Items);
        Assert.Throws<ApiException>(() => store.OpenPhoto(before.Id));
        store.DeleteCampaignType(type.Id);
    }

    [Fact]
    public void TemporaryPointsNeverNeedCyclicInspection()
    {
        var today = new DateOnly(2026, 10, 12);
        Assert.False(ReminderRules.NeedsInspection("weekly", false, (DateOnly?)null, today, temporary: true));
        Assert.False(ReminderRules.NeedsInspection("monthly", false, Array.Empty<DateOnly>(), today, temporary: true));
    }
}

internal sealed class V2Environment : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hbtool-v2-" + Guid.NewGuid().ToString("N"));
    public string Data => Path.Combine(Root, "data");
    public static string[] LegacyPhotoIds => Enumerable.Range(1, 4).Select(value => value.ToString("x32", CultureInfo.InvariantCulture)).ToArray();
    public V2Environment() => Directory.CreateDirectory(Data);
    public Store Open() => new(Data);
    public PointDto CreatePoint(Store store)
    {
        var community = store.SaveCommunity(null, new("社区", "社区居民委员会"));
        return store.SavePoint(null, new("测试点位", community.Id, store.GetPointTypes()[0].Id, "测试地址", Frequency: "weekly"));
    }
    public static string FixtureRoot
    {
        get
        {
            var current = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (current is not null)
            {
                var path = Path.Combine(current.FullName, "server", "StreetEcoTool.Tests", "Fixtures", "schema-v1");
                if (File.Exists(Path.Combine(path, "app.db"))) return path;
                current = current.Parent;
            }
            throw new IOException("找不到固定 v1 数据夹具。");
        }
    }
    public void CopyVersion1(bool legacyLayout)
    {
        File.Copy(Path.Combine(FixtureRoot, "app.db"), Path.Combine(Data, "app.db"));
        var target = Path.Combine(legacyLayout ? Data : Root, "photos", "202610");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "photos", "202610")))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
    public string CreateVersion1Zip()
    {
        var path = Path.Combine(Root, "v1-backup.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in new[] { "app.db", "manifest.json" }) zip.CreateEntryFromFile(Path.Combine(FixtureRoot, name), name);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FixtureRoot, "photos"), "*.jpg", SearchOption.AllDirectories))
            zip.CreateEntryFromFile(file, "photos/" + Path.GetRelativePath(Path.Combine(FixtureRoot, "photos"), file).Replace(Path.DirectorySeparatorChar, '/'));
        return path;
    }
    private static SqliteConnection Connect(string path, bool readOnly)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString());
        connection.Open(); return connection;
    }
    public static object? Scalar(string path, string sql)
    {
        using var connection = Connect(path, true);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
    public static long Number(string path, string sql) => Convert.ToInt64(Scalar(path, sql), CultureInfo.InvariantCulture);
    public static void Execute(string path, string sql)
    {
        using var connection = Connect(path, false);
        using var command = connection.CreateCommand(); command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    public static void AssertForeignKeys(string path)
    {
        using var connection = Connect(path, true);
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader(); Assert.False(reader.Read());
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
