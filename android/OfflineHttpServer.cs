using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StreetEcoTool;
using Microsoft.Maui.Storage;

namespace StreetEcoTool.Mobile;

internal sealed class OfflineRequest(
    string method, string path, Dictionary<string, List<string>> query,
    Dictionary<string, string> headers, byte[] body)
{
    public string Method { get; } = method;
    public string Path { get; } = path;
    public byte[] Body { get; } = body;
    public string Header(string name) => headers.TryGetValue(name, out var value) ? value : "";
    public string? Query(string name) => query.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
    public string[] QueryValues(string name) => query.TryGetValue(name, out var values) ? [.. values] : [];
    public int Number(string name, int fallback) => int.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    public bool Flag(string name) => Query(name) is "true" or "1";
    public bool Confirmed => Query("confirm") == "true";
}

internal sealed record OfflineResponse(int Status, string Type, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static OfflineResponse Json(byte[] bytes, int status = 200) => new(status, "application/json; charset=utf-8", bytes);
    public static OfflineResponse Text(string text, int status = 200) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
    public static OfflineResponse Empty(int status = 204) => new(status, "application/octet-stream", []);
}

/// <summary>
/// Small loopback-only HTTP transport; no ASP.NET shared-runtime requirement.
/// It serves bundled Nuxt files and routes APIs into the original SQLite Store.
/// </summary>
internal sealed class OfflineHttpServer : IAsyncDisposable
{
    private const int HeaderLimit = 16384;
    private const int BodyLimit = 26 * 1024 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Store _store;
    private Task? _acceptTask;
    private bool _started;
    public Uri? Address { get; private set; }
    public Store Data => _store;

    public OfflineHttpServer(string appDataPath)
    {
        Directory.CreateDirectory(appDataPath);
        _store = new Store(Path.Combine(appDataPath, "data"));
        Directory.CreateDirectory(_store.OfflineTilesRoot);
    }

    public void Start()
    {
        if (_started) return;
        _listener.Start();
        _started = true;
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = new Uri($"http://127.0.0.1:{port}/");
        _acceptTask = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _ = Task.Run(() => ProcessAsync(client, _shutdown.Token));
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (SocketException) when (_shutdown.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                var request = await ReceiveAsync(stream, token);
                if (request is null) return;
                OfflineResponse result;
                try
                {
                    result = await RouteAsync(request, token);
                }
                catch (ApiException error)
                {
                    result = OfflineResponse.Json(MobileApi.Serialize(new ErrorDto(error.Message)), error.StatusCode);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                {
                    result = OfflineResponse.Json(MobileApi.Serialize(new ErrorDto("请求或本地文件无效：" + error.Message)), 400);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine(error);
                    result = OfflineResponse.Json(MobileApi.Serialize(new ErrorDto("本地服务执行失败，请检查手机存储空间。")), 500);
                }
                await RespondAsync(stream, result, token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine(error.Message);
            }
        }
    }

    private static async Task<OfflineRequest?> ReceiveAsync(Stream stream, CancellationToken token)
    {
        var header = new List<byte>();
        var single = new byte[1];
        while (header.Count < HeaderLimit)
        {
            if (await stream.ReadAsync(single, token) == 0) return null;
            header.Add(single[0]);
            var count = header.Count;
            if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 &&
                header[count - 2] == 13 && header[count - 1] == 10) break;
        }
        if (header.Count >= HeaderLimit) throw new IOException("请求头超过限制。");
        var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[1].StartsWith('/'))
            throw new IOException("请求行无效。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            var sep = line.IndexOf(':');
            if (sep > 0) headers[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }
        // Reject malformed framing rather than treating invalid Content-Length as an
        // empty body. The listener is private to loopback, not an HTTP proxy.
        if (headers.TryGetValue("Host", out var host) &&
            !(host.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
              host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("仅允许访问本机服务。");
        if (headers.ContainsKey("Transfer-Encoding")) throw new IOException("暂不支持流式请求体。");
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var raw) &&
            !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            throw new IOException("请求长度无效。");
        if (length < 0 || length > BodyLimit) throw new IOException("请求体超过 26 MB。");
        var bytes = new byte[length];
        if (length > 0) await stream.ReadExactlyAsync(bytes, token);

        var requestUri = new Uri("http://127.0.0.1" + requestLine[1]);
        var path = Uri.UnescapeDataString(requestUri.AbsolutePath);
        if (path.Contains('\\') || path.Split('/').Any(part => part == ".."))
            throw new IOException("非法文件路径。");
        var query = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            var key = Uri.UnescapeDataString(split[0].Replace('+', ' '));
            var value = split.Length == 2 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : "";
            if (!query.TryGetValue(key, out var values)) query[key] = values = [];
            values.Add(value);
        }
        return new OfflineRequest(requestLine[0], path, query, headers, bytes);
    }

    private static async Task RespondAsync(Stream stream, OfflineResponse response, CancellationToken token)
    {
        var label = response.Status switch
        {
            200 => "OK", 201 => "Created", 204 => "No Content", 206 => "Partial Content",
            400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            409 => "Conflict", 413 => "Payload Too Large", 415 => "Unsupported Media Type",
            416 => "Range Not Satisfiable", 501 => "Not Implemented", _ => "Internal Server Error"
        };
        var builder = new StringBuilder($"HTTP/1.1 {response.Status} {label}\r\n");
        builder.Append("Connection: close\r\nX-Content-Type-Options: nosniff\r\n");
        builder.Append("Cache-Control: no-store\r\n");
        builder.Append("Content-Type: ").Append(response.Type).Append("\r\n");
        builder.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        if (response.Headers is not null)
            foreach (var item in response.Headers)
                builder.Append(item.Key).Append(": ").Append(item.Value).Append("\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), token);
        if (response.Body.Length > 0) await stream.WriteAsync(response.Body, token);
    }

    private async Task<OfflineResponse> RouteAsync(OfflineRequest request, CancellationToken token)
    {
        if (request.Path.StartsWith("/api/map/archive/", StringComparison.Ordinal))
            return await OfflineMap.ArchiveAsync(_store, request, token);
        if (request.Path.StartsWith("/api/map/style/", StringComparison.Ordinal))
            return OfflineMap.Style(_store, request);
        if (request.Path.StartsWith("/api/map/tiles/", StringComparison.Ordinal))
            return OfflineMap.Tile(_store, request);
        if (request.Path.StartsWith("/photos/", StringComparison.Ordinal))
        {
            if (request.Method != "GET") return OfflineResponse.Text("Method not allowed", 405);
            var id = request.Path["/photos/".Length..];
            var thumbnail = request.Flag("thumb");
            await using var file = _store.OpenPhoto(id, thumbnail);
            using var output = new MemoryStream();
            await file.CopyToAsync(output, token);
            return new OfflineResponse(200, "image/jpeg", output.ToArray());
        }
        if (request.Path.StartsWith("/api/", StringComparison.Ordinal))
            return await MobileApi.HandleAsync(_store, request, Address!, token);
        if (request.Method != "GET" && request.Method != "HEAD")
            return OfflineResponse.Text("Method not allowed", 405);
        var fileName = request.Path == "/" || !Path.HasExtension(request.Path) ? "index.html" : request.Path.TrimStart('/');
        if (fileName.Split('/').Any(x => x is "." or "..")) return OfflineResponse.Text("Not found", 404);
        try
        {
            await using var asset = await FileSystem.OpenAppPackageFileAsync("wwwroot/" + fileName);
            using var memory = new MemoryStream();
            await asset.CopyToAsync(memory, token);
            return new OfflineResponse(200, Mime(fileName), request.Method == "HEAD" ? [] : memory.ToArray());
        }
        catch (FileNotFoundException) { return OfflineResponse.Text("Not found", 404); }
    }

    private static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp", ".woff2" => "font/woff2", ".wasm" => "application/wasm",
        ".ico" => "image/x-icon", ".pbf" => "application/x-protobuf", _ => "application/octet-stream"
    };

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        if (_acceptTask is not null) await _acceptTask;
        _store.Dispose();
        _shutdown.Dispose();
    }
}
