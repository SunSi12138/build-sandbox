using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection _db = null!;
    private bool _disposed;
    public const int SchemaVersion = 2;
    public const int DatabaseApplicationId = 1397048143;
    public string RootDir { get; }
    public string DataDir { get; }
    public string PhotosDir { get; }
    public string BackupDir { get; }
    public string TemporaryDir { get; }
    public string DatabasePath => Path.Combine(DataDir, "app.db");
    public static string Today => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string NewId() => Guid.NewGuid().ToString("N");

    public Store(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        RootDir = Directory.GetParent(DataDir)?.FullName ?? throw new IOException("数据目录缺少程序上级目录。");
        PhotosDir = Path.Combine(RootDir, "photos");
        BackupDir = Path.Combine(RootDir, "backup");
        TemporaryDir = Path.Combine(RootDir, "temp");
        Directory.CreateDirectory(DataDir);
        RecoverInterruptedRestore(DataDir);
        MigrateLegacyLayout(DataDir);
        Directory.CreateDirectory(PhotosDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(TemporaryDir);
        SQLitePCL.Batteries_V2.Init();
        try { ReopenConnection(); }
        catch { _db?.Dispose(); throw; }
    }

    // The caller holds _gate when replacing a live database (restore).
    internal void CloseConnection()
    {
        _db?.Dispose();
    }

    internal void ReopenConnection()
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, ForeignKeys = true, DefaultTimeout = 15
        }.ToString());
        _db.Open();
        using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;")) pragmas.ExecuteNonQuery();
        using var versionCommand = Command("PRAGMA user_version;");
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new ApiException(409, "数据库来自更新版本，请使用相同或更新的程序打开。");
        if (version == 0) MigrateToVersion1();
        if (version <= 1) MigrateToVersion2(version == 1);
    }

    private void MigrateToVersion1()
    {
        using var transaction = _db.BeginTransaction();
        using (var migrate = Command("""
            CREATE TABLE settings (
                id INTEGER PRIMARY KEY CHECK(id=1), street_name TEXT NOT NULL, issuing_unit TEXT NOT NULL,
                default_inspector TEXT NOT NULL, notice_template TEXT NOT NULL, jpeg_quality INTEGER NOT NULL CHECK(jpeg_quality BETWEEN 1 AND 100)
            );
            CREATE TABLE communities (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, title TEXT NOT NULL);
            CREATE TABLE point_types (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE);
            CREATE TABLE points (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT,
                type_id TEXT NOT NULL REFERENCES point_types(id) ON DELETE RESTRICT, address TEXT NOT NULL,
                contact TEXT NOT NULL DEFAULT '', phone TEXT NOT NULL DEFAULT '',
                frequency TEXT NOT NULL CHECK(frequency IN ('weekly','monthly','quarterly','irregular')),
                notes TEXT NOT NULL DEFAULT '', disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1))
            );
            CREATE TABLE inspections (
                id TEXT PRIMARY KEY, point_id TEXT NOT NULL REFERENCES points(id) ON DELETE RESTRICT,
                inspection_date TEXT NOT NULL, inspector TEXT NOT NULL, result TEXT NOT NULL CHECK(result IN ('normal','issues')),
                notes TEXT NOT NULL DEFAULT '', notice_number TEXT NOT NULL DEFAULT '',
                point_name TEXT NOT NULL, community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT,
                community_name TEXT NOT NULL, community_title TEXT NOT NULL,
                type_id TEXT NOT NULL REFERENCES point_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
                address TEXT NOT NULL, contact TEXT NOT NULL, phone TEXT NOT NULL
            );
            CREATE TABLE issues (
                id TEXT PRIMARY KEY, inspection_id TEXT NOT NULL REFERENCES inspections(id) ON DELETE RESTRICT,
                description TEXT NOT NULL, requirement TEXT NOT NULL DEFAULT '', deadline TEXT NOT NULL, responsible_unit TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','feedback','closed')),
                feedback_date TEXT, feedback_notes TEXT NOT NULL DEFAULT '', review_date TEXT,
                review_conclusion TEXT NOT NULL DEFAULT '', closed_date TEXT
            );
            CREATE TABLE issue_actions (
                id TEXT PRIMARY KEY, issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE RESTRICT,
                kind TEXT NOT NULL CHECK(kind IN ('feedback','review','return')), action_date TEXT NOT NULL, notes TEXT NOT NULL
            );
            CREATE TABLE photos (
                id TEXT PRIMARY KEY, inspection_id TEXT REFERENCES inspections(id) ON DELETE RESTRICT,
                issue_id TEXT REFERENCES issues(id) ON DELETE RESTRICT,
                action_id TEXT REFERENCES issue_actions(id) ON DELETE RESTRICT,
                phase TEXT NOT NULL CHECK(phase IN ('site','after','review')),
                relative_path TEXT NOT NULL UNIQUE, width INTEGER NOT NULL CHECK(width>0), height INTEGER NOT NULL CHECK(height>0),
                uploaded_at TEXT NOT NULL,
                CHECK(inspection_id IS NULL OR issue_id IS NULL), CHECK(action_id IS NULL OR issue_id IS NOT NULL),
                CHECK(inspection_id IS NULL OR phase='site')
            );
            CREATE INDEX ix_points_community_type ON points(community_id,type_id,disabled);
            CREATE INDEX ix_inspections_point_date ON inspections(point_id,inspection_date);
            CREATE INDEX ix_inspections_community_date ON inspections(community_id,inspection_date);
            CREATE INDEX ix_issues_status_deadline ON issues(status,deadline);
            CREATE INDEX ix_issues_inspection ON issues(inspection_id);
            CREATE INDEX ix_actions_issue ON issue_actions(issue_id);
            CREATE INDEX ix_photos_inspection ON photos(inspection_id);
            CREATE INDEX ix_photos_issue ON photos(issue_id);
            CREATE INDEX ix_photos_unattached ON photos(uploaded_at) WHERE inspection_id IS NULL AND issue_id IS NULL;
            PRAGMA application_id=1397048143;
            PRAGMA user_version=1;
            """, transaction)) migrate.ExecuteNonQuery();
        using (var settings = Command("INSERT INTO settings(id,street_name,issuing_unit,default_inspector,notice_template,jpeg_quality) VALUES(1,$street,$unit,$inspector,$template,$quality);", transaction,
            ("$street", ""), ("$unit", ""), ("$inspector", ""),
            ("$template", "经我街道于{日期}对你社区辖区内{点位}开展环保排查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并将整改情况书面反馈我街道。"), ("$quality", 75))) settings.ExecuteNonQuery();
        foreach (var name in new[] { "餐饮油烟", "工地扬尘", "河道水体", "垃圾堆放", "小散乱污", "其他" })
        {
            using var type = Command("INSERT INTO point_types(id,name) VALUES($id,$name);", transaction, ("$id", NewId()), ("$name", name));
            type.ExecuteNonQuery();
        }
        transaction.Commit();
    }


    public static void MigrateLegacyLayout(string dataDir)
    {
        var data = Path.GetFullPath(dataDir);
        var root = Directory.GetParent(data)?.FullName ?? throw new IOException("数据目录缺少程序上级目录。");
        try
        {
            foreach (var name in new[] { "photos", "backup" })
                MergeDirectory(Path.Combine(data, name), Path.Combine(root, name));
            var temp = Path.Combine(root, "temp");
            MergeDirectory(Path.Combine(data, ".temporary"), temp);
            MergeDirectory(Path.Combine(data, ".restore-staging"), Path.Combine(temp, "restore-staging"));
            foreach (var name in new[] { ".restore-journal.json", ".restore-journal.json.tmp" })
            {
                var old = Path.Combine(data, name);
                if (!File.Exists(old)) continue;
                Directory.CreateDirectory(temp);
                var target = Path.Combine(temp, name.TrimStart('.'));
                if (File.Exists(target)) Console.Error.WriteLine($"旧目录存在同名文件，已保留原文件：{old}");
                else File.Move(old, target);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException($"旧目录迁移失败，原数据已保留，请检查磁盘权限后重试：{error.Message}", error); }
    }

    private static void MergeDirectory(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
            (Directory.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("数据目录不能使用符号链接或目录联接。");
        if (!Directory.Exists(target)) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); Directory.Move(source, target); return; }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination) || Directory.Exists(destination)) Console.Error.WriteLine($"旧目录存在同名条目，已保留原文件：{file}");
            else File.Move(file, destination);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            if (File.Exists(destination)) Console.Error.WriteLine($"旧目录存在同名条目，已保留原目录：{directory}");
            else MergeDirectory(directory, destination);
        }
        if (!Directory.EnumerateFileSystemEntries(source).Any()) Directory.Delete(source);
    }

    private void MigrateToVersion2(bool preserveVersion1)
    {
        Directory.CreateDirectory(BackupDir);
        if (preserveVersion1)
        {
            using (var checkpoint = Command("PRAGMA wal_checkpoint(TRUNCATE);"))
            using (var result = checkpoint.ExecuteReader())
                if (!result.Read() || result.GetInt32(0) != 0) throw new IOException("迁移前无法完成数据库快照，请关闭其他数据库程序后重试。");
            var backup = Path.Combine(BackupDir, $"before-migrate-v1-{DateTime.Now:yyyyMMdd-HHmmss}-{NewId()[..8]}.db");
            File.Copy(DatabasePath, backup);
        }
        using (var foreignKeys = Command("PRAGMA foreign_keys=OFF;")) foreignKeys.ExecuteNonQuery();
        try
        {
            using var transaction = _db.BeginTransaction();
            using (var migrate = Command("""
                ALTER TABLE points ADD COLUMN longitude REAL CHECK(longitude IS NULL OR longitude BETWEEN -180 AND 180);
                ALTER TABLE points ADD COLUMN latitude REAL CHECK(latitude IS NULL OR latitude BETWEEN -90 AND 90);
                ALTER TABLE points ADD COLUMN temporary INTEGER NOT NULL DEFAULT 0 CHECK(temporary IN (0,1));
                CREATE TABLE campaign_types (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE);
                CREATE TABLE campaigns (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL,
                    type_id TEXT NOT NULL REFERENCES campaign_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
                    community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT, community_name TEXT NOT NULL,
                    location TEXT NOT NULL, campaign_date TEXT NOT NULL, end_date TEXT,
                    organizer TEXT NOT NULL DEFAULT '', participants TEXT NOT NULL DEFAULT '',
                    participant_count INTEGER CHECK(participant_count IS NULL OR participant_count >= 0),
                    vehicle_count INTEGER CHECK(vehicle_count IS NULL OR vehicle_count >= 0),
                    garbage_tons REAL CHECK(garbage_tons IS NULL OR garbage_tons >= 0),
                    area_sqm REAL CHECK(area_sqm IS NULL OR area_sqm >= 0),
                    content TEXT NOT NULL, outcome TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
                    CHECK(end_date IS NULL OR end_date >= campaign_date)
                );
                CREATE TABLE photos_v2 (
                    id TEXT PRIMARY KEY, inspection_id TEXT REFERENCES inspections(id) ON DELETE RESTRICT,
                    issue_id TEXT REFERENCES issues(id) ON DELETE RESTRICT,
                    action_id TEXT REFERENCES issue_actions(id) ON DELETE RESTRICT,
                    point_id TEXT REFERENCES points(id) ON DELETE RESTRICT,
                    campaign_id TEXT REFERENCES campaigns(id) ON DELETE RESTRICT,
                    phase TEXT NOT NULL CHECK(phase IN ('site','after','review','point','before','process')),
                    relative_path TEXT NOT NULL UNIQUE, width INTEGER NOT NULL CHECK(width>0), height INTEGER NOT NULL CHECK(height>0),
                    uploaded_at TEXT NOT NULL, has_thumb INTEGER NOT NULL DEFAULT 0 CHECK(has_thumb IN (0,1)),
                    CHECK((inspection_id IS NOT NULL)+(issue_id IS NOT NULL)+(point_id IS NOT NULL)+(campaign_id IS NOT NULL) <= 1),
                    CHECK(action_id IS NULL OR issue_id IS NOT NULL),
                    CHECK(inspection_id IS NULL OR phase='site'),
                    CHECK(issue_id IS NULL OR phase IN ('site','after','review')),
                    CHECK(point_id IS NULL OR phase='point'),
                    CHECK(campaign_id IS NULL OR phase IN ('before','process','after'))
                );
                INSERT INTO photos_v2(id,inspection_id,issue_id,action_id,phase,relative_path,width,height,uploaded_at)
                    SELECT id,inspection_id,issue_id,action_id,phase,relative_path,width,height,uploaded_at FROM photos;
                DROP TABLE photos;
                ALTER TABLE photos_v2 RENAME TO photos;
                CREATE INDEX ix_photos_inspection ON photos(inspection_id);
                CREATE INDEX ix_photos_issue ON photos(issue_id);
                CREATE INDEX ix_photos_point ON photos(point_id);
                CREATE INDEX ix_photos_campaign ON photos(campaign_id);
                CREATE INDEX ix_photos_unattached ON photos(uploaded_at)
                    WHERE inspection_id IS NULL AND issue_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;
                CREATE INDEX ix_campaigns_date ON campaigns(campaign_date,community_id,type_id);
                CREATE INDEX ix_points_temporary ON points(temporary,disabled);
                PRAGMA user_version=2;
                """, transaction)) migrate.ExecuteNonQuery();
            foreach (var name in new[] { "卫生大扫除", "垃圾清运", "河道清理", "乱堆乱放清理", "小广告清除", "绿化补种", "其他" })
            {
                using var command = Command("INSERT INTO campaign_types(id,name) VALUES($id,$name);", transaction, ("$id", NewId()), ("$name", name));
                command.ExecuteNonQuery();
            }
            using (var check = Command("PRAGMA foreign_key_check;", transaction))
            using (var row = check.ExecuteReader())
                if (row.Read()) throw new InvalidDataException("迁移后的数据关联检查失败。");
            transaction.Commit();
        }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException)
        { throw new IOException($"数据库升级失败，事务已回滚，请保留升级前备份：{error.Message}", error); }
        finally { using var foreignKeys = Command("PRAGMA foreign_keys=ON;"); foreignKeys.ExecuteNonQuery(); }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private static string Text(string? value, string label, int maximum = 2000, bool required = false)
    {
        var result = value?.Trim() ?? "";
        if (required && result.Length == 0) throw new ApiException(400, $"请填写{label}。");
        if (result.Length > maximum) throw new ApiException(400, $"{label}不能超过 {maximum} 个字符。");
        if (result.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')) throw new ApiException(400, $"{label}含有无效控制字符。");
        return result;
    }

    private static string DateText(string? value, string label, bool defaultToday = false)
    {
        if (string.IsNullOrWhiteSpace(value) && defaultToday) return Today;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new ApiException(400, $"{label}必须是 yyyy-MM-dd 格式的有效日期。");
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string? OptionalDate(string? value, string label) => string.IsNullOrWhiteSpace(value) ? null : DateText(value, label);
    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static (int Page, int PageSize) Paging(int page, int pageSize) => (Math.Clamp(page, 1, 1000000), Math.Clamp(pageSize, 1, 100));
    private static void RequireChanged(int count, string name) { if (count == 0) throw new ApiException(404, $"{name}不存在。"); }
    private static void CheckDateRange(string? from, string? to) { if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0) throw new ApiException(400, "开始日期不能晚于结束日期。"); }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseConnection();
        }
    }
}
