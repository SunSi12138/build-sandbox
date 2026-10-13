using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private bool _dailySnapshotsEnabled;
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

    public Store(string dataDir, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        DataDir = Path.GetFullPath(dataDir);
        RootDir = Directory.GetParent(DataDir)?.FullName ?? throw new IOException("数据目录缺少程序上级目录。");
        PhotosDir = Path.Combine(RootDir, "photos");
        BackupDir = Path.Combine(RootDir, "backup");
        TemporaryDir = Path.Combine(RootDir, "temp");
        RejectUnsupportedLayout(DataDir);
        RecoverInterruptedRestore(DataDir);
        if (!File.Exists(DatabasePath) && Directory.Exists(PhotosDir) && Directory.EnumerateFileSystemEntries(PhotosDir).Any())
            throw new ApiException(409, "照片目录已有内容但缺少数据库。已停止启动，请保留文件并检查完整备份。");
        Directory.CreateDirectory(DataDir);
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
        var initialize = IsEmptyOrValidateCurrentDatabase();
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Pooling = false, ForeignKeys = true, DefaultTimeout = 15
        }.ToString());
        _db.Open();
        using (var pragmas = Command("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;")) pragmas.ExecuteNonQuery();
        if (initialize) InitializeCurrentDatabase();
    }

    private void InitializeCurrentDatabase()
    {
        using var transaction = _db.BeginTransaction();
        using (var create = Command("""
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
                notes TEXT NOT NULL DEFAULT '', disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1)),
                longitude REAL CHECK(longitude IS NULL OR longitude BETWEEN -180 AND 180),
                latitude REAL CHECK(latitude IS NULL OR latitude BETWEEN -90 AND 90),
                temporary INTEGER NOT NULL DEFAULT 0 CHECK(temporary IN (0,1))
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
            CREATE TABLE photos (
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
            CREATE INDEX ix_points_community_type ON points(community_id,type_id,disabled);
            CREATE INDEX ix_inspections_point_date ON inspections(point_id,inspection_date);
            CREATE INDEX ix_inspections_community_date ON inspections(community_id,inspection_date);
            CREATE INDEX ix_issues_status_deadline ON issues(status,deadline);
            CREATE INDEX ix_issues_inspection ON issues(inspection_id);
            CREATE INDEX ix_actions_issue ON issue_actions(issue_id);
            CREATE INDEX ix_photos_inspection ON photos(inspection_id);
            CREATE INDEX ix_photos_issue ON photos(issue_id);
            CREATE INDEX ix_photos_unattached ON photos(uploaded_at) WHERE inspection_id IS NULL AND issue_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;
            CREATE INDEX ix_photos_point ON photos(point_id);
            CREATE INDEX ix_photos_campaign ON photos(campaign_id);
            CREATE INDEX ix_campaigns_date ON campaigns(campaign_date,community_id,type_id);
            CREATE INDEX ix_points_temporary ON points(temporary,disabled);
            PRAGMA application_id=1397048143;
            PRAGMA user_version=2;
            """, transaction)) create.ExecuteNonQuery();
        using (var settings = Command("INSERT INTO settings(id,street_name,issuing_unit,default_inspector,notice_template,jpeg_quality) VALUES(1,$street,$unit,$inspector,$template,$quality);", transaction,
            ("$street", ""), ("$unit", ""), ("$inspector", ""),
            ("$template", "经我街道于{日期}对你社区辖区内{点位}开展环保排查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并将整改情况书面反馈我街道。"), ("$quality", 75))) settings.ExecuteNonQuery();
        foreach (var name in new[] { "餐饮油烟", "工地扬尘", "河道水体", "垃圾堆放", "小散乱污", "其他" })
        {
            using var type = Command("INSERT INTO point_types(id,name) VALUES($id,$name);", transaction, ("$id", NewId()), ("$name", name));
            type.ExecuteNonQuery();
        }
        foreach (var name in new[] { "卫生大扫除", "垃圾清运", "河道清理", "乱堆乱放清理", "小广告清除", "绿化补种", "其他" })
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
        if (!File.Exists(DatabasePath)) return true;
        // Even SQLite's read-only mode may create WAL/SHM sidecars. Inspect a
        // private copy instead, including recovery data, so a refused version
        // cannot alter any original file. Called before serving requests or with
        // _gate held after the old connection has been closed.
        var probeDirectory = Path.Combine(Path.GetTempPath(), "hbtool-schema-" + NewId());
        Directory.CreateDirectory(probeDirectory);
        try
        {
            var probePath = Path.Combine(probeDirectory, "app.db");
            foreach (var suffix in new[] { "", "-wal", "-journal" })
            {
                var source = DatabasePath + suffix;
                if (File.Exists(source)) File.Copy(source, probePath + suffix);
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = probePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
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
        finally { Directory.Delete(probeDirectory, true); }
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

    // Called at the actual mutation boundary under _gate, after HTTP body reads.
    // A request spanning midnight must snapshot before its first new-day write.
    private void PrepareWrite()
    {
        if (_dailySnapshotsEnabled) EnsurePreviousDaySnapshot(DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date));
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
