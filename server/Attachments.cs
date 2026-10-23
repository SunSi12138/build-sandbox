using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

namespace StreetEcoTool;

public static class AttachmentStorage
{
    public const int MaximumBytes = 25 * 1024 * 1024;
    public static string ContentType(string extension) => extension switch
    {
        ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".doc" => "application/msword",
        ".xls" => "application/vnd.ms-excel", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".txt" => "text/plain", ".zip" => "application/zip",
        _ => throw new ApiException(400, "附件支持 PDF、Word、Excel、图片、TXT 和 ZIP 文件。")
    };
    public static string ResolvePath(string root, string id, string relative)
    {
        var parts = relative.Split('/');
        if (parts.Length != 2 || parts[0].Length != 6 || !DateOnly.TryParseExact(parts[0] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !Guid.TryParseExact(id, "N", out var guid) || guid.ToString("N") != id || parts[1] != id + Path.GetExtension(parts[1]) || parts[1].Contains('\\'))
            throw new InvalidDataException("附件存储路径无效。");
        _ = ContentType(Path.GetExtension(parts[1]));
        var directory = Path.Combine(Path.GetFullPath(root), parts[0]);
        var file = Path.Combine(directory, parts[1]);
        if ((Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is not null) || (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is not null) || (File.Exists(file) && new FileInfo(file).LinkTarget is not null))
            throw new InvalidDataException("附件路径不能包含符号链接。");
        return file;
    }
    public static void Validate(byte[] bytes, string extension)
    {
        _ = ContentType(extension);
        if (bytes.Length == 0) throw new ApiException(400, "附件不能为空。");
        if (bytes.Length > MaximumBytes) throw new ApiException(413, "单个附件不能超过 25 MB。");
        ReadOnlySpan<byte> value = bytes;
        var valid = extension switch
        {
            ".pdf" => value.StartsWith("%PDF-"u8),
            ".docx" or ".xlsx" or ".zip" => value.StartsWith(new byte[] { 0x50, 0x4b, 3, 4 }) || value.StartsWith(new byte[] { 0x50, 0x4b, 5, 6 }),
            ".doc" or ".xls" => value.StartsWith(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }),
            ".png" => value.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => value.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            ".txt" => true,
            _ => false
        };
        if (!valid) throw new ApiException(400, "文件内容与扩展名不一致。");
    }
}

public sealed partial class Store
{
    private const string AttachmentColumns = "id,point_id,name,notes,size,content_type,uploaded_at,sha256";
    private static AttachmentDto ReadAttachment(Microsoft.Data.Sqlite.SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt64(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(5) is "application/pdf" or "image/jpeg" or "image/png");
    public AttachmentDto[] GetAttachments(string pointId)
    {
        lock (_gate)
        {
            RequireOwner("point", pointId);
            using var command = Command($"SELECT {AttachmentColumns} FROM attachments WHERE point_id=$point ORDER BY uploaded_at DESC,rowid DESC;", null, ("$point", pointId));
            using var reader = command.ExecuteReader(); var list = new List<AttachmentDto>();
            while (reader.Read()) list.Add(ReadAttachment(reader)); return list.ToArray();
        }
    }
    public AttachmentDto GetAttachment(string id)
    {
        lock (_gate)
        {
            using var command = Command($"SELECT {AttachmentColumns} FROM attachments WHERE id=$id;", null, ("$id", id));
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw new ApiException(404, "附件不存在。"); return ReadAttachment(reader);
        }
    }
    private static string AttachmentName(string value)
    {
        var name = Text(value, "文件名称", 200, true);
        if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new ApiException(400, "文件名称不能包含路径。");
        return name;
    }
    public AttachmentDto UploadAttachment(string pointId, string filename, byte[] bytes, string? notes = null)
    {
        var name = AttachmentName(filename); var extension = Path.GetExtension(name).ToLowerInvariant(); AttachmentStorage.Validate(bytes, extension);
        lock (_gate)
        {
            PrepareWrite(); RequireOwner("point", pointId);
            var id = NewId(); var relative = DateTime.Now.ToString("yyyyMM", CultureInfo.InvariantCulture) + "/" + id + extension;
            var file = AttachmentStorage.ResolvePath(AttachmentsDir, id, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var pending = file + ".pending-upload"; var committed = false;
            try
            {
                using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes); output.Flush(true); }
                using var tx = _db.BeginTransaction();
                using (var add = Command("INSERT INTO attachments VALUES($id,$point,$name,$notes,$path,$size,$type,$time,$sha);", tx,
                    ("$id", id), ("$point", pointId), ("$name", name), ("$notes", Text(notes, "附件备注", 2000)), ("$path", relative),
                    ("$size", bytes.Length), ("$type", AttachmentStorage.ContentType(extension)), ("$time", DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)), ("$sha", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()))) add.ExecuteNonQuery();
                File.Move(pending, file); tx.Commit(); committed = true; return GetAttachment(id);
            }
            finally { if (!committed) { TryDeleteFile(pending); TryDeleteFile(file); } }
        }
    }
    public AttachmentDto UpdateAttachment(string id, AttachmentUpdateRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var old = GetAttachment(id); var name = AttachmentName(request.Name);
            if (!string.Equals(Path.GetExtension(name), Path.GetExtension(old.Name), StringComparison.OrdinalIgnoreCase)) throw new ApiException(400, "重命名不能更改文件扩展名。");
            using var update = Command("UPDATE attachments SET name=$name,notes=$notes WHERE id=$id;", null, ("$id", id), ("$name", name), ("$notes", Text(request.Notes, "附件备注", 2000))); update.ExecuteNonQuery(); return GetAttachment(id);
        }
    }
    public FileStream OpenAttachment(string id)
    {
        lock (_gate)
        {
            using var command = Command("SELECT relative_path FROM attachments WHERE id=$id;", null, ("$id", id));
            var relative = command.ExecuteScalar() as string ?? throw new ApiException(404, "附件不存在。");
            var file = AttachmentStorage.ResolvePath(AttachmentsDir, id, relative);
            try { return new(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (IOException) { throw new ApiException(404, "附件文件缺失，请检查完整备份。"); }
        }
    }
    public void DeleteAttachment(string id)
    {
        lock (_gate)
        {
            PrepareWrite(); _ = GetAttachment(id);
            using var query = Command("SELECT relative_path FROM attachments WHERE id=$id;", null, ("$id", id));
            var file = AttachmentStorage.ResolvePath(AttachmentsDir, id, (string)query.ExecuteScalar()!); var pending = file + ".pending-delete";
            var moved = File.Exists(file);
            if (moved) File.Move(file, pending, true);
            try
            {
                using var tx = _db.BeginTransaction(); using var delete = Command("DELETE FROM attachments WHERE id=$id;", tx, ("$id", id)); delete.ExecuteNonQuery(); tx.Commit();
            }
            catch { if (moved && File.Exists(pending)) File.Move(pending, file, true); throw; }
            TryDeleteFile(pending);
        }
    }
    private void RecoverAttachmentFiles()
    {
        foreach (var pending in Directory.EnumerateFiles(AttachmentsDir, "*.pending-*", SearchOption.AllDirectories))
        {
            var suffix = pending.EndsWith(".pending-delete", StringComparison.Ordinal) ? ".pending-delete" : pending.EndsWith(".pending-upload", StringComparison.Ordinal) ? ".pending-upload" : "";
            if (suffix.Length == 0) continue;
            var file = pending[..^suffix.Length]; var relative = Path.GetRelativePath(AttachmentsDir, file).Replace('\\', '/'); var id = Path.GetFileNameWithoutExtension(file);
            _ = AttachmentStorage.ResolvePath(AttachmentsDir, id, relative);
            using var query = Command("SELECT COUNT(*) FROM attachments WHERE id=$id AND relative_path=$path;", null, ("$id", id), ("$path", relative));
            if (Convert.ToInt32(query.ExecuteScalar()) > 0 && !File.Exists(file)) File.Move(pending, file);
            else TryDeleteFile(pending);
        }
    }
    public void MapAttachmentApi(WebApplication app)
    {
        RequestDelegate list = context => Api.Write(context, GetAttachments(Api.Id(context)), AppJsonContext.Default.AttachmentDtoArray);
        RequestDelegate upload = async context =>
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = AttachmentStorage.MaximumBytes;
            if (context.Request.ContentLength > AttachmentStorage.MaximumBytes) throw new ApiException(413, "单个附件不能超过 25 MB。");
            using var buffer = new MemoryStream(); await CopyLimitedAsync(context.Request.Body, buffer, AttachmentStorage.MaximumBytes, context.RequestAborted);
            var result = UploadAttachment(Api.Id(context), context.Request.Query["name"].ToString(), buffer.ToArray());
            await Api.Write(context, result, AppJsonContext.Default.AttachmentDto, 201);
        };
        RequestDelegate download = async context =>
        {
            var metadata = GetAttachment(Api.Id(context)); using var stream = OpenAttachment(metadata.Id);
            var preview = context.Request.Query["preview"] == "true" && metadata.CanPreview;
            context.Response.ContentType = metadata.ContentType;
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(preview ? "inline" : "attachment") { FileNameStar = metadata.Name }.ToString();
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "sandbox";
            context.Response.ContentLength = metadata.Size;
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        };
        RequestDelegate update = async context => await Api.Write(context, UpdateAttachment(Api.Id(context), await Api.Read(context, AppJsonContext.Default.AttachmentUpdateRequest)), AppJsonContext.Default.AttachmentDto);
        RequestDelegate delete = context => { Api.RequireDeleteConfirmation(context); DeleteAttachment(Api.Id(context)); context.Response.StatusCode = 204; return Task.CompletedTask; };
        app.MapGet("/api/points/{id}/attachments", list); app.MapPut("/api/points/{id}/attachments", upload);
        app.MapGet("/api/attachments/{id}", download); app.MapPut("/api/attachments/{id}", update); app.MapDelete("/api/attachments/{id}", delete);
    }
}
