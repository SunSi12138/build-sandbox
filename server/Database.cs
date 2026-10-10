using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store : IDisposable
{
    private readonly object _gate = new();
    private SqliteConnection _db = null!;
    private bool _disposed;
    public const int SchemaVersion = 1;
    public const int DatabaseApplicationId = 1397048143;
    public string DataDir { get; }
    public string PhotosDir { get; }
    public string BackupDir { get; }
    public string DatabasePath => Path.Combine(DataDir, "app.db");
    public static string Today => DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string NewId() => Guid.NewGuid().ToString("N");

    public Store(string dataDir)
    {
        DataDir = Path.GetFullPath(dataDir);
        PhotosDir = Path.Combine(DataDir, "photos");
        BackupDir = Path.Combine(DataDir, "backup");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(PhotosDir);
        Directory.CreateDirectory(BackupDir);
        RecoverInterruptedRestore(DataDir);
        SQLitePCL.Batteries_V2.Init();
        ReopenConnection();
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
