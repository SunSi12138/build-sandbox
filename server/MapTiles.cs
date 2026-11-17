using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed record TileProvider(string Id, string Folder, string Scheme = "xyz", string UrlTemplate = "", string ApiKey = "", string MapKey = "", string Subdomains = "0,1,2,3,4,5,6,7", string Name = "", bool ForceRefresh = false, bool Enabled = true, int SortOrder = 0);
public sealed record MapLayerDto(string Id);
public sealed record MapOrderRequest(string[] Ids);
public sealed record MapEnabledRequest(bool? Enabled);
public sealed record MapSettingsDto(TileProvider[] Providers, TileProvider[] Defaults, string CacheRoot);
public sealed record MapLayerCheckDto(string Status, string Message, bool HasCache, bool HasUrl);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TileProvider))]
[JsonSerializable(typeof(TileProvider[]))]
[JsonSerializable(typeof(MapSettingsDto))]
[JsonSerializable(typeof(MapLayerDto[]))]
[JsonSerializable(typeof(MapOrderRequest))]
[JsonSerializable(typeof(MapEnabledRequest))]
[JsonSerializable(typeof(MapLayerCheckDto))]
public partial class MapConfigJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private static readonly HttpClient MapHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _mapDownloads = new(6);
    private int _mapGeneration;
    private static readonly string[] TileExtensions = [".png", ".jpg", ".webp"];
    private static TileProvider[] DefaultMapProviders() =>
    [
        new("jlyh", "jlyh", "tms", "https://api.jl1mall.com/getMap/{z}/{x}/{y}?mk={mk}&tk={key}", Name: "吉林一号"),
        new("tdt-cva", "tdt-cva", "xyz", "https://t{s}.tianditu.gov.cn/cva_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=cva&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk={key}", Name: "天地图地名标注")
    ];
    private const string MapProviderColumns = "id,folder,scheme,url_template,api_key,map_key,subdomains,name,force_refresh,enabled,sort_order";
    private static TileProvider ReadMapProvider(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7), row.GetInt32(8) != 0, row.GetInt32(9) != 0, row.GetInt32(10));
    private TileProvider GetMapProvider(string id)
    {
        using var command = Command("SELECT " + MapProviderColumns + " FROM map_providers WHERE id=$id;", null, ("$id", id));
        using var row = command.ExecuteReader();
        return row.Read() ? ReadMapProvider(row) : throw new ApiException(404, "地图图层不存在。");
    }
    public MapSettingsDto GetMapSettings()
    {
        lock (_gate)
        {
            using var command = Command("SELECT " + MapProviderColumns + " FROM map_providers ORDER BY sort_order,rowid;");
            using var rows = command.ExecuteReader(); var providers = new List<TileProvider>();
            while (rows.Read()) providers.Add(ReadMapProvider(rows));
            return new(providers.ToArray(), DefaultMapProviders(), Path.Combine(RootDir, "tiles"));
        }
    }
    private string MapCacheDirectory(string folder) => Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(RootDir, "tiles", folder));
    private TileProvider ValidateMapProvider(TileProvider request)
    {
        var provider = request with
        {
            Name = Text(request.Name, "地图名称", 80, true), Folder = Text(request.Folder, "缓存目录", 1000, true),
            UrlTemplate = Text(request.UrlTemplate, "调用链接", 4000), ApiKey = Text(request.ApiKey, "服务密钥", 2000),
            MapKey = Text(request.MapKey, "地图密钥", 2000), Subdomains = Text(request.Subdomains, "子域名", 200)
        };
        if (provider.Scheme is not ("xyz" or "tms")) throw new ApiException(400, "图块坐标格式应为 XYZ 或 TMS。");
        if (provider.ForceRefresh && provider.UrlTemplate.Length == 0) throw new ApiException(400, "强制更新缓存需要填写调用链接。");
        if (provider.UrlTemplate.Length > 0)
        {
            if (provider.UrlTemplate.Contains("{key}", StringComparison.Ordinal) && provider.ApiKey.Length == 0) throw new ApiException(400, "调用链接包含 {key}，请填写服务密钥。");
            if (provider.UrlTemplate.Contains("{mk}", StringComparison.Ordinal) && provider.MapKey.Length == 0) throw new ApiException(400, "调用链接包含 {mk}，请填写地图密钥。");
            var sample = MapTileUrl(provider, 1, 0, 0);
            if (sample.Contains('{') || sample.Contains('}') || !Uri.TryCreate(sample, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ApiException(400, "请填写 HTTP/HTTPS 调用链接，支持 {z}、{x}、{y}、{s}、{key} 和 {mk}。");
        }
        try
        {
            var directory = MapCacheDirectory(provider.Folder);
            Directory.CreateDirectory(directory);
            if (provider.UrlTemplate.Length > 0)
            {
                var probe = Path.Combine(directory, ".hbtool-map-" + NewId() + ".tmp");
                try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                finally { TryDeleteFile(probe); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ApiException(400, "缓存目录不可用，请检查路径和读写权限。"); }
        return provider;
    }
    public TileProvider CreateMapProvider(TileProvider request)
    {
        var provider = ValidateMapProvider(request with { Id = NewId() });
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("""
                INSERT INTO map_providers(id,name,folder,scheme,url_template,api_key,map_key,subdomains,force_refresh,enabled,sort_order)
                VALUES($id,$name,$folder,$scheme,$url,$key,$map,$subdomains,$force,$enabled,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM map_providers));
                """, null, ("$id", provider.Id), ("$name", provider.Name), ("$folder", provider.Folder), ("$scheme", provider.Scheme), ("$url", provider.UrlTemplate),
                ("$key", provider.ApiKey), ("$map", provider.MapKey), ("$subdomains", provider.Subdomains), ("$force", provider.ForceRefresh ? 1 : 0), ("$enabled", provider.Enabled ? 1 : 0));
            command.ExecuteNonQuery(); _mapGeneration++; return GetMapProvider(provider.Id);
        }
    }
    public TileProvider SaveMapProvider(string id, TileProvider request)
    {
        if (request.Id != id) throw new ApiException(400, "地图图层无效。");
        var provider = ValidateMapProvider(request);
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("""
                UPDATE map_providers SET name=$name,folder=$folder,scheme=$scheme,url_template=$url,api_key=$key,map_key=$map,subdomains=$subdomains,force_refresh=$force WHERE id=$id;
                """, null, ("$id", provider.Id), ("$name", provider.Name), ("$folder", provider.Folder), ("$scheme", provider.Scheme),
                ("$url", provider.UrlTemplate), ("$key", provider.ApiKey), ("$map", provider.MapKey), ("$subdomains", provider.Subdomains), ("$force", provider.ForceRefresh ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "地图图层"); _mapGeneration++; return GetMapProvider(id);
        }
    }
    public TileProvider SetMapProviderEnabled(string id, MapEnabledRequest request)
    {
        if (request.Enabled is null) throw new ApiException(400, "请选择图层启用状态。");
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("UPDATE map_providers SET enabled=$enabled WHERE id=$id;", null, ("$id", id), ("$enabled", request.Enabled.Value ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "地图图层"); _mapGeneration++; return GetMapProvider(id);
        }
    }
    public void ReorderMapProviders(MapOrderRequest request)
    {
        lock (_gate)
        {
            PrepareWrite(); var current = GetMapSettings().Providers.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (request.Ids is null || request.Ids.Length != current.Count || request.Ids.Distinct(StringComparer.Ordinal).Count() != current.Count || !current.SetEquals(request.Ids))
                throw new ApiException(409, "图层列表已变化，请刷新后重试。");
            using var transaction = _db.BeginTransaction();
            for (var i = 0; i < request.Ids.Length; i++)
            {
                using var command = Command("UPDATE map_providers SET sort_order=$order WHERE id=$id;", transaction, ("$id", request.Ids[i]), ("$order", i)); command.ExecuteNonQuery();
            }
            transaction.Commit(); _mapGeneration++;
        }
    }
    public void DeleteMapProvider(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("DELETE FROM map_providers WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "地图图层"); _mapGeneration++;
        }
    }
    public MapLayerDto[] GetMapLayers()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id FROM map_providers WHERE enabled=1 ORDER BY sort_order DESC,rowid DESC;");
            using var rows = command.ExecuteReader(); var layers = new List<MapLayerDto>();
            while (rows.Read()) layers.Add(new(rows.GetString(0)));
            return layers.ToArray();
        }
    }
    // Availability checks are explicit, read-only diagnostics. In offline mode
    // they only inspect the configured cache directory, never attempt network IO.
    private static (int Z, int X, int Row)? SampleCachedMapTile(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!TileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var path = Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (path.Length != 3 ||
                !int.TryParse(path[0], NumberStyles.None, CultureInfo.InvariantCulture, out var z) ||
                !int.TryParse(path[1], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ||
                !int.TryParse(Path.GetFileNameWithoutExtension(path[2]), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
                z is < 0 or > 18 || x < 0 || row < 0 || x >= 1 << z || row >= 1 << z)
                continue;
            return (z, x, row);
        }
        return null;
    }

    public async Task<MapLayerCheckDto> CheckMapLayerAsync(string id, CancellationToken cancellationToken)
    {
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(id);
        var hasUrl = !string.IsNullOrWhiteSpace(provider.UrlTemplate);
        (int Z, int X, int Row)? cached;
        try { cached = SampleCachedMapTile(MapCacheDirectory(provider.Folder)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("error", "无法读取该图层的缓存目录，请检查目录和访问权限。", false, hasUrl);
        }
        var hasCache = cached.HasValue;
        if (!hasUrl)
            return hasCache
                ? new("cached", "检测到本地图块，可在已缓存区域离线使用；未检查全部区域。", true, false)
                : new("empty", "未配置调用链接，也未发现本地图块。空白底图不影响点位和区域操作。", false, false);

        // Test one representative image tile using the same URL formatter as
        // actual map requests. A successful sample does not guarantee coverage.
        const int defaultZoom = 12;
        var zoom = cached?.Z ?? defaultZoom;
        var xTile = cached?.X ?? (int)Math.Floor((88.893426 + 180) / 360 * (1 << defaultZoom));
        var latitude = 29.274331 * Math.PI / 180;
        var xyzY = (int)Math.Floor((1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2 * (1 << defaultZoom));
        var row = cached?.Row ?? (provider.Scheme == "tms" ? (1 << defaultZoom) - 1 - xyzY : xyzY);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = new HttpRequestMessage(HttpMethod.Get, MapTileUrl(provider, zoom, xTile, row));
            using var response = await MapHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                var header = new byte[12];
                var length = 0;
                while (length < header.Length)
                {
                    var read = await input.ReadAsync(header.AsMemory(length), timeout.Token);
                    if (read == 0) break;
                    length += read;
                }
                var valid = length >= 8 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71
                    || length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255
                    || length >= 12 && header[0] == 82 && header[1] == 73 && header[2] == 70 && header[3] == 70
                        && header[8] == 87 && header[9] == 69 && header[10] == 66 && header[11] == 80;
                if (valid) return new("online", "调用链接的样例图块请求成功；其他区域和缩放级别未逐一检查。", hasCache, true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { /* Report below without exposing keys or the requested URL. */ }
        return hasCache
            ? new("cached", "调用链接未通过样例请求，但本地图块仍可在已缓存区域使用。", true, true)
            : new("unavailable", "调用链接样例图块不可访问，且未发现本地缓存；请检查网络、链接和密钥。", false, true);
    }

    private static string MapTileUrl(TileProvider provider, int z, int x, int row)
    {
        var hosts = provider.Subdomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return provider.UrlTemplate.Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{y}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s}", hosts.Length > 0 ? hosts[(x + row) % hosts.Length] : "0", StringComparison.Ordinal)
            .Replace("{key}", Uri.EscapeDataString(provider.ApiKey), StringComparison.Ordinal).Replace("{mk}", Uri.EscapeDataString(provider.MapKey), StringComparison.Ordinal);
    }
    private static string? CachedMapTile(string path) => TileExtensions.Select(extension => path + extension).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    private async Task<string?> GetMapTileAsync(string layer, int z, int x, int y, CancellationToken cancellationToken)
    {
        if (z < 0 || z > 18 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return null;
        TileProvider provider; int generation;
        lock (_gate) { provider = GetMapProvider(layer); generation = _mapGeneration; }
        if (!provider.Enabled) return null;
        var row = provider.Scheme == "tms" ? (1 << z) - 1 - y : y;
        var path = Path.Combine(MapCacheDirectory(provider.Folder), z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), row.ToString(CultureInfo.InvariantCulture));
        var cached = CachedMapTile(path);
        if (provider.UrlTemplate.Length == 0 || cached is not null && !provider.ForceRefresh) return cached;
        await _mapDownloads.WaitAsync(cancellationToken);
        try
        {
            cached = CachedMapTile(path);
            if (cached is not null && !provider.ForceRefresh) return cached;
            using var request = new HttpRequestMessage(HttpMethod.Get, MapTileUrl(provider, z, x, row));
            if (provider.ForceRefresh) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await MapHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new IOException("地图图片过大。");
            using var bytes = new MemoryStream();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var buffer = new byte[81920]; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (bytes.Length + count > 10 * 1024 * 1024) throw new IOException("地图图片过大。");
                    bytes.Write(buffer, 0, count);
                }
            }
            var data = bytes.ToArray();
            var extension = data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ? ".png"
                : data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? ".jpg"
                : data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? ".webp" : throw new IOException("地图服务未返回图片。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + NewId() + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, data, cancellationToken);
                lock (_gate)
                {
                    if (generation != _mapGeneration) return CachedMapTile(path);
                    File.Move(temp, path + extension, true);
                    foreach (var old in TileExtensions.Where(item => item != extension)) TryDeleteFile(path + old);
                }
            }
            finally { TryDeleteFile(temp); }
            return path + extension;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { return CachedMapTile(path); }
        finally { _mapDownloads.Release(); }
    }
    public void MapApi(WebApplication app)
    {
        RequestDelegate settings = context => Api.Write(context, GetMapSettings(), MapConfigJsonContext.Default.MapSettingsDto);
        RequestDelegate check = async context => await Api.Write(context,
            await CheckMapLayerAsync(Api.Id(context), context.RequestAborted), MapConfigJsonContext.Default.MapLayerCheckDto);
        RequestDelegate save = async context => await Api.Write(context, SaveMapProvider(Api.Id(context), await Api.Read(context, MapConfigJsonContext.Default.TileProvider)), MapConfigJsonContext.Default.TileProvider);
        RequestDelegate create = async context => await Api.Write(context, CreateMapProvider(await Api.Read(context, MapConfigJsonContext.Default.TileProvider)), MapConfigJsonContext.Default.TileProvider, 201);
        RequestDelegate enabled = async context => await Api.Write(context, SetMapProviderEnabled(Api.Id(context), await Api.Read(context, MapConfigJsonContext.Default.MapEnabledRequest)), MapConfigJsonContext.Default.TileProvider);
        RequestDelegate order = async context => { ReorderMapProviders(await Api.Read(context, MapConfigJsonContext.Default.MapOrderRequest)); await Api.Write(context, GetMapSettings(), MapConfigJsonContext.Default.MapSettingsDto); };
        RequestDelegate delete = context => { Api.RequireDeleteConfirmation(context); DeleteMapProvider(Api.Id(context)); context.Response.StatusCode = 204; return Task.CompletedTask; };
        RequestDelegate layers = context => Api.Write(context, GetMapLayers(), MapConfigJsonContext.Default.MapLayerDtoArray);
        app.MapGet("/api/map/layers", layers);
        app.MapPost("/api/map/settings", create);
        app.MapPut("/api/map/settings/order", order);
        app.MapPut("/api/map/settings/{id}/enabled", enabled);
        app.MapDelete("/api/map/settings/{id}", delete);
        app.MapGet("/api/map/settings", settings);
        app.MapGet("/api/map/settings/{id}/check", check);
        app.MapPut("/api/map/settings/{id}", save);
        RequestDelegate tile = async context =>
        {
            var file = await GetMapTileAsync((string)context.Request.RouteValues["layer"]!, int.Parse((string)context.Request.RouteValues["z"]!, CultureInfo.InvariantCulture), int.Parse((string)context.Request.RouteValues["x"]!, CultureInfo.InvariantCulture), int.Parse((string)context.Request.RouteValues["y"]!, CultureInfo.InvariantCulture), context.RequestAborted);
            if (file is null) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = Path.GetExtension(file) switch { ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(file, context.RequestAborted);
        };
        app.MapGet("/api/map/tiles/{layer}/{z:int}/{x:int}/{y:int}", tile);
    }
}
