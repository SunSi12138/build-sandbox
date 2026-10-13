from pathlib import Path
import re
import shutil


def edit(path, old, new, count=1):
    p = Path(path); text = p.read_text()
    assert text.count(old) == count, (path, old[:100], text.count(old))
    p.write_text(text.replace(old, new))


# Generate the direct CURRENT DDL from the two previously verified definitions.
# Only this temporary development script performs this transformation; runtime
# initialization contains one explicit transaction and no migration engine.
p = Path('server/Database.cs'); s = p.read_text()
a = s.index('    private void MigrateToVersion1()')
b = s.index('    // Called at the actual mutation boundary')
old = s[a:b]
v1 = re.search(r'Command\("""\n(.*?)\n            """, transaction\)', old, re.S).group(1)
v2 = re.search(r'CREATE TABLE campaign_types.*?PRAGMA user_version=2;', old, re.S).group(0)
photo_table = re.search(r'CREATE TABLE photos_v2 \(.*?\n                \);', v2, re.S).group(0).replace('photos_v2', 'photos')
campaign_tables = v2[:v2.index('                CREATE TABLE photos_v2')].rstrip()
v1 = v1.replace("notes TEXT NOT NULL DEFAULT '', disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1))", "notes TEXT NOT NULL DEFAULT '', disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1)),\n                longitude REAL CHECK(longitude IS NULL OR longitude BETWEEN -180 AND 180),\n                latitude REAL CHECK(latitude IS NULL OR latitude BETWEEN -90 AND 90),\n                temporary INTEGER NOT NULL DEFAULT 0 CHECK(temporary IN (0,1))")
v1 = re.sub(r'CREATE TABLE photos \(.*?\n            \);', lambda _: campaign_tables + '\n            ' + photo_table, v1, flags=re.S)
v1 = v1.replace('WHERE inspection_id IS NULL AND issue_id IS NULL;', 'WHERE inspection_id IS NULL AND issue_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;')
v1 = v1.replace('            PRAGMA application_id=', '            CREATE INDEX ix_photos_point ON photos(point_id);\n            CREATE INDEX ix_photos_campaign ON photos(campaign_id);\n            CREATE INDEX ix_campaigns_date ON campaigns(campaign_date,community_id,type_id);\n            CREATE INDEX ix_points_temporary ON points(temporary,disabled);\n            PRAGMA application_id=')
v1 = v1.replace('PRAGMA user_version=1;', 'PRAGMA user_version=2;')
assert 'photos_v2' not in v1 and 'DROP TABLE' not in v1 and 'ALTER TABLE' not in v1
seed = old[old.index('        using (var settings = Command'):old.index('        transaction.Commit();')]
new = '''    private void InitializeCurrentDatabase()
    {
        using var transaction = _db.BeginTransaction();
        using (var create = Command("""
''' + v1 + '''
            """, transaction)) create.ExecuteNonQuery();
''' + seed + '''        foreach (var name in new[] { "卫生大扫除", "垃圾清运", "河道清理", "乱堆乱放清理", "小广告清除", "绿化补种", "其他" })
        {
            using var command = Command("INSERT INTO campaign_types(id,name) VALUES($id,$name);", transaction, ("$id", NewId()), ("$name", name));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // Refuse unsupported input before enabling WAL or running any mutation.
    // A schema version of zero is only an empty SQLite database, never an
    // invitation to recreate tables in an unrelated existing database.
    private bool IsEmptyOrValidateCurrentDatabase()
    {
        if (!File.Exists(DatabasePath) || new FileInfo(DatabasePath).Length == 0) return true;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        static long Number(SqliteConnection db, string sql)
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var version = Number(connection, "PRAGMA user_version;");
        var application = Number(connection, "PRAGMA application_id;");
        if (version == 0 && application == 0 && Number(connection, "SELECT COUNT(*) FROM sqlite_master;") == 0) return true;
        if (version != SchemaVersion || application != DatabaseApplicationId)
            throw new ApiException(409, "数据库版本或标识不受支持。现有文件未被重建，请保留数据并使用对应版本的程序。");
        foreach (var sql in RequiredSchemaQueries)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader();
        }
        return false;
    }

    private static void RejectUnsupportedLayout(string dataDir)
    {
        foreach (var name in new[] { "photos", "backup", ".temporary", ".restore-staging", ".restore-journal.json", ".restore-journal.json.tmp" })
        {
            var path = Path.Combine(dataDir, name);
            if (File.Exists(path) || Directory.Exists(path))
                throw new ApiException(409, $"检测到不支持的旧版目录或恢复日志：{path}。已停止启动，未移动或删除任何文件。");
        }
    }

'''
s = s[:a] + new + s[b:]
s = s.replace('        Directory.CreateDirectory(DataDir);\n        RecoverInterruptedRestore(DataDir);\n        MigrateLegacyLayout(DataDir);', '''        RejectUnsupportedLayout(DataDir);
        RecoverInterruptedRestore(DataDir);
        if (!File.Exists(DatabasePath) && Directory.Exists(PhotosDir) && Directory.EnumerateFileSystemEntries(PhotosDir).Any())
            throw new ApiException(409, "照片目录已有内容但缺少数据库。已停止启动，请保留文件并检查完整备份。");
        Directory.CreateDirectory(DataDir);''')
s = s.replace('    internal void ReopenConnection()\n    {\n', '    internal void ReopenConnection()\n    {\n        var initialize = IsEmptyOrValidateCurrentDatabase();\n')
start = s.index('        using var versionCommand = Command(')
end = s.index('\n    }', start)
s = s[:start] + '        if (initialize) InitializeCurrentDatabase();' + s[end:]
p.write_text(s)

p = Path('server/BackupStore.cs'); s = p.read_text()
s = s.replace('int IssueCount, long ExpandedBytes, string ExpiresAt);', 'int IssueCount, long ExpandedBytes, string ExpiresAt, int CampaignCount);')
s = s.replace('version < 1 || version > SchemaVersion', 'version != SchemaVersion')
start = s.index('        if (version >= 2)\n')
end = s.index('        using (var dates = connection.CreateCommand())', start)
s = s[:start] + s[end:]
s = s.replace('            if (version >= 2) dates.CommandText = dates.CommandText.TrimEnd(\';\') + " UNION ALL SELECT campaign_date FROM campaigns UNION ALL SELECT end_date FROM campaigns;";', '            dates.CommandText = dates.CommandText.TrimEnd(\';\') + " UNION ALL SELECT campaign_date FROM campaigns UNION ALL SELECT end_date FROM campaigns;";')
s = s.replace('photos.CommandText = version >= 2 ? "SELECT id,relative_path,width,height,has_thumb FROM photos;" : "SELECT id,relative_path,width,height,0 FROM photos;";', 'photos.CommandText = "SELECT id,relative_path,width,height,has_thumb FROM photos;";')
s = s.replace('DateTime.Now.AddMinutes(30).ToString("O", CultureInfo.InvariantCulture));', 'DateTime.Now.AddMinutes(30).ToString("O", CultureInfo.InvariantCulture), checked((int)Number(connection, "SELECT count(*) FROM campaigns;")));')
s = s.replace('address,contact,phone,frequency,notes,disabled FROM points LIMIT 0;', 'address,contact,phone,frequency,notes,disabled,longitude,latitude,temporary FROM points LIMIT 0;')
s = s.replace('id,inspection_id,issue_id,action_id,phase,relative_path,width,height,uploaded_at FROM photos LIMIT 0;"', 'id,inspection_id,issue_id,action_id,point_id,campaign_id,phase,relative_path,width,height,uploaded_at,has_thumb FROM photos LIMIT 0;",\n        "SELECT id,name FROM campaign_types LIMIT 0;",\n        "SELECT id,name,type_id,type_name,community_id,community_name,location,campaign_date,end_date,organizer,participants,participant_count,vehicle_count,garbage_tons,area_sqm,content,outcome,notes FROM campaigns LIMIT 0;"')
s = s.replace('        var legacyJournal = Path.Combine(dataDir, ".restore-journal.json");\n        var legacy = File.Exists(legacyJournal);\n        var journalPath = legacy ? legacyJournal : Path.Combine(root, "temp", "restore-journal.json");', '        RejectUnsupportedLayout(dataDir);\n        var journalPath = Path.Combine(root, "temp", "restore-journal.json");')
s = s.replace('legacy ? dataDir : root', 'root')
assert 'version >= 2' not in s and 'legacy' not in s
p.write_text(s)

# Replace only historical migration assertions. Preserve the same mixed file's
# midnight, atomic temporary-point, deletion, batch and current restore tests.
p = Path('server/StreetEcoTool.Tests/StoreV2Tests.cs'); s = p.read_text()
start = s.index('    [Fact]')
end = s.index('    [Fact]\n    public void PreviousDaySnapshot', start)
s = s[:start] + s[end:]
s = s.replace('BackupIncludesThumbnailsButPreviewCountsOnlyOriginalsAndRestoresVersion1', 'BackupIncludesThumbnailsButPreviewCountsOnlyOriginals')
start = s.index('        var legacy = environment.CreateVersion1Zip();')
end = s.index('        V2Environment.AssertForeignKeys(store.DatabasePath);', start)
s = s[:start] + s[end:]
s = re.sub(r'    public static string\[\] LegacyPhotoIds.*\n', '', s)
start = s.index('    public static string FixtureRoot')
end = s.index('    private static SqliteConnection Connect', start)
s = s[:start] + s[end:]
p.write_text(s)

p = Path('tests/api-smoke.py'); s = p.read_text()
s = s.replace('for legacy, committed in ((False, False), (False, True), (True, False), (True, True)):', 'for committed in (False, True):')
s = s.replace('storage_root = self.data if legacy else self.install', 'storage_root = self.install')
s = s.replace('            if legacy:\n                for name in ("photos", "backup"):\n                    (self.install / name).rename(self.data / name)\n', '')
s = s.replace('("legacy-" if legacy else "v2-")', '"v2-"')
s = s.replace('self.data / ".restore-journal.json" if legacy else self.install / "temp" / "restore-journal.json"', 'self.install / "temp" / "restore-journal.json"')
start = s.index('    def test_legacy_upgrade_and_restore(')
end = s.index('    def run(self)', start)
s = s[:start] + '''    def test_unsupported_data_is_unchanged(self) -> None:
        require(self.process is None, "Unsupported fixtures require a stopped test server")
        database = self.data / "app.db"
        original = database.read_bytes()
        def hashes():
            return {str(path.relative_to(self.install)): hashlib.sha256(path.read_bytes()).hexdigest()
                    for folder in (self.data, self.install / "photos", self.install / "backup", self.install / "temp")
                    for path in folder.rglob("*") if path.is_file()}
        def rejected():
            before = hashes()
            result = subprocess.run([str(self.executable), "--no-browser", "--port", "0"], cwd=self.temp,
                                    capture_output=True, timeout=15)
            # Port zero would fail argument validation without exercising Store;
            # use a valid isolated port instead below.
            result = subprocess.run([str(self.executable), "--no-browser", "--port", str(self.port)], cwd=self.temp,
                                    capture_output=True, timeout=15)
            require(result.returncode != 0, "Unsupported data started successfully")
            require(hashes() == before, "Refused data or photos were modified")
            text = (result.stdout + result.stderr).decode("utf-8", errors="replace")
            require("不支持" in text or "不受支持" in text, "Unsupported data needs an explicit explanation")
        for version in (1, 3):
            with contextlib.closing(sqlite3.connect(database)) as connection:
                connection.execute(f"PRAGMA user_version={version}")
                connection.commit()
            rejected()
            database.write_bytes(original)
        old = self.data / "photos"
        old.mkdir()
        (old / "preserve.jpg").write_bytes(b"do-not-delete")
        rejected()
        (old / "preserve.jpg").unlink(); old.rmdir()
        journal = self.data / ".restore-journal.json"
        journal.write_text('{"backupName":"before-restore-legacy","committed":false}')
        rejected(); journal.unlink()
        self.start()
        require(self.sql_rows("PRAGMA user_version")[0][0] == 2, "Current schema failed to reopen")
        self.stop()

''' + s[end:]
s = s.replace('            ("real version 1 database upgrade, old photo directories and version 1 backup restore", self.test_legacy_upgrade_and_restore),', '            ("unsupported versions and legacy layout are rejected without changing files", self.test_unsupported_data_is_unchanged),')
s = s.replace('            result = subprocess.run([str(self.executable), "--no-browser", "--port", "0"], cwd=self.temp,\n                                    capture_output=True, timeout=15)\n            # Port zero would fail argument validation without exercising Store;\n            # use a valid isolated port instead below.\n', '')
s = s.replace('require(preview["pointCount"] == len(baseline["database"]["points"]) and preview["photoCount"] == len(photo_rows),', 'require(preview["campaignCount"] == len(baseline["database"]["campaigns"]) and preview["pointCount"] == len(baseline["database"]["points"]) and preview["photoCount"] == len(photo_rows),')
p.write_text(s)

Path('server/StreetEcoTool.Tests/CurrentSchemaTests.cs').write_text('''using System.Security.Cryptography;
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
    [InlineData(1)]
    [InlineData(3)]
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
    public async Task CurrentBackupPreviewIncludesCampaignCountAndFormatOneRemainsAccepted(int count)
    {
        using var environment = new V2Environment();
        using var store = environment.Open();
        var point = environment.CreatePoint(store);
        for (var i = 0; i < count; i++) store.SaveCampaign(null, new("活动" + i, store.GetCampaignTypes()[0].Id, point.CommunityId, "地点", Store.Today, "内容"));
        using var input = File.OpenRead(store.CreateBackupZip());
        var preview = await store.PrepareRestoreAsync(input, CancellationToken.None);
        Assert.Equal(count, preview.CampaignCount);
        Assert.Equal(2, preview.SchemaVersion);
        var result = store.Restore(new(preview.Token, true));
        Assert.True(result.Ok);
        Assert.Equal(count, store.ListCampaigns(new()).Total);
        Assert.True(Directory.Exists(Path.Combine(store.RootDir, result.PreservedBackup)));
    }
}
''')

# Shared business rules: a single-item request is an atomic batch of one, not a
# second implementation of dates, state changes, action insertion and photos.
p = Path('server/Store.Issues.cs'); s = p.read_text()
start = s.index('    public IssueDetailDto RecordFeedback(')
end = s.index('    public IssueDetailDto ReturnIssue(', start)
s = s[:start] + '''    public IssueDetailDto RecordFeedback(string id, FeedbackRequest request)
    {
        lock (_gate)
        {
            var issue = GetIssue(id);
            RecordInspectionFeedback(issue.InspectionId, new(request.Date, [new(id, request.Notes, request.PhotoIds)]));
            return GetIssueDetail(id);
        }
    }

    public IssueDetailDto ReviewAndClose(string id, ReviewRequest request)
    {
        lock (_gate)
        {
            var issue = GetIssue(id);
            ReviewInspection(issue.InspectionId, new(request.Date, [new(id, request.Conclusion, request.PhotoIds)]));
            return GetIssueDetail(id);
        }
    }

''' + s[end:]
p.write_text(s)

# Migrate every in-repository caller before deleting old route aliases.
p = Path('server/WordStore.cs'); s = p.read_text()
s = s.replace('        app.MapGet("/api/inspections/{id}/export/rectification-record", rectification);\n', '')
s = s.replace('        app.MapGet("/api/exports/ledger", ledger);\n', '')
p.write_text(s)
for directory in ['web', 'tests', 'server/StreetEcoTool.Tests']:
    for p in Path(directory).rglob('*'):
        if p.suffix not in ['.vue', '.ts', '.mjs', '.py', '.cs'] or any(name in p.parts for name in ['node_modules', 'obj', 'bin', '.nuxt', '.output']): continue
        s = p.read_text()
        s = s.replace('/api/exports/ledger', '/api/exports/rectification-ledger').replace('/export/rectification-record', '/export/rectification')
        p.write_text(s)
for name in ['web/app/pages/issues/index.vue', 'web/app/pages/issues/[id].vue', 'web/app/pages/exports.vue']:
    Path(name).unlink()
p = Path('web/browser-test.mjs'); s = p.read_text()
s = s.replace("await page.goto(origin + '/#/issues?status=pending&overdue=true', { waitUntil: 'networkidle' })", "await page.goto(origin + '/#/inspections?rectify=pending&overdue=true', { waitUntil: 'networkidle' })")
s = s.replace("    await page.goto(origin + '/#/exports', { waitUntil: 'networkidle' }); await page.waitForURL('**/#/inspections')\n", '')
s = s.replace("await page.goto(origin + '/#/issues/' + issueId, { waitUntil: 'networkidle' })", "await page.goto(origin + '/#/inspections/' + inspectionId + '?issue=' + issueId, { waitUntil: 'networkidle' })")
p.write_text(s)
shutil.rmtree('server/StreetEcoTool.Tests/Fixtures/schema-v1')
# Do not delete the mixed StoreV2Tests file or current recovery assertions.
for p in Path('server').rglob('*.cs'):
    if any(part in ['bin', 'obj'] for part in p.parts): continue
    assert 'MigrateLegacyLayout' not in p.read_text(), str(p)

# Drop ONLY the global prose blacklist; the independent color/token check and
# its still-used comment scanner remain. No lexical filtering of business input.
p = Path('server/StreetEcoTool.Tests/SourceCopyTests.cs'); s = p.read_text()
start = s.index('    [Fact]\n    public void UserFacingSourceHasNoProhibitedCopy()')
end = s.index('    [Fact]\n    public void PagesAndComponentsUseColorTokensAndCompactControls()', start)
s = s[:start] + '''    [Fact]
    public void DestructiveDialogsExplainTheObjectsAtRisk()
    {
        var root = Repository();
        var inspection = File.ReadAllText(Path.Combine(root, "web", "app", "pages", "inspections", "[id].vue"));
        Assert.Contains("最后一条排查", inspection, StringComparison.Ordinal);
        Assert.Contains("点位照片也将删除", inspection, StringComparison.Ordinal);
        var batch = File.ReadAllText(Path.Combine(root, "web", "app", "components", "InspectionBatch.vue"));
        Assert.Contains("未提交的", batch, StringComparison.Ordinal);
        Assert.Contains("继续编辑", batch, StringComparison.Ordinal);
    }

''' + s[end:]
p.write_text(s)

# Explain the changed support boundary immediately; the final documentation
# phase will shorten the README and move its historical verification separately.
p = Path('docs/optimization/DECISIONS.md'); p.write_text(p.read_text() + '\nF06：直接建立 schema 2，现有 schema 2 不迁移；旧/未来数据库和旧布局无损拒绝。当前备份 manifest FormatVersion=1、当前恢复日志、before-* 副本、午夜写入保护和 JPEG 路径保护保留。旧兼容夹具删除，当前恢复/业务混合测试文件保留。\nF11：规范导出路由为 /export/rectification 和 /api/exports/rectification-ledger；旧前端跳转壳和重复 Word 路由移除。当前单项 API 及示例数据仍可用，单项调用同一批量事务实现。\n')
Path('/tmp/hbtool-stage-message.txt').write_text('refactor: F06/F10/F11/F19 initialize current schema and remove legacy-only paths safely\n')
