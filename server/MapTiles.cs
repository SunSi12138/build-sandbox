using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed record TileProvider(string Id, string Folder, string Scheme = "xyz", string UrlTemplate = "", string ApiKey = "", string MapKey = "", string Subdomains = "0,1,2,3,4,5,6,7", string Name = "", bool ForceRefresh = false, bool Enabled = true, int SortOrder = 0, string Format = "raster", int Preload = 0, bool HasStyle = false);
public sealed record MapLayerDto(string Id, string Kind = "raster", int Preload = 0, string? StyleUrl = null);
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
    private static readonly string[] RasterTileExtensions = [".png", ".jpg", ".webp"];
    private static readonly string[] VectorTileExtensions = [".pbf", ".mvt"];
    private const string MvtUrlPrefix = "mvt+";
    private const string PmtilesUrlPrefix = "pmtiles+";
    // Existing url_template stores the format prefix. An optional preload prefix
    // keeps 0-3 per-layer values without adding columns or rewriting old rows.
    private const string PreloadUrlPrefix = "hb-preload-";
    private static (string Url, int Preload) UnwrapStoredMapUrl(string value)
    {
        if (value.StartsWith(PreloadUrlPrefix, StringComparison.Ordinal) &&
            value.Length > PreloadUrlPrefix.Length + 1 &&
            value[PreloadUrlPrefix.Length] is >= '1' and <= '3' &&
            value[PreloadUrlPrefix.Length + 1] == '+')
            return (value[(PreloadUrlPrefix.Length + 2)..], value[PreloadUrlPrefix.Length] - '0');
        return (value, 0);
    }
    private const int ArchiveMaxRange = 16 * 1024 * 1024;
    private static readonly Regex ArchiveRange = new(@"^bytes=(\d+)-(\d+)$", RegexOptions.CultureInvariant);
    private static bool IsPmtiles(TileProvider provider) => provider.Format == "pmtiles";
    private static bool ValidPmtilesHeader(ReadOnlySpan<byte> data) =>
        data.Length >= 127 && data[0] == (byte)'P' && data[1] == (byte)'M' &&
        data[2] == (byte)'T' && data[3] == (byte)'i' && data[4] == (byte)'l' &&
        data[5] == (byte)'e' && data[6] == (byte)'s' && data[7] == 3;
    private static bool IsMvt(TileProvider provider) => provider.Format == "mvt";
    private static string StoredMapUrl(TileProvider provider)
    {
        var value = IsPmtiles(provider)
            ? PmtilesUrlPrefix + provider.UrlTemplate : IsMvt(provider) ? MvtUrlPrefix + provider.UrlTemplate : provider.UrlTemplate;
        return provider.Preload == 0 ? value : PreloadUrlPrefix + provider.Preload.ToString(CultureInfo.InvariantCulture) + "+" + value;
    }
    private static bool IsTms(TileProvider provider) => provider.Scheme == "tms";
    private static string[] TileExtensions(TileProvider provider) => IsMvt(provider) ? VectorTileExtensions : RasterTileExtensions;
    private static TileProvider[] DefaultMapProviders() =>
    [
        new("jlyh", "jlyh", "tms", "https://api.jl1mall.com/getMap/{z}/{x}/{y}?mk={mk}&tk={key}", Name: "吉林一号"),
        new("tdt-cva", "tdt-cva", "xyz", "https://t{s}.tianditu.gov.cn/cva_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER=cva&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk={key}", Name: "天地图地名标注")
    ];
    private const string MapProviderColumns = "id,folder,scheme,url_template,api_key,map_key,subdomains,name,force_refresh,enabled,sort_order";
    private TileProvider ReadMapProvider(SqliteDataReader row)
    {
        var (storedUrl, preload) = UnwrapStoredMapUrl(row.GetString(3));
        var mvt = storedUrl.StartsWith(MvtUrlPrefix, StringComparison.Ordinal);
        var archive = storedUrl.StartsWith(PmtilesUrlPrefix, StringComparison.Ordinal);
        return new(row.GetString(0), row.GetString(1), row.GetString(2),
            archive ? storedUrl[PmtilesUrlPrefix.Length..] : mvt ? storedUrl[MvtUrlPrefix.Length..] : storedUrl,
            row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7),
            row.GetInt32(8) != 0, row.GetInt32(9) != 0, row.GetInt32(10), archive ? "pmtiles" : mvt ? "mvt" : "raster", preload, File.Exists(Path.Combine(MapStyleDirectory(row.GetString(0)), "style.json")));
    }
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
        if (provider.Scheme is not ("xyz" or "tms")) throw new ApiException(400, "图块坐标应为 XYZ 或 TMS。");
        if (provider.Preload is < 0 or > 3) throw new ApiException(400, "图层加载深度必须在 0 到 3 之间。");
        if (provider.Format is not ("raster" or "mvt" or "pmtiles")) throw new ApiException(400, "图层类型应为图片、MVT 或 PMTiles。");
        if (IsPmtiles(provider) && (!provider.Folder.EndsWith(".pmtiles", StringComparison.OrdinalIgnoreCase) || provider.Scheme != "xyz"))
            throw new ApiException(400, "PMTiles 请填写 .pmtiles 文件路径并使用 XYZ 坐标。");
        if (IsPmtiles(provider) && provider.ForceRefresh)
            throw new ApiException(400, "PMTiles 按需分段读取，不支持强制更新缓存。");
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
            if (IsPmtiles(provider))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null) throw new ApiException(400, "PMTiles 文件路径不正确。");
                Directory.CreateDirectory(parent);
            }
            else Directory.CreateDirectory(directory);
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
                """, null, ("$id", provider.Id), ("$name", provider.Name), ("$folder", provider.Folder), ("$scheme", provider.Scheme), ("$url", StoredMapUrl(provider)),
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
                ("$url", StoredMapUrl(provider)), ("$key", provider.ApiKey), ("$map", provider.MapKey), ("$subdomains", provider.Subdomains), ("$force", provider.ForceRefresh ? 1 : 0));
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
            using var command = Command("SELECT id,url_template FROM map_providers WHERE enabled=1 ORDER BY sort_order DESC,rowid DESC;");
            using var rows = command.ExecuteReader(); var layers = new List<MapLayerDto>();
            while (rows.Read())
            {
                var (url, preload) = UnwrapStoredMapUrl(rows.GetString(1));
                layers.Add(new(rows.GetString(0),
                    url.StartsWith(PmtilesUrlPrefix, StringComparison.Ordinal) ? "pmtiles" :
                    url.StartsWith(MvtUrlPrefix, StringComparison.Ordinal) ? "mvt" : "raster", preload,
                    File.Exists(Path.Combine(MapStyleDirectory(rows.GetString(0)), "style.json"))
                        ? $"/api/map/style/{Uri.EscapeDataString(rows.GetString(0))}/style.json" : null));
            }
            return layers.ToArray();
        }
    }
    // Availability checks are explicit, read-only diagnostics. In offline mode
    // they only inspect the configured cache directory, never attempt network IO.
    private static (int Z, int X, int Row)? SampleCachedMapTile(string directory, string[] extensions)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
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
        if (IsPmtiles(provider)) return await CheckPmtilesAsync(provider, cancellationToken);
        (int Z, int X, int Row)? cached;
        try { cached = SampleCachedMapTile(MapCacheDirectory(provider.Folder), TileExtensions(provider)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("error", "无法读取该图层的缓存目录，请检查目录和访问权限。", false, hasUrl);
        }
        var hasCache = cached.HasValue;
        if (!hasUrl)
            return hasCache
                ? new("cached", "检测到本地图块，可在已缓存区域离线使用；未检查全部区域。", true, false)
                : new("empty", "未配置调用链接，也未发现本地图块。空白底图不影响点位和区域操作。", false, false);

        // Test one representative image or MVT tile using the same URL formatter as
        // actual map requests. A successful sample does not guarantee coverage.
        const int defaultZoom = 12;
        var zoom = cached?.Z ?? defaultZoom;
        var xTile = cached?.X ?? (int)Math.Floor((88.893426 + 180) / 360 * (1 << defaultZoom));
        var latitude = 29.274331 * Math.PI / 180;
        var xyzY = (int)Math.Floor((1 - Math.Log(Math.Tan(latitude) + 1 / Math.Cos(latitude)) / Math.PI) / 2 * (1 << defaultZoom));
        var row = cached?.Row ?? (IsTms(provider) ? (1 << defaultZoom) - 1 - xyzY : xyzY);
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
                var valid = IsMvt(provider) ? length > 2 && (header[0] == 0x1a || header[0] == 0x1f && header[1] == 0x8b) : length >= 8 && header[0] == 137 && header[1] == 80 && header[2] == 78 && header[3] == 71
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

    private async Task<MapLayerCheckDto> CheckPmtilesAsync(TileProvider provider, CancellationToken token)
    {
        var local = MapCacheDirectory(provider.Folder);
        var hasLocal = false;
        var archiveDescription = "";
        try
        {
            if (File.Exists(local))
            {
                await using var input = File.OpenRead(local);
                var header = new byte[127];
                hasLocal = await input.ReadAsync(header, token) == 127 && ValidPmtilesHeader(header);
                if (!hasLocal) return new("unavailable", "本地文件不是有效的 PMTiles v3 归档。", false, provider.UrlTemplate.Length > 0);
                var type = header[99] switch
                {
                    1 => "MVT/PBF 矢量",
                    2 => "PNG 图片",
                    3 => "JPG 图片",
                    4 => "WebP 图片",
                    _ => "未知图块类型"
                };
                archiveDescription = $"图块类型：{type}，缩放级别：{header[100]}–{header[101]}。";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("error", "无法读取 PMTiles 文件，请检查路径与权限。", false, provider.UrlTemplate.Length > 0); }
        if (hasLocal) return new("cached", $"本地 PMTiles v3 归档可读取。{archiveDescription}未逐一检查其中的图块和地名字段。", true, provider.UrlTemplate.Length > 0);
        if (provider.UrlTemplate.Length == 0) return new("empty", "尚未找到本地 PMTiles 文件。可将归档复制到设置的路径。", false, false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, MapTileUrl(provider, 0, 0, 0));
            request.Headers.Range = new RangeHeaderValue(0, 126);
            using var response = await MapHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange?.From == 0)
            {
                var header = new byte[127];
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                var read = 0;
                while (read < header.Length)
                {
                    var count = await input.ReadAsync(header.AsMemory(read), timeout.Token);
                    if (count == 0) break;
                    read += count;
                }
                if (read == 127 && ValidPmtilesHeader(header))
                    return new("online", "在线 PMTiles v3 归档支持分段读取；未检查全部图块。", false, true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) { /* Only report a sanitized result. */ }
        return new("unavailable", "在线 PMTiles 无法分段读取，请确认地址、权限及 HTTP Range 支持。", false, true);
    }

    private async Task ServePmtilesArchiveAsync(HttpContext context)
    {
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(Api.Id(context));
        if (!provider.Enabled || !IsPmtiles(provider)) { context.Response.StatusCode = 404; return; }
        var match = ArchiveRange.Match(context.Request.Headers.Range.ToString());
        if (!match.Success ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            end < start || end - start >= ArchiveMaxRange)
        { context.Response.StatusCode = 416; return; }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.AcceptRanges = "bytes";
        var local = MapCacheDirectory(provider.Folder);
        if (File.Exists(local))
        {
            await Results.File(local, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
            return;
        }
        if (provider.UrlTemplate.Length == 0) { context.Response.StatusCode = 404; return; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, MapTileUrl(provider, 0, 0, 0));
            request.Headers.Range = new RangeHeaderValue(start, end);
            using var response = await MapHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var remoteRange = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || remoteRange?.From != start ||
                remoteRange.To > end || response.Content.Headers.ContentLength is > ArchiveMaxRange)
            { context.Response.StatusCode = 502; return; }
            context.Response.StatusCode = 206;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentRange = remoteRange.ToString();
            if (response.Content.Headers.ContentLength is long size)
                context.Response.ContentLength = size;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await input.CopyToAsync(context.Response.Body, timeout.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 502; }
    }

    private static string MapTileUrl(TileProvider provider, int z, int x, int row)
    {
        var hosts = provider.Subdomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return provider.UrlTemplate.Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{y}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s}", hosts.Length > 0 ? hosts[(x + row) % hosts.Length] : "0", StringComparison.Ordinal)
            .Replace("{key}", Uri.EscapeDataString(provider.ApiKey), StringComparison.Ordinal).Replace("{mk}", Uri.EscapeDataString(provider.MapKey), StringComparison.Ordinal);
    }
    private static string? CachedMapTile(string path, TileProvider provider) => TileExtensions(provider).Select(extension => path + extension).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    private async Task<string?> GetMapTileAsync(string layer, int z, int x, int y, CancellationToken cancellationToken)
    {
        if (z < 0 || z > 18 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return null;
        TileProvider provider; int generation;
        lock (_gate) { provider = GetMapProvider(layer); generation = _mapGeneration; }
        if (!provider.Enabled || IsPmtiles(provider)) return null;
        var row = IsTms(provider) ? (1 << z) - 1 - y : y;
        var path = Path.Combine(MapCacheDirectory(provider.Folder), z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), row.ToString(CultureInfo.InvariantCulture));
        var cached = CachedMapTile(path, provider);
        if (provider.UrlTemplate.Length == 0 || cached is not null && !provider.ForceRefresh) return cached;
        await _mapDownloads.WaitAsync(cancellationToken);
        try
        {
            cached = CachedMapTile(path, provider);
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
            // HTTP Content-Encoding is normally decompressed by MapHttp. Also
            // accept providers returning gzip bytes without that header.
            if (IsMvt(provider) && data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b)
            {
                using var compressed = new MemoryStream(data);
                using var unzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var uncompressed = new MemoryStream();
                var buffer = new byte[81920]; int read;
                while ((read = unzip.Read(buffer)) > 0)
                {
                    if (uncompressed.Length + read > 10 * 1024 * 1024) throw new IOException("矢量图块解压后过大。");
                    uncompressed.Write(buffer, 0, read);
                }
                data = uncompressed.ToArray();
            }
            var extension = IsMvt(provider)
                ? data.Length > 2 && data[0] == 0x1a ? ".pbf" : throw new IOException("地图服务未返回有效 MVT 图块。")
                : data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ? ".png"
                : data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? ".jpg"
                : data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? ".webp" : throw new IOException("地图服务未返回图片。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + NewId() + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, data, cancellationToken);
                lock (_gate)
                {
                    if (generation != _mapGeneration) return CachedMapTile(path, provider);
                    File.Move(temp, path + extension, true);
                    foreach (var old in TileExtensions(provider).Where(item => item != extension)) TryDeleteFile(path + old);
                }
            }
            finally { TryDeleteFile(temp); }
            return path + extension;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { return CachedMapTile(path, provider); }
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
        app.MapGet("/api/map/archive/{id}", ServePmtilesArchiveAsync);
        app.MapPut("/api/map/style/{id}", async context =>
        {
            var provider = await ImportMapStyleAsync(Api.Id(context), context.Request.Body, context.RequestAborted);
            await Api.Write(context, provider, MapConfigJsonContext.Default.TileProvider);
        });
        app.MapDelete("/api/map/style/{id}", context =>
        {
            Api.RequireDeleteConfirmation(context);
            DeleteMapStyle(Api.Id(context));
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        app.MapGet("/api/map/style/{id}/{**asset}", ServeMapStyleAssetAsync);
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
            context.Response.ContentType = Path.GetExtension(file) switch { ".jpg" => "image/jpeg", ".webp" => "image/webp", ".pbf" or ".mvt" => "application/vnd.mapbox-vector-tile", _ => "image/png" };
            // Locally imported PBFs are sometimes gzip-compressed. Let the browser transparently decode them.
            if (file.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mvt", StringComparison.OrdinalIgnoreCase))
            {
                var header = new byte[2];
                using var input = File.OpenRead(file);
                if (input.Read(header) == 2 && header[0] == 0x1f && header[1] == 0x8b)
                    context.Response.Headers["Content-Encoding"] = "gzip";
            }
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(file, context.RequestAborted);
        };
        app.MapGet("/api/map/tiles/{layer}/{z:int}/{x:int}/{y:int}", tile);
    }
}
