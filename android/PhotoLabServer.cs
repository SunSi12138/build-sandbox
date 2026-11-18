using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace StreetEcoTool.Mobile;

/// <summary>Single-purpose, loopback-only image upload echo server. No database or business endpoints.</summary>
internal sealed class PhotoLabServer : IAsyncDisposable
{
    private const int MaxBody = 22 * 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _accept;
    private readonly string _uploads = Path.Combine(FileSystem.CacheDirectory, "photo-lab-uploads");
    public Uri? Origin { get; private set; }

    public void Start()
    {
        Directory.CreateDirectory(_uploads);
        _listener.Start();
        Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client, _stop.Token));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException) when (_stop.IsCancellationRequested) { return; }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var headers = new List<byte>(512);
                var one = new byte[1];
                while (headers.Count < 16 * 1024)
                {
                    if (await stream.ReadAsync(one, token) == 0) return;
                    headers.Add(one[0]);
                    int n = headers.Count;
                    if (n > 3 && headers[n - 4] == 13 && headers[n - 3] == 10 &&
                        headers[n - 2] == 13 && headers[n - 1] == 10) break;
                }
                if (headers.Count >= 16 * 1024)
                {
                    await Reply(stream, 431, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "请求头超长" }), token);
                    return;
                }
                var lines = Encoding.ASCII.GetString([.. headers]).Split("\r\n");
                var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3) return;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                if (!fields.TryGetValue("Host", out var host) || host != "127.0.0.1:" + Origin!.Port ||
                    fields.ContainsKey("Transfer-Encoding"))
                {
                    await Reply(stream, 400, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "Host 或请求编码不受支持" }), token);
                    return;
                }
                var target = parts[1].Split('?', 2)[0];
                if (parts[0] == "GET" && target is "/" or "/photo-lab.html")
                {
                    await using var html = await FileSystem.OpenAppPackageFileAsync("photo-lab.html");
                    using var buffer = new MemoryStream();
                    await html.CopyToAsync(buffer, token);
                    await Reply(stream, 200, "text/html; charset=utf-8", buffer.ToArray(), token);
                    return;
                }
                if (parts[0] == "GET" && target == "/api/status")
                {
                    await Reply(stream, 200, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { ready = true, mode = "MAUI CoreCLR loopback" }), token);
                    return;
                }
                if (parts[0] != "PUT" || target != "/api/upload")
                {
                    await Reply(stream, 404, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "仅提供照片上传测试接口" }), token);
                    return;
                }
                if (!fields.TryGetValue("Content-Length", out var size) ||
                    !int.TryParse(size, out var count) || count < 1 || count > MaxBody)
                {
                    await Reply(stream, 413, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "上传必须声明 1B–22MB 的 Content-Length" }), token);
                    return;
                }
                var bytes = new byte[count];
                await stream.ReadExactlyAsync(bytes, token);
                var mime = fields.GetValueOrDefault("Content-Type") ?? "";
                if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    await Reply(stream, 415, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { error = "只允许 image/* 文件", type = mime }), token);
                    return;
                }
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var path = Path.Combine(_uploads, Guid.NewGuid().ToString("N") + ".image");
                await File.WriteAllBytesAsync(path, bytes, token);
                Android.Util.Log.Info("PhotoLab", $"UPLOAD_OK size={count} sha256={hash} mime={mime}");
                await Reply(stream, 200, "application/json",
                    JsonSerializer.SerializeToUtf8Bytes(new { ok = true, bytes = count, mime, sha256 = hash, saved = true }),
                    token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidOperationException)
            {
                Android.Util.Log.Warn("PhotoLab", "HTTP failed: " + e.Message);
            }
            catch (Exception e)
            {
                Android.Util.Log.Error("PhotoLab", "HTTP fatal: " + e);
            }
        }
    }

    private static async Task Reply(Stream stream, int code, string mime, byte[] bytes, CancellationToken ct)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 413 => "Payload Too Large", 415 => "Unsupported Media Type", 431 => "Request Header Fields Too Large", _ => "Error" };
        var headers = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {mime}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
        await stream.WriteAsync(bytes, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_accept is not null) await _accept;
        _stop.Dispose();
    }
}
