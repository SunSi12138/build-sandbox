using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public static class PhotoStorage
{
    public const int MaximumUploadBytes = 10 * 1024 * 1024;
    public const int MaximumThumbnailBytes = 200 * 1024;

    public static string ResolvePath(string photosRoot, string photoId, string relativePath)
    {
        if (string.IsNullOrEmpty(photoId) || photoId.Length != 32 ||
            !Guid.TryParseExact(photoId, "N", out var parsedId) || parsedId.ToString("N") != photoId ||
            string.IsNullOrEmpty(relativePath) || relativePath.Length != 43 ||
            relativePath[6] != '/' || !relativePath.EndsWith(".jpg", StringComparison.Ordinal) ||
            !relativePath.AsSpan(7, 32).SequenceEqual(photoId.AsSpan()))
            throw new InvalidDataException("照片文件路径无效。");
        for (var index = 0; index < 6; index++)
            if (relativePath[index] is < '0' or > '9') throw new InvalidDataException("照片文件路径无效。");
        if (!DateOnly.TryParseExact(relativePath[..6] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("照片文件路径中的年月无效。");

        var root = Path.GetFullPath(photosRoot);
        var monthDirectory = Path.Combine(root, relativePath[..6]);
        var path = Path.Combine(monthDirectory, photoId + ".jpg");
        // Uploaded ZIPs and database paths may not redirect photo reads through links.
        foreach (var candidate in new[] { root, monthDirectory, path })
            if ((File.Exists(candidate) || Directory.Exists(candidate)) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("照片路径不能包含符号链接或目录联接。");
        return path;
    }
    public static string ResolveThumbnailPath(string photosRoot, string photoId, string relativePath)
    {
        var path = ResolvePath(photosRoot, photoId, relativePath);
        var thumbnail = path[..^4] + ".thumb.jpg";
        if (File.Exists(thumbnail) && (File.GetAttributes(thumbnail) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("照片路径不能包含符号链接或目录联接。");
        return thumbnail;
    }
}

public sealed partial class Store
{
    private const string PendingPhotoUploadSuffix = ".pending-upload";
    private const string PendingPhotoDeleteSuffix = ".pending-delete";
    private const string PhotoColumns = "p.id,p.width,p.height,p.phase,p.uploaded_at,p.inspection_id,p.issue_id,p.action_id,p.point_id,p.campaign_id,p.has_thumb";

    public PhotoUploadDto UploadPhoto(byte[] jpeg, string phase)
    {
        ValidatePhotoPhase(phase);
        if (jpeg.Length > PhotoStorage.MaximumUploadBytes) throw new ApiException(413, "单张照片不能超过 10 MB，请先压缩再上传。");
        JpegDimensions dimensions;
        try { dimensions = JpegMetadata.Read(jpeg); }
        catch (InvalidDataException error) { throw new ApiException(400, error.Message); }

        lock (_gate)
        {
            PrepareWrite();
            var now = DateTime.Now;
            var id = NewId();
            var relativePath = now.ToString("yyyyMM", CultureInfo.InvariantCulture) + "/" + id + ".jpg";
            var path = PhotoStorage.ResolvePath(PhotosDir, id, relativePath);
            var temporaryPath = path + PendingPhotoUploadSuffix;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var committed = false;
            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(jpeg);
                    file.Flush(flushToDisk: true);
                }
                using (var transaction = _db.BeginTransaction())
                {
                    using var insert = Command("""
                        INSERT INTO photos(id,phase,relative_path,width,height,uploaded_at)
                        VALUES($id,$phase,$path,$width,$height,$uploaded);
                        """, transaction, ("$id", id), ("$phase", phase), ("$path", relativePath),
                        ("$width", dimensions.Width), ("$height", dimensions.Height),
                        ("$uploaded", now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
                    insert.ExecuteNonQuery();
                    transaction.Commit();
                    committed = true;
                }
                // _gate excludes backup/restore until the committed file is in place.
                // A crash between commit and move is repaired by maintenance.
                File.Move(temporaryPath, path);
                return new PhotoUploadDto(id, dimensions.Width, dimensions.Height);
            }
            catch
            {
                if (!committed) TryDeletePhotoFile(temporaryPath);
                throw;
            }
        }
    }

    public FileStream OpenPhoto(string id, bool thumbnail = false)
    {
        lock (_gate)
        {
            using var command = Command("SELECT relative_path FROM photos WHERE id=$id;", null, ("$id", id));
            if (command.ExecuteScalar() is not string relativePath) throw new ApiException(404, "照片不存在。");
            var path = PhotoStorage.ResolvePath(PhotosDir, id, relativePath);
            if (thumbnail)
            {
                var thumb = PhotoStorage.ResolveThumbnailPath(PhotosDir, id, relativePath);
                if (File.Exists(thumb)) path = thumb;
            }
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException) { throw new ApiException(404, "照片文件不存在，请检查备份或恢复照片。"); }
            catch (DirectoryNotFoundException) { throw new ApiException(404, "照片文件不存在，请检查备份或恢复照片。"); }
        }
    }

    public void DeletePhoto(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            DeletePhotoCore(id);
        }
    }

    public void UploadThumbnail(string id, byte[] jpeg)
    {
        if (jpeg.Length > PhotoStorage.MaximumThumbnailBytes) throw new ApiException(413, "缩略图不能超过 200 KB。");
        try
        {
            var dimensions = JpegMetadata.Read(jpeg);
            if (Math.Max(dimensions.Width, dimensions.Height) > 360) throw new ApiException(400, "缩略图长边不能超过 360 像素。");
        }
        catch (InvalidDataException error) { throw new ApiException(400, error.Message); }
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("SELECT relative_path FROM photos WHERE id=$id;", null, ("$id", id));
            var relative = command.ExecuteScalar() as string ?? throw new ApiException(404, "照片不存在。");
            var path = PhotoStorage.ResolveThumbnailPath(PhotosDir, id, relative);
            var temporary = path + PendingPhotoUploadSuffix;
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(jpeg); stream.Flush(true); }
            // Never advertise a thumbnail until its file exists: a failed optional
            // thumbnail must not turn the next complete backup into a failure.
            File.Move(temporary, path, true);
            using (var update = Command("UPDATE photos SET has_thumb=1 WHERE id=$id;", null, ("$id", id))) update.ExecuteNonQuery();
        }
    }

    private void DeletePhotoCore(string id) => ExecuteWithPhotoDeletion([id], _ => { });

    private void ExecuteWithPhotoDeletion(string[] photoIds, Action<SqliteTransaction> mutation)
    {
        var moved = new List<(string Original, string Pending)>();
        try
        {
            foreach (var id in photoIds.Distinct(StringComparer.Ordinal))
            {
                using var command = Command("SELECT relative_path FROM photos WHERE id=$id;", null, ("$id", id));
                var relative = command.ExecuteScalar() as string ?? throw new ApiException(404, "照片不存在。");
                foreach (var path in new[] { PhotoStorage.ResolvePath(PhotosDir, id, relative), PhotoStorage.ResolveThumbnailPath(PhotosDir, id, relative) })
                {
                    if (!File.Exists(path)) continue;
                    var pending = path + PendingPhotoDeleteSuffix;
                    File.Move(path, pending, true);
                    moved.Add((path, pending));
                }
            }
            using var transaction = _db.BeginTransaction();
            foreach (var id in photoIds)
            {
                using var delete = Command("DELETE FROM photos WHERE id=$id;", transaction, ("$id", id));
                delete.ExecuteNonQuery();
            }
            mutation(transaction);
            transaction.Commit();
        }
        catch
        {
            foreach (var (original, pending) in moved)
                if (File.Exists(pending) && !File.Exists(original)) File.Move(pending, original);
            throw;
        }
        foreach (var (_, pending) in moved) TryDeletePhotoFile(pending);
    }

    private void AttachPhotos(string[]? photoIds, string phase, string? inspectionId, string? issueId,
        SqliteTransaction tx, string? actionId = null, string? pointId = null, string? campaignId = null)
    {
        ValidatePhotoPhase(phase);
        if (new[] { inspectionId, issueId, pointId, campaignId }.Count(id => id is not null) != 1)
            throw new ApiException(400, "照片必须关联到一个对象。");
        if (pointId is not null && phase != "point") throw new ApiException(400, "点位只能关联 point 阶段照片。");
        if (campaignId is not null && phase is not ("before" or "process" or "after")) throw new ApiException(400, "整治照片阶段无效。");
        if (issueId is not null && phase is not ("site" or "after" or "review")) throw new ApiException(400, "问题照片阶段无效。");
        if (inspectionId is not null && phase != "site") throw new ApiException(400, "排查记录只能关联现场照片。");
        if (actionId is not null)
        {
            if (issueId is null || phase == "site") throw new ApiException(400, "反馈或复查照片的关联信息无效。");
            using var action = Command("SELECT kind FROM issue_actions WHERE id=$action AND issue_id=$issue;", tx,
                ("$action", actionId), ("$issue", issueId));
            var kind = action.ExecuteScalar() as string;
            if (kind != (phase == "after" ? "feedback" : "review")) throw new ApiException(400, "照片阶段与反馈或复查记录不一致。");
        }
        if (photoIds is null || photoIds.Length == 0) return;
        if (photoIds.Length > 100) throw new ApiException(400, "每次最多关联 100 张照片。");
        var uniqueIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in photoIds)
            if (string.IsNullOrEmpty(id) || !uniqueIds.Add(id)) throw new ApiException(400, "照片列表包含空编号或重复照片。");

        foreach (var id in photoIds)
        {
            if (pointId is not null || campaignId is not null)
            {
                using var already = Command("SELECT COUNT(*) FROM photos WHERE id=$id AND phase=$phase AND (($point IS NOT NULL AND point_id=$point) OR ($campaign IS NOT NULL AND campaign_id=$campaign));", tx,
                    ("$id", id), ("$phase", phase), ("$point", pointId), ("$campaign", campaignId));
                if (Convert.ToInt32(already.ExecuteScalar()) > 0) continue;
            }
            string path;
            using (var existing = Command("""
                SELECT relative_path FROM photos
                WHERE id=$id AND phase=$phase AND inspection_id IS NULL AND issue_id IS NULL AND action_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;
                """, tx, ("$id", id), ("$phase", phase)))
            {
                if (existing.ExecuteScalar() is not string relativePath)
                    throw new ApiException(409, "照片不存在、阶段不符或已经关联，请重新上传照片。");
                path = PhotoStorage.ResolvePath(PhotosDir, id, relativePath);
            }
            if (!File.Exists(path)) throw new ApiException(409, "照片文件已丢失，请重新上传后再保存。");
            using var attach = Command("""
                UPDATE photos SET inspection_id=$inspection,issue_id=$issue,action_id=$action,point_id=$point,campaign_id=$campaign
                WHERE id=$id AND phase=$phase AND inspection_id IS NULL AND issue_id IS NULL AND action_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;
                """, tx, ("$inspection", inspectionId), ("$issue", issueId), ("$action", actionId), ("$point", pointId), ("$campaign", campaignId), ("$id", id), ("$phase", phase));
            if (attach.ExecuteNonQuery() != 1) throw new ApiException(409, "照片已经被关联，请重新选择照片。");
        }
        if (pointId is not null || campaignId is not null)
        {
            using var count = Command("SELECT COUNT(*) FROM photos WHERE phase=$phase AND (($point IS NOT NULL AND point_id=$point) OR ($campaign IS NOT NULL AND campaign_id=$campaign));", tx,
                ("$phase", phase), ("$point", pointId), ("$campaign", campaignId));
            var maximum = pointId is not null ? 9 : 20;
            if (Convert.ToInt32(count.ExecuteScalar()) > maximum) throw new ApiException(400, $"该阶段照片最多 {maximum} 张。");
        }
    }

    private PhotoDto[] ReadPhotos(string? inspectionId = null, string? issueId = null, string? actionId = null, bool includeIssuePhotos = false, string? pointId = null, string? campaignId = null)
    {
        if (inspectionId is null && issueId is null && actionId is null && pointId is null && campaignId is null) return [];
        using var command = Command($"""
            SELECT {PhotoColumns} FROM photos p
            WHERE ($inspection IS NULL OR p.inspection_id=$inspection
                OR ($includeIssues=1 AND EXISTS(SELECT 1 FROM issues i WHERE i.id=p.issue_id AND i.inspection_id=$inspection)))
              AND ($issue IS NULL OR p.issue_id=$issue)
              AND ($action IS NULL OR p.action_id=$action)
              AND ($point IS NULL OR p.point_id=$point) AND ($campaign IS NULL OR p.campaign_id=$campaign)
            ORDER BY CASE p.phase WHEN 'site' THEN 0 WHEN 'point' THEN 0 WHEN 'before' THEN 0 WHEN 'process' THEN 1 WHEN 'after' THEN 2 ELSE 3 END,p.uploaded_at,p.rowid;
            """, null, ("$inspection", inspectionId), ("$issue", issueId), ("$action", actionId), ("$includeIssues", includeIssuePhotos ? 1 : 0), ("$point", pointId), ("$campaign", campaignId));
        using var reader = command.ExecuteReader();
        var photos = new List<PhotoDto>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            photos.Add(new PhotoDto(id, reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), "/photos/" + id,
                reader.GetString(4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8), NullableString(reader, 9), reader.GetInt32(10) != 0));
        }
        return photos.ToArray();
    }

    public void CleanupOrphanPhotos()
    {
        lock (_gate)
        {
            RepairPendingPhotoOperations();
            var expiredIds = new List<string>();
            var cutoff = DateTime.Now.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            using (var command = Command("""
                SELECT id FROM photos WHERE inspection_id IS NULL AND issue_id IS NULL AND action_id IS NULL AND point_id IS NULL AND campaign_id IS NULL AND uploaded_at < $cutoff;
                """, null, ("$cutoff", cutoff)))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) expiredIds.Add(reader.GetString(0));
            foreach (var id in expiredIds)
            {
                try { DeletePhotoCore(id); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine($"照片 {id} 暂时无法清理，下次维护将重试：{error.Message}"); }
            }
        }
    }

    private void RepairPendingPhotoOperations()
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var pendingPath in Directory.EnumerateFiles(PhotosDir, "*.pending-*", enumeration))
        {
            var suffix = pendingPath.EndsWith(PendingPhotoUploadSuffix, StringComparison.Ordinal) ? PendingPhotoUploadSuffix
                : pendingPath.EndsWith(PendingPhotoDeleteSuffix, StringComparison.Ordinal) ? PendingPhotoDeleteSuffix : null;
            if (suffix is null) continue;
            try
            {
                var originalPath = pendingPath[..^suffix.Length];
                var relativePath = Path.GetRelativePath(PhotosDir, originalPath).Replace(Path.DirectorySeparatorChar, '/');
                var thumbnail = originalPath.EndsWith(".thumb.jpg", StringComparison.Ordinal);
                var id = Path.GetFileNameWithoutExtension(originalPath);
                if (thumbnail) { id = id[..^6]; relativePath = relativePath[..^10] + ".jpg"; }
                var validatedPath = thumbnail ? PhotoStorage.ResolveThumbnailPath(PhotosDir, id, relativePath) : PhotoStorage.ResolvePath(PhotosDir, id, relativePath);
                using var command = Command("SELECT relative_path FROM photos WHERE id=$id AND ($thumbnail=0 OR has_thumb=1);", null, ("$id", id), ("$thumbnail", thumbnail ? 1 : 0));
                var storedPath = command.ExecuteScalar() as string;
                if (storedPath == relativePath && !File.Exists(validatedPath)) File.Move(pendingPath, validatedPath);
                else TryDeletePhotoFile(pendingPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { Console.Error.WriteLine($"一项照片文件操作尚未完成，下次维护将重试：{error.Message}"); }
        }
    }

    private static void ValidatePhotoPhase(string phase)
    {
        if (phase is not ("site" or "after" or "review" or "point" or "before" or "process")) throw new ApiException(400, "照片阶段无效。");
    }

    private static void TryDeletePhotoFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"照片临时文件将在下次维护时重试清理：{error.Message}"); }
    }
}

public static class PhotoApi
{
    public static void Map(WebApplication app, Store store)
    {
        RequestDelegate upload = async context =>
        {
            var request = context.Request;
            var phase = request.Query["phase"].ToString();
            if (phase is not ("site" or "after" or "review" or "point" or "before" or "process"))
                throw new ApiException(400, "照片阶段无效。");
            if (request.ContentLength > PhotoStorage.MaximumUploadBytes)
                throw new ApiException(413, "单张照片不能超过 10 MB，请先压缩再上传。");
            var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = PhotoStorage.MaximumUploadBytes;
            var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
            if (!string.IsNullOrEmpty(mediaType) &&
                !mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new ApiException(415, "请上传 JPEG 原始字节，不要使用 multipart 表单。");

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var body = new MemoryStream();
                while (true)
                {
                    var allowed = Math.Min(buffer.Length, PhotoStorage.MaximumUploadBytes - (int)body.Length + 1);
                    var count = await request.Body.ReadAsync(buffer.AsMemory(0, allowed), context.RequestAborted);
                    if (count == 0) break;
                    if (body.Length + count > PhotoStorage.MaximumUploadBytes)
                        throw new ApiException(413, "单张照片不能超过 10 MB，请先压缩再上传。");
                    body.Write(buffer, 0, count);
                }
                var photo = store.UploadPhoto(body.ToArray(), phase);
                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsJsonAsync(photo, AppJsonContext.Default.PhotoUploadDto,
                    contentType: null, cancellationToken: context.RequestAborted);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        };

        RequestDelegate read = async context =>
        {
            var id = context.Request.RouteValues["id"] as string ?? "";
            var stream = store.OpenPhoto(id, context.Request.Query["size"] == "thumb");
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, max-age=86400";
            await Results.File(stream, "image/jpeg", enableRangeProcessing: true).ExecuteAsync(context);
        };

        RequestDelegate delete = context =>
        {
            if (!string.Equals(context.Request.Query["confirm"].ToString(), "true", StringComparison.Ordinal))
                throw new ApiException(400, "删除照片需要二次确认，请确认后再操作。");
            store.DeletePhoto(context.Request.RouteValues["id"] as string ?? "");
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        };
        RequestDelegate thumbnail = async context =>
        {
            if (context.Request.ContentLength > PhotoStorage.MaximumThumbnailBytes) throw new ApiException(413, "缩略图不能超过 200 KB。");
            var sizeLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeLimit is { IsReadOnly: false }) sizeLimit.MaxRequestBodySize = PhotoStorage.MaximumThumbnailBytes;
            using var bytes = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
            {
                if (bytes.Length + count > PhotoStorage.MaximumThumbnailBytes) throw new ApiException(413, "缩略图不能超过 200 KB。");
                bytes.Write(buffer, 0, count);
            }
            store.UploadThumbnail(context.Request.RouteValues["id"] as string ?? "", bytes.ToArray());
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        };
        app.MapPut("/api/photos", upload);
        app.MapPut("/api/photos/{id}/thumb", thumbnail);
        app.MapGet("/photos/{id}", read);
        app.MapDelete("/api/photos/{id}", delete);
    }
}
