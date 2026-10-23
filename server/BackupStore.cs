using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed record BackupEntryDto(string Name, string Date, long Bytes);
public sealed record BackupOverviewDto(BackupEntryDto[] Items, int RetentionCount);
public sealed record BackupManifest(int FormatVersion, int SchemaVersion, string CreatedAt, string Application);
public sealed record RestorePreviewDto(string Token, string CreatedAt, int SchemaVersion, int PhotoCount, int PointCount, int InspectionCount, int IssueCount, long ExpandedBytes, string ExpiresAt, int RemediationCount, int ActivityCount, int MatterCount, int AttachmentCount, int TagCount);
public sealed record RestoreRequest(string Token, bool Confirm = false);
public sealed record RestoreResultDto(bool Ok, string PreservedBackup, string Message);
internal sealed record RestoreJournal(string BackupName, bool Committed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupOverviewDto))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(RestorePreviewDto))]
[JsonSerializable(typeof(RestoreRequest))]
[JsonSerializable(typeof(RestoreResultDto))]
[JsonSerializable(typeof(RestoreJournal))]
internal partial class BackupJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    public const long MaximumRestoreBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 50000;
    private string? _maintenanceDate;
    private string StagingDir => Path.Combine(TemporaryDir, "restore-staging");

    public void RunMaintenance() => RunDailyMaintenance(DateOnly.FromDateTime(DateTime.Today));

    public void EnsurePreviousDaySnapshot(DateOnly today)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(BackupDir);
            var previousDay = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var destination = Path.Combine(BackupDir, $"auto-{previousDay}.db");
            if (!File.Exists(destination))
            {
                var temporary = Path.Combine(TemporaryDir, $"auto-{NewId()}.db");
                try { VacuumInto(temporary); File.Move(temporary, destination); }
                finally { TryDeleteFile(temporary); }
                Console.WriteLine($"已保存 {previousDay} 数据库快照：{destination}");
            }
            foreach (var old in GetAutomaticBackupFiles().OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(14)) File.Delete(old);
        }
    }

    public void RunDailyMaintenance(DateOnly today)
    {
        lock (_gate)
        {
            var day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (_maintenanceDate == day) return;
            EnsurePreviousDaySnapshot(today);
            _dailySnapshotsEnabled = true;
            CleanupOrphanPhotos();
            var cutoff = DateTime.Now.AddDays(-1);
            if (Directory.Exists(StagingDir))
                foreach (var directory in Directory.EnumerateDirectories(StagingDir))
                    if (Directory.GetLastWriteTime(directory) < cutoff) TryDeleteDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(TemporaryDir))
                if (!Path.GetFileName(file).StartsWith("restore-journal", StringComparison.Ordinal) && File.GetLastWriteTime(file) < cutoff) TryDeleteFile(file);
            foreach (var directory in Directory.EnumerateDirectories(TemporaryDir))
                if (directory != StagingDir && Directory.GetLastWriteTime(directory) < cutoff) TryDeleteDirectory(directory);
            _maintenanceDate = day;
        }
    }

    public BackupOverviewDto GetBackups()
    {
        lock (_gate)
        {
            return new BackupOverviewDto(GetAutomaticBackupFiles().OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new BackupEntryDto(Path.GetFileName(path), Path.GetFileName(path)[5..15], new FileInfo(path).Length)).ToArray(), 14);
        }
    }

    private IEnumerable<string> GetAutomaticBackupFiles() => Directory.Exists(BackupDir)
        ? Directory.EnumerateFiles(BackupDir, "auto-????-??-??.db").Where(path => Path.GetFileName(path).Length == 18 && DateOnly.TryParseExact(Path.GetFileName(path)[5..15], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        : [];

    private void VacuumInto(string path)
    {
        using var command = Command("VACUUM INTO $path;", null, ("$path", path));
        command.ExecuteNonQuery();
    }

    // Hold the database gate for the snapshot and every photo copy: no upload, delete,
    // association or restore can change the database/files halfway through this archive.
    public string CreateBackupZip()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(TemporaryDir);
            var id = NewId();
            var database = Path.Combine(TemporaryDir, id + ".db");
            var destination = Path.Combine(TemporaryDir, id + ".zip");
            try
            {
                VacuumInto(database);
                using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(database, "app.db", CompressionLevel.Fastest);
                var manifest = archive.CreateEntry("manifest.json");
                using (var output = manifest.Open())
                    JsonSerializer.Serialize(output, new BackupManifest(1, SchemaVersion, DateTime.Now.ToString("O", CultureInfo.InvariantCulture), "StreetEcoTool"), BackupJsonContext.Default.BackupManifest);
                using (var attachments = Command("SELECT id,relative_path FROM attachments ORDER BY relative_path;"))
                using (var files = attachments.ExecuteReader())
                    while (files.Read())
                    {
                        var relative = files.GetString(1);
                        var file = AttachmentStorage.ResolvePath(AttachmentsDir, files.GetString(0), relative);
                        if (!File.Exists(file)) throw new IOException("数据库中登记的附件缺失，请检查完整备份。");
                        archive.CreateEntryFromFile(file, "attachments/" + relative, CompressionLevel.NoCompression);
                    }
                using var photoCommand = Command("SELECT id,relative_path,has_thumb FROM photos ORDER BY relative_path;");
                using var photoRows = photoCommand.ExecuteReader();
                while (photoRows.Read())
                {
                    var relative = photoRows.GetString(1);
                    var photo = PhotoStorage.ResolvePath(PhotosDir, photoRows.GetString(0), relative);
                    if (!File.Exists(photo)) throw new IOException("数据库中登记的照片文件缺失，请保留现有数据并检查磁盘。");
                    archive.CreateEntryFromFile(photo, "photos/" + relative, CompressionLevel.NoCompression);
                    if (photoRows.GetInt32(2) != 0)
                    {
                        var thumbnail = PhotoStorage.ResolveThumbnailPath(PhotosDir, photoRows.GetString(0), relative);
                        if (!File.Exists(thumbnail)) throw new IOException("数据库中登记的缩略图缺失，请检查照片目录。");
                        archive.CreateEntryFromFile(thumbnail, "photos/" + relative[..^4] + ".thumb.jpg", CompressionLevel.NoCompression);
                    }
                }
                return destination;
            }
            catch { TryDeleteFile(destination); throw; }
            finally { TryDeleteFile(database); }
        }
    }

    public async Task<RestorePreviewDto> PrepareRestoreAsync(Stream input, CancellationToken cancellationToken)
    {
        var token = NewId();
        var staging = Path.Combine(StagingDir, token);
        Directory.CreateDirectory(staging);
        var zipPath = Path.Combine(staging, "upload.zip");
        var extracted = Path.Combine(staging, "content");
        Directory.CreateDirectory(extracted);
        Directory.CreateDirectory(Path.Combine(extracted, "photos"));
        Directory.CreateDirectory(Path.Combine(extracted, "attachments"));
        try
        {
            await using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                await CopyLimitedAsync(input, file, MaximumRestoreBytes, cancellationToken);
            var expanded = ExtractArchive(zipPath, extracted);
            var preview = ValidateRestoreDirectory(extracted, token, expanded);
            using (var metadata = File.Create(Path.Combine(staging, "preview.json")))
                JsonSerializer.Serialize(metadata, preview, BackupJsonContext.Default.RestorePreviewDto);
            File.Delete(zipPath);
            return preview;
        }
        catch (Exception error) when (error is InvalidDataException or SqliteException or JsonException or FormatException or OverflowException)
        {
            TryDeleteDirectory(staging);
            throw new ApiException(400, $"备份校验未通过，现有数据没有改变：{error.Message}");
        }
        catch { TryDeleteDirectory(staging); throw; }
    }

    private static long ExtractArchive(string zipPath, string extracted)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count is < 1 or > MaximumArchiveEntries) throw new InvalidDataException("压缩包条目数量无效或超过 50000 个。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        var hasDatabase = false;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.Length > 160 || name.Contains('\\') || name.StartsWith('/') || name.Contains("..", StringComparison.Ordinal) || !seen.Add(name))
                throw new InvalidDataException("压缩包包含不安全、重复或过长的文件路径。");
            if ((((uint)entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("备份不能包含符号链接。");
            if (name.EndsWith('/'))
            {
                var trimmed = name.TrimEnd('/');
                if (trimmed is not ("photos" or "attachments") && !(trimmed.StartsWith("photos/", StringComparison.Ordinal) && IsPhotoMonth(trimmed[7..])) && !(trimmed.StartsWith("attachments/", StringComparison.Ordinal) && IsPhotoMonth(trimmed[12..])))
                    throw new InvalidDataException("压缩包包含非预期目录。");
                if (entry.Length != 0) throw new InvalidDataException("压缩包目录条目无效。");
                continue;
            }
            var limit = name switch
            {
                "app.db" => 256L * 1024 * 1024,
                "manifest.json" => 16L * 1024,
                _ when IsArchivePhotoPath(name) => name.EndsWith(".thumb.jpg", StringComparison.Ordinal) ? PhotoStorage.MaximumThumbnailBytes : PhotoStorage.MaximumUploadBytes,
                _ when IsArchiveAttachmentPath(name) => AttachmentStorage.MaximumBytes,
                _ => throw new InvalidDataException("压缩包只能包含数据库、清单、照片和附件。")
            };
            if (entry.Length < 1 || entry.Length > limit) throw new InvalidDataException($"压缩包中的 {name} 为空或超过大小限制。");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("备份解压总大小超过 16 GiB。");
            var target = Path.Combine(extracted, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int count;
            while ((count = source.Read(buffer)) != 0)
            {
                copied = checked(copied + count);
                if (copied > limit || copied > entry.Length) throw new InvalidDataException("解压数据长度与压缩包声明不符。");
                output.Write(buffer, 0, count);
            }
            if (copied != entry.Length) throw new InvalidDataException("压缩包文件不完整。");
            if (name == "app.db") hasDatabase = true;
        }
        if (!hasDatabase) throw new InvalidDataException("备份缺少 app.db 数据库。");
        return expanded;
    }

    private static bool IsPhotoMonth(string text) => text.Length == 6 && text.All(char.IsAsciiDigit)
        && int.TryParse(text[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1 and <= 9999
        && int.TryParse(text[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12;

    private static bool IsArchivePhotoPath(string name)
    {
        if (!name.StartsWith("photos/", StringComparison.Ordinal)) return false;
        var parts = name[7..].Split('/');
        return parts.Length == 2 && IsPhotoMonth(parts[0]) &&
            ((parts[1].Length == 36 && parts[1].EndsWith(".jpg", StringComparison.Ordinal)) ||
             (parts[1].Length == 42 && parts[1].EndsWith(".thumb.jpg", StringComparison.Ordinal))) &&
            parts[1][..32].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsArchiveAttachmentPath(string name)
    {
        if (!name.StartsWith("attachments/", StringComparison.Ordinal)) return false;
        var parts = name[12..].Split('/');
        if (parts.Length != 2 || !IsPhotoMonth(parts[0]) || parts[1].Length < 36) return false;
        var id = parts[1][..32];
        try { _ = AttachmentStorage.ResolvePath(Path.GetTempPath(), id, name[12..]); return true; }
        catch (Exception error) when (error is InvalidDataException or ApiException) { return false; }
    }

    private static RestorePreviewDto ValidateRestoreDirectory(string directory, string token, long expanded)
    {
        var path = Path.Combine(directory, "app.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, ForeignKeys = true }.ToString());
        connection.Open();
        using (var trusted = connection.CreateCommand()) { trusted.CommandText = "PRAGMA trusted_schema=OFF;"; trusted.ExecuteNonQuery(); }
        static long Number(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var application = Number(connection, "PRAGMA application_id;");
        var version = Number(connection, "PRAGMA user_version;");
        if (application != DatabaseApplicationId || version != SchemaVersion)
            throw new InvalidDataException("这不是本工具支持的备份，或数据库来自更新版本。");
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (integrity.ExecuteScalar()?.ToString() != "ok") throw new InvalidDataException("数据库完整性检查失败。");
        }
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read()) throw new InvalidDataException("数据库记录之间的关联不完整。");
        }
        if (Number(connection, "SELECT count(*) FROM sqlite_master WHERE type IN ('trigger','view');") != 0)
            throw new InvalidDataException("备份包含非预期的数据库对象。");
        if (Number(connection, "SELECT count(*) FROM settings WHERE id=1 AND jpeg_quality BETWEEN 1 AND 100;") != 1)
            throw new InvalidDataException("备份的设置记录不完整。");
        // Read all required columns before replacing a live database, including columns
        // whose absence SQLite's generic integrity_check would not consider an error.
        foreach (var sql in RequiredSchemaQueries)
        {
            using var schema = connection.CreateCommand(); schema.CommandText = sql;
            using var reader = schema.ExecuteReader();
        }
        using (var dates = connection.CreateCommand())
        {
            dates.CommandText = "SELECT inspection_date FROM inspections UNION ALL SELECT deadline FROM issues UNION ALL SELECT feedback_date FROM issues UNION ALL SELECT review_date FROM issues UNION ALL SELECT closed_date FROM issues UNION ALL SELECT action_date FROM issue_actions;";
            dates.CommandText = dates.CommandText.TrimEnd(';') + " UNION ALL SELECT activity_date FROM activities UNION ALL SELECT end_date FROM activities UNION ALL SELECT received_date FROM matters UNION ALL SELECT deadline FROM matters UNION ALL SELECT closed_date FROM matters UNION ALL SELECT action_date FROM matter_actions;";
            using var reader = dates.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0) && !DateOnly.TryParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new InvalidDataException("备份包含格式无效的业务日期。");
        }
        if (Number(connection, "SELECT COUNT(*) FROM activities a LEFT JOIN activity_remediations r ON r.activity_id=a.id WHERE (a.template='remediation' AND r.activity_id IS NULL) OR (a.template='general' AND r.activity_id IS NOT NULL);") != 0)
            throw new InvalidDataException("活动模板与整治数据不一致。");
        if (Number(connection, "SELECT COUNT(*) FROM activity_types WHERE template='remediation' AND built_in=1;") != 1)
            throw new InvalidDataException("内置整治类型不完整。");
        if (Number(connection, "SELECT COUNT(*) FROM photos p JOIN activities a ON a.id=p.activity_id WHERE (a.template='general' AND p.phase<>'activity') OR (a.template='remediation' AND p.phase NOT IN ('before','process','after'));") != 0)
            throw new InvalidDataException("活动照片阶段不一致。");
        var attachmentCount = 0;
        var attachmentFiles = new HashSet<string>(StringComparer.Ordinal);
        using (var attachments = connection.CreateCommand())
        {
            attachments.CommandText = "SELECT id,name,relative_path,size,content_type,sha256 FROM attachments;";
            using var rows = attachments.ExecuteReader();
            while (rows.Read())
            {
                var relative = rows.GetString(2); var extension = Path.GetExtension(relative);
                var file = AttachmentStorage.ResolvePath(Path.Combine(directory, "attachments"), rows.GetString(0), relative);
                if (!File.Exists(file) || new FileInfo(file).Length != rows.GetInt64(3) || rows.GetInt64(3) is < 1 or > AttachmentStorage.MaximumBytes)
                    throw new InvalidDataException("附件缺失或大小与数据库不一致。");
                if (rows.GetString(4) != AttachmentStorage.ContentType(extension) || !string.Equals(Path.GetExtension(rows.GetString(1)), extension, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("附件类型不一致。");
                var bytes = File.ReadAllBytes(file); AttachmentStorage.Validate(bytes, extension);
                if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != rows.GetString(5)) throw new InvalidDataException("附件校验值不一致。");
                attachmentFiles.Add(Path.GetFullPath(file)); attachmentCount++;
            }
        }
        if (Directory.EnumerateFiles(Path.Combine(directory, "attachments"), "*", SearchOption.AllDirectories).Any(file => !attachmentFiles.Contains(Path.GetFullPath(file))))
            throw new InvalidDataException("备份包含未登记的附件。");
        var photoFiles = new HashSet<string>(StringComparer.Ordinal);
        var photoCount = 0;
        using (var photos = connection.CreateCommand())
        {
            photos.CommandText = "SELECT id,relative_path,width,height,has_thumb FROM photos;";
            using var reader = photos.ExecuteReader();
            while (reader.Read())
            {
                var relative = reader.GetString(1);
                var file = PhotoStorage.ResolvePath(Path.Combine(directory, "photos"), reader.GetString(0), relative);
                if (!File.Exists(file)) throw new InvalidDataException("备份缺少数据库中引用的照片。");
                if (new FileInfo(file).Length > PhotoStorage.MaximumUploadBytes) throw new InvalidDataException("备份中的照片超过 10 MB。");
                var dimensions = JpegMetadata.Read(File.ReadAllBytes(file));
                if (dimensions.Width != reader.GetInt32(2) || dimensions.Height != reader.GetInt32(3)) throw new InvalidDataException("备份照片的实际尺寸与数据库不一致。");
                photoFiles.Add(Path.GetFullPath(file));
                photoCount++;
                if (reader.GetInt32(4) != 0)
                {
                    var thumbnail = PhotoStorage.ResolveThumbnailPath(Path.Combine(directory, "photos"), reader.GetString(0), relative);
                    if (!File.Exists(thumbnail)) throw new InvalidDataException("备份缺少数据库中登记的缩略图。");
                    if (new FileInfo(thumbnail).Length > PhotoStorage.MaximumThumbnailBytes) throw new InvalidDataException("备份中的缩略图超过 200 KB。");
                    var thumbSize = JpegMetadata.Read(File.ReadAllBytes(thumbnail));
                    if (Math.Max(thumbSize.Width, thumbSize.Height) > 360) throw new InvalidDataException("缩略图尺寸超过 360 像素。");
                    photoFiles.Add(Path.GetFullPath(thumbnail));
                }
            }
        }
        if (Directory.EnumerateFiles(Path.Combine(directory, "photos"), "*", SearchOption.AllDirectories).Any(file => !photoFiles.Contains(Path.GetFullPath(file))))
            throw new InvalidDataException("备份存在未在数据库登记的照片。");
        var manifestPath = Path.Combine(directory, "manifest.json");
        var createdAt = File.GetLastWriteTime(path).ToString("O", CultureInfo.InvariantCulture);
        if (File.Exists(manifestPath))
        {
            using var file = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize(file, BackupJsonContext.Default.BackupManifest);
            if (manifest is null || manifest.Application != "StreetEcoTool" || manifest.FormatVersion != 1 || manifest.SchemaVersion != version)
                throw new InvalidDataException("备份清单不受支持或与数据库版本不一致。");
            createdAt = manifest.CreatedAt;
        }
        return new RestorePreviewDto(token, createdAt, checked((int)version), photoCount,
            checked((int)Number(connection, "SELECT count(*) FROM points;")), checked((int)Number(connection, "SELECT count(*) FROM inspections;")),
            checked((int)Number(connection, "SELECT count(*) FROM issues;")), expanded, DateTime.Now.AddMinutes(30).ToString("O", CultureInfo.InvariantCulture), checked((int)Number(connection, "SELECT count(*) FROM activity_remediations;")), checked((int)Number(connection, "SELECT count(*) FROM activities;")), checked((int)Number(connection, "SELECT count(*) FROM matters;")), attachmentCount, checked((int)Number(connection, "SELECT count(*) FROM tags;")));
    }

    private static readonly string[] RequiredSchemaQueries =
    [
        "SELECT id,street_name,issuing_unit,default_inspector,notice_template,jpeg_quality FROM settings LIMIT 0;",
        "SELECT id,name,title FROM communities LIMIT 0;",
        "SELECT id,name FROM point_types LIMIT 0;",
        "SELECT id,name,community_id,type_id,address,contact,phone,frequency,notes,disabled,longitude,latitude,temporary FROM points LIMIT 0;",
        "SELECT id,point_id,inspection_date,inspector,result,notes,notice_number,source,point_name,community_id,community_name,community_title,type_id,type_name,address,contact,phone FROM inspections LIMIT 0;",
        "SELECT id,inspection_id,issue_type_id,issue_type_name,description,requirement,deadline,responsible_unit,status,feedback_date,feedback_notes,review_date,review_conclusion,closed_date FROM issues LIMIT 0;",
        "SELECT id,name FROM issue_types LIMIT 0;",
        "SELECT id,name,template,built_in FROM activity_types LIMIT 0;",
        "SELECT id,name,type_id,type_name,template,point_id,point_name,community_id,community_name,location,activity_date,end_date,organizer,participants,participant_count,content,outcome,notes FROM activities LIMIT 0;",
        "SELECT id,issue_id,kind,action_date,notes FROM issue_actions LIMIT 0;",
        "SELECT id,inspection_id,issue_id,action_id,point_id,activity_id,phase,relative_path,width,height,uploaded_at,has_thumb FROM photos LIMIT 0;",
        "SELECT id,name FROM remediation_types LIMIT 0;",
        "SELECT activity_id,type_id,type_name,vehicle_count,garbage_tons,area_sqm FROM activity_remediations LIMIT 0;",
        "SELECT id,title,source,received_date,content,point_id,location,deadline,handler,reference_number,contact,phone,status,closed_date,outcome,conclusion FROM matters LIMIT 0;",
        "SELECT id,matter_id,kind,action_date,handler,notes,outcome FROM matter_actions LIMIT 0;",
        "SELECT id,name,disabled FROM tags LIMIT 0;",
        "SELECT id,point_id,name,notes,relative_path,size,content_type,uploaded_at,sha256 FROM attachments LIMIT 0;",
        "SELECT activity_id,issue_id FROM activity_issues LIMIT 0;",
        "SELECT point_id,tag_id FROM point_tags LIMIT 0;",
        "SELECT inspection_id,tag_id FROM inspection_tags LIMIT 0;",
        "SELECT activity_id,tag_id FROM activity_tags LIMIT 0;",
        "SELECT matter_id,tag_id FROM matter_tags LIMIT 0;",
        "SELECT inspection_id,matter_id FROM inspection_matters LIMIT 0;",
        "SELECT issue_id,matter_id FROM issue_matters LIMIT 0;",
        "SELECT activity_id,matter_id FROM activity_matters LIMIT 0;"
    ];

    public RestoreResultDto Restore(RestoreRequest request)
    {
        if (!request.Confirm) throw new ApiException(400, "恢复会覆盖当前数据，请先核对备份摘要并再次确认。");
        if (request.Token is null || request.Token.Length != 32 || !request.Token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ApiException(400, "备份确认标识无效，请重新上传。");
        lock (_gate)
        {
            PrepareWrite();
            var staging = Path.Combine(StagingDir, request.Token);
            var metadataPath = Path.Combine(staging, "preview.json");
            if (!File.Exists(metadataPath)) throw new ApiException(404, "待恢复的备份不存在，请重新上传。");
            RestorePreviewDto preview;
            using (var metadata = File.OpenRead(metadataPath))
                preview = JsonSerializer.Deserialize(metadata, BackupJsonContext.Default.RestorePreviewDto) ?? throw new ApiException(400, "备份摘要无效。");
            if (!DateTime.TryParse(preview.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiry) || DateTime.Now > expiry)
                throw new ApiException(410, "备份确认已超过 30 分钟，请重新上传并核对。");
            var source = Path.Combine(staging, "content");
            ValidateRestoreDirectory(source, request.Token, preview.ExpandedBytes);
            Directory.CreateDirectory(BackupDir);
            // The old main file must be self-contained before any rename. If an
            // external reader prevents a full checkpoint, leave everything intact.
            using (var checkpoint = Command("PRAGMA wal_checkpoint(TRUNCATE);"))
            using (var result = checkpoint.ExecuteReader())
                if (!result.Read() || result.GetInt32(0) != 0)
                    throw new ApiException(409, "数据库正被其他程序使用，恢复尚未执行。请关闭数据库查看工具后重试。");
            var backupName = $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{NewId()[..8]}";
            var previous = Path.Combine(BackupDir, backupName);
            Directory.CreateDirectory(previous);
            var journalPath = Path.Combine(TemporaryDir, "restore-journal.json");
            WriteRestoreJournal(journalPath, new RestoreJournal(backupName, false));
            try
            {
                CloseConnection();
                MoveDatabaseFiles(DataDir, previous);
                Directory.Move(PhotosDir, Path.Combine(previous, "photos"));
                Directory.Move(AttachmentsDir, Path.Combine(previous, "attachments"));
                File.Move(Path.Combine(source, "app.db"), DatabasePath);
                Directory.Move(Path.Combine(source, "photos"), PhotosDir);
                Directory.Move(Path.Combine(source, "attachments"), AttachmentsDir);
                ReopenConnection();
                WriteRestoreJournal(journalPath, new RestoreJournal(backupName, true));
            }
            catch
            {
                CloseConnection();
                RecoverInterruptedRestore(DataDir);
                ReopenConnection();
                throw;
            }
            TryDeleteFile(journalPath);
            TryDeleteDirectory(staging);
            _maintenanceDate = null;
            return new RestoreResultDto(true, $"backup/{backupName}", "恢复完成。覆盖前的数据库、照片和附件已保留。");
        }
    }

    private static void WriteRestoreJournal(string path, RestoreJournal journal)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, journal, BackupJsonContext.Default.RestoreJournal);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    public static void RecoverInterruptedRestore(string dataDir)
    {
        var root = Directory.GetParent(Path.GetFullPath(dataDir))?.FullName ?? throw new IOException("数据目录无效。");
        RejectUnsupportedLayout(dataDir);
        var journalPath = Path.Combine(root, "temp", "restore-journal.json");
        if (!File.Exists(journalPath)) return;
        RestoreJournal journal;
        using (var file = File.OpenRead(journalPath))
            journal = JsonSerializer.Deserialize(file, BackupJsonContext.Default.RestoreJournal) ?? throw new IOException("恢复日志损坏，请保留数据并联系维护人员。");
        if (Path.GetFileName(journal.BackupName) != journal.BackupName || !journal.BackupName.StartsWith("before-restore-", StringComparison.Ordinal))
            throw new IOException("恢复日志路径无效，已停止以保护数据。");
        if (!journal.Committed)
        {
            var previous = Path.Combine(root, "backup", journal.BackupName);
            if (File.Exists(Path.Combine(previous, "app.db")))
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path.Combine(dataDir, "app.db" + suffix));
                MoveDatabaseFiles(previous, dataDir);
            }
            foreach (var name in new[] { "photos", "attachments" })
            {
                var old = Path.Combine(previous, name);
                if (!Directory.Exists(old)) continue;
                var destination = Path.Combine(root, name);
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(old, destination);
            }
        }
        File.Delete(journalPath);
        TryDeleteFile(journalPath + ".tmp");
    }

    private static void MoveDatabaseFiles(string source, string destination)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path.Combine(source, "app.db" + suffix);
            if (File.Exists(file)) File.Move(file, Path.Combine(destination, "app.db" + suffix));
        }
    }

    private static async Task CopyLimitedAsync(Stream source, Stream destination, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total = checked(total + count);
            if (total > limit) throw new ApiException(413, limit == AttachmentStorage.MaximumBytes ? "单个附件不能超过 25 MB。" : "恢复压缩包超过 4 GiB，请使用已保留的备份目录恢复。");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public void MapBackupApi(WebApplication app)
    {
        RequestDelegate overview = context => context.Response.WriteAsJsonAsync(GetBackups(), BackupJsonContext.Default.BackupOverviewDto, contentType: null, cancellationToken: context.RequestAborted);
        app.MapGet("/api/backups", overview);
        RequestDelegate download = async context =>
        {
            var file = CreateBackupZip();
            try
            {
                context.Response.ContentType = "application/zip";
                context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = $"环保完整备份_{DateTime.Now:yyyyMMdd_HHmmss}.zip" }.ToString();
                await context.Response.SendFileAsync(file, context.RequestAborted);
            }
            finally { TryDeleteFile(file); }
        };
        app.MapGet("/api/backups/download", download);
        RequestDelegate prepare = async context =>
        {
            var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumRestoreBytes;
            if (context.Request.ContentLength > MaximumRestoreBytes) throw new ApiException(413, "恢复压缩包不能超过 4 GiB。");
            var contentType = context.Request.ContentType?.Split(';')[0].Trim();
            if (contentType is not ("application/zip" or "application/octet-stream")) throw new ApiException(415, "请上传 ZIP 格式的备份文件。");
            var result = await PrepareRestoreAsync(context.Request.Body, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(result, BackupJsonContext.Default.RestorePreviewDto, contentType: null, cancellationToken: context.RequestAborted);
        };
        app.MapPut("/api/backups/restore/prepare", prepare);
        RequestDelegate restore = async context =>
        {
            var request = await context.Request.ReadFromJsonAsync(BackupJsonContext.Default.RestoreRequest, context.RequestAborted) ?? throw new ApiException(400, "缺少恢复确认内容。");
            var result = Restore(request);
            await context.Response.WriteAsJsonAsync(result, BackupJsonContext.Default.RestoreResultDto, contentType: null, cancellationToken: context.RequestAborted);
        };
        app.MapPost("/api/backups/restore", restore);
    }
}
