using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

internal sealed class FileReceiver : IDisposable
{
    public const int ChunkSize = 2 * 1024 * 1024;
    private const long MaxFileSize = 10L * 1024 * 1024 * 1024;
    private const int MaxSessions = 32;
    private const int MaxReceivers = 8;
    private readonly string stage;
    private readonly string address;
    private readonly int port;
    private readonly FileStream directoryLock;
    private readonly object sessionsLock = new();
    private readonly Dictionary<Guid, Upload> sessions = new();
    private readonly SemaphoreSlim receivers = new(MaxReceivers);
    private readonly SemaphoreSlim publish = new(1);
    private readonly Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
    private readonly Timer cleanupTimer;
    public string DirectoryPath { get; }

    public FileReceiver(string root, string address, int port)
    {
        DirectoryPath = root;
        this.address = address;
        this.port = port;
        Directory.CreateDirectory(root);
        stage = Path.Combine(root, ".file-assistant");
        Directory.CreateDirectory(stage);
        if (File.GetAttributes(stage).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("暂存目录不能为符号链接");
        directoryLock = new FileStream(Path.Combine(stage, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (string path in Directory.EnumerateFiles(stage, "*.part"))
                File.Delete(path);
            foreach (string path in Directory.EnumerateFiles(root))
            {
                if (!IsRegular(path)) continue;
                try
                {
                    using var f = File.OpenRead(path);
                    AddIndex(Convert.ToHexString(SHA256.HashData(f)), path);
                }
                catch (IOException) { Console.Error.WriteLine("无法索引：" + Path.GetFileName(path)); }
                catch (UnauthorizedAccessException) { Console.Error.WriteLine("无法索引：" + Path.GetFileName(path)); }
            }
            cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }
        catch
        {
            directoryLock.Dispose();
            throw;
        }
    }

    public async Task HandleAsync(HttpContext c)
    {
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers["X-Content-Type-Options"] = "nosniff";
        c.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'";
        c.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!string.Equals(c.Request.Host.Host, address, StringComparison.Ordinal) ||
            (c.Request.Host.Port ?? 80) != port)
        {
            await Text(c, 400, "invalid host");
            return;
        }
        var path = c.Request.Path.Value;
        if (HttpMethods.IsGet(c.Request.Method) && path == "/")
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.WriteAsync(UploadPage.Html, c.RequestAborted);
            return;
        }
        if (HttpMethods.IsGet(c.Request.Method) && path == "/health")
        {
            await Text(c, 200, "ok");
            return;
        }
        if (!HttpMethods.IsPost(c.Request.Method) || path is not ("/api/init" or "/api/chunk" or "/api/finish"))
        {
            await Text(c, 404, "not found");
            return;
        }

        // A custom header prevents ordinary cross-site form submissions; no CORS is enabled.
        string origin = c.Request.Headers.Origin.ToString();
        if (c.Request.Headers["X-File-Assistant"] != "1" ||
            c.Request.Headers["Sec-Fetch-Site"] == "cross-site" ||
            (origin.Length != 0 && origin != $"http://{address}:{port}"))
        {
            await Text(c, 403, "invalid origin");
            return;
        }
        if (!Guid.TryParseExact(c.Request.Query["id"].ToString(), "N", out Guid id))
        {
            await Text(c, 400, "invalid id");
            return;
        }
        if (!await receivers.WaitAsync(0, c.RequestAborted))
        {
            await Text(c, 429, "busy");
            return;
        }
        Upload? upload = null;
        bool ownsGate = false;
        try
        {
            int status = 200;
            lock (sessionsLock)
            {
                if (!sessions.TryGetValue(id, out upload))
                {
                    if (path != "/api/init") status = 404;
                    else if (!long.TryParse(c.Request.Query["size"].ToString(), NumberStyles.None,
                        CultureInfo.InvariantCulture, out long size) || size < 0) status = 400;
                    else if (size > MaxFileSize) status = 413;
                    else if (sessions.Values.Count(u => u.Result is null) >= MaxSessions) status = 429;
                    else
                    {
                        string name = SafeName(c.Request.Query["name"].ToString());
                        upload = new Upload(name, size, Path.Combine(stage, id.ToString("N") + ".part"));
                        using (File.Open(upload.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                        sessions.Add(id, upload);
                    }
                }
                if (status == 200 && upload is not null)
                {
                    ownsGate = upload.Gate.Wait(0);
                    if (!ownsGate) status = 429;
                    else upload.Touched = DateTime.UtcNow;
                }
            }
            if (status != 200 || upload is null)
            {
                await Text(c, status, status == 429 ? "busy" : status == 413 ? "file too large" : "invalid upload");
                return;
            }

            if (path == "/api/init" &&
                (!long.TryParse(c.Request.Query["size"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long requestedSize) ||
                 requestedSize != upload.Size || SafeName(c.Request.Query["name"].ToString()) != upload.Name))
            {
                await Text(c, 409, "id conflict");
                return;
            }

            if (upload.Result is not null)
            {
                await Text(c, 200, upload.Result);
                return;
            }

            if (path == "/api/init")
                await Text(c, 200, Number(upload.Offset));
            else if (path == "/api/chunk")
                await Append(c, upload);
            else if (upload.Offset != upload.Size)
                await Text(c, 409, "incomplete");
            else
            {
                // Once all bytes arrived, finalize even if the client's connection drops.
                string result = await FinalizeUpload(upload);
                upload.Result = result;
                await Text(c, 200, result);
            }
        }
        catch (OperationCanceledException) when (c.RequestAborted.IsCancellationRequested) { }
        catch (BadHttpRequestException e)
        {
            if (!c.Response.HasStarted) await Text(c, e.StatusCode, "invalid request");
        }
        catch (IOException e)
        {
            if (!c.RequestAborted.IsCancellationRequested && !c.Response.HasStarted)
            {
                Console.Error.WriteLine("文件写入失败：" + e.Message);
                await Text(c, 507, "storage error");
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (!c.Response.HasStarted) await Text(c, 507, "storage error");
        }
        finally
        {
            if (ownsGate && upload is not null)
            {
                upload.Touched = DateTime.UtcNow;
                upload.Gate.Release();
            }
            receivers.Release();
        }
    }

    private static async Task Append(HttpContext c, Upload upload)
    {
        if (!long.TryParse(c.Request.Query["offset"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long offset))
        {
            await Text(c, 400, "invalid offset");
            return;
        }
        // An already committed chunk may be resent after losing its response.
        if (offset != upload.Offset || upload.Offset == upload.Size)
        {
            await Text(c, 200, Number(upload.Offset));
            return;
        }
        int expected = (int)Math.Min(ChunkSize, upload.Size - upload.Offset);
        if (c.Request.ContentLength is long length && length != expected)
        {
            await Text(c, 400, "invalid chunk length");
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous);
            file.SetLength(offset);
            file.Position = offset;
            try
            {
                int remaining = expected;
                while (remaining > 0)
                {
                    int read = await c.Request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), c.RequestAborted);
                    if (read == 0) throw new IOException("incomplete chunk");
                    await file.WriteAsync(buffer.AsMemory(0, read), c.RequestAborted);
                    remaining -= read;
                }
                if (await c.Request.Body.ReadAsync(buffer.AsMemory(0, 1), c.RequestAborted) != 0)
                    throw new BadHttpRequestException("invalid chunk length", 400);
                await file.FlushAsync(c.RequestAborted);
                upload.Offset += expected;
            }
            catch
            {
                file.SetLength(offset);
                file.Flush();
                throw;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        await Text(c, 200, Number(upload.Offset));
    }

    private async Task<string> FinalizeUpload(Upload upload)
    {
        string hash = await HashFile(upload.Path);
        await publish.WaitAsync();
        try
        {
            if (index.TryGetValue(hash, out var paths))
            {
                foreach (string path in paths.ToArray())
                {
                    if (!IsRegular(path))
                    {
                        paths.Remove(path);
                        continue;
                    }
                    // The owner may have changed a saved file while the service was running.
                    if (new FileInfo(path).Length == upload.Size && await HashFile(path) == hash)
                    {
                        File.Delete(upload.Path);
                        Console.WriteLine("已存在：" + upload.Name);
                        return "duplicate";
                    }
                    paths.Remove(path);
                }
            }

            string stem = Path.GetFileNameWithoutExtension(upload.Name);
            string extension = Path.GetExtension(upload.Name);
            for (int n = 0; ; n++)
            {
                string name = n == 0 ? upload.Name : $"{stem} ({n}){extension}";
                string destination = Path.Combine(DirectoryPath, name);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                try
                {
                    File.Move(upload.Path, destination, overwrite: false);
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { continue; }
                AddIndex(hash, destination);
                Console.WriteLine("已接收：" + name);
                return "saved";
            }
        }
        finally { publish.Release(); }
    }

    private static async Task<string> HashFile(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    private void AddIndex(string hash, string path)
    {
        if (!index.TryGetValue(hash, out var paths)) index.Add(hash, paths = []);
        paths.Add(path);
    }

    private static bool IsRegular(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void Cleanup()
    {
        lock (sessionsLock)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var pair in sessions.ToArray())
            {
                var upload = pair.Value;
                if (!upload.Gate.Wait(0)) continue;
                try
                {
                    bool expired = now - upload.Touched > (upload.Result is null ? TimeSpan.FromHours(24) : TimeSpan.FromHours(1));
                    if (!expired && !(sessions.Count > 1024 && upload.Result is not null)) continue;
                    File.Delete(upload.Path);
                    sessions.Remove(pair.Key);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { upload.Gate.Release(); }
            }
        }
    }

    private static string SafeName(string supplied)
    {
        string name = Path.GetFileName(supplied.Replace('\\', '/'));
        var b = new StringBuilder();
        int count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (count + rune.Utf8SequenceLength > 180) break;
            count += rune.Utf8SequenceLength;
            if (rune.Value < 32 || rune.Value == 127 || "<>:\"/\\|?*".Contains((char)rune.Value) ||
                rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
                b.Append('_');
            else b.Append(rune.ToString());
        }
        string safe = b.ToString().Trim().TrimStart('.').TrimEnd('.', ' ');
        if (safe.Length == 0) return "file";
        string first = safe.Split('.')[0].ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            (first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) ||
             first.StartsWith("LPT", StringComparison.Ordinal)) && first[3] is >= '1' and <= '9'))
            safe = "_" + safe;
        return safe;
    }

    private static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static Task Text(HttpContext c, int status, string body)
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = "text/plain; charset=utf-8";
        return c.Response.WriteAsync(body, c.RequestAborted);
    }

    public void Dispose()
    {
        cleanupTimer.Dispose();
        directoryLock.Dispose();
    }

    private sealed class Upload(string name, long size, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public long Size { get; } = size;
        public long Offset { get; set; }
        public string? Result { get; set; }
        public DateTime Touched { get; set; } = DateTime.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1);
    }
}
