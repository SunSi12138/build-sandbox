using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

// Preload is a legacy API/storage name kept for existing databases.
// It now represents high-resolution depth; OpenLayers preload is fixed at 1.
public sealed record TileProvider(string Id, string Folder, string Scheme = "xyz", string UrlTemplate = "", string ApiKey = "", string MapKey = "", string Subdomains = "0,1,2,3,4,5,6,7", string Name = "", bool ForceRefresh = false, bool Enabled = true, int SortOrder = 0, string Format = "raster", int Preload = 0, string StyleDirectory = "", bool HasStyle = false);
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
    private static readonly string[] RasterTileExtensions = [".png", ".jpg", ".webp"];
    private static readonly string[] VectorTileExtensions = [".pbf", ".mvt"];
    private const string MvtUrlPrefix = "mvt+";
    private const string PmtilesUrlPrefix = "pmtiles+";
    // Existing url_template stores the format prefix. An optional preload prefix
    // keeps 0-3 per-layer values without adding columns or rewriting old rows.
    private const string PreloadUrlPrefix = "hb-preload-";
    private const string StyleUrlPrefix = "hb-style-";
    private static (string Url, int Preload, string StyleDirectory) UnwrapStoredMapUrl(string value)
    {
        var preload = 0;
        if (value.StartsWith(PreloadUrlPrefix, StringComparison.Ordinal) &&
            value.Length > PreloadUrlPrefix.Length + 1 &&
            value[PreloadUrlPrefix.Length] is >= '1' and <= '3' &&
            value[PreloadUrlPrefix.Length + 1] == '+')
        {
            preload = value[PreloadUrlPrefix.Length] - '0';
            value = value[(PreloadUrlPrefix.Length + 2)..];
        }
        var styleDirectory = "";
        if (value.StartsWith(StyleUrlPrefix, StringComparison.Ordinal))
        {
            var separator = value.IndexOf('+', StyleUrlPrefix.Length);
            if (separator > StyleUrlPrefix.Length)
            {
                var token = value[StyleUrlPrefix.Length..separator].Replace('-', '+').Replace('_', '/');
                try
                {
                    styleDirectory = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token.PadRight((token.Length + 3) / 4 * 4, '=')));
                    value = value[(separator + 1)..];
                }
                catch (FormatException) { /* Ignore an invalid legacy prefix. */ }
            }
        }
        return (value, preload, styleDirectory);
    }
    private const int ArchiveMaxRange = 16 * 1024 * 1024;
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
        if (provider.StyleDirectory.Length > 0)
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(provider.StyleDirectory)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            value = StyleUrlPrefix + token + "+" + value;
        }
        return provider.Preload == 0 ? value : PreloadUrlPrefix + provider.Preload.ToString(CultureInfo.InvariantCulture) + "+" + value;
    }
    private static bool IsTms(TileProvider provider) => provider.Scheme == "tms";
    private static string[] TileExtensions(TileProvider provider) => IsMvt(provider) ? VectorTileExtensions : RasterTileExtensions;
    // Keep a single default local layer when initializing a fresh database.
    // Existing databases keep their original providers and stored online fields.
    private static TileProvider[] DefaultMapProviders() =>
    [
        new("local-imagery", "local-imagery", Name: "本地影像")
    ];
    private const string MapProviderColumns = "id,folder,scheme,url_template,api_key,map_key,subdomains,name,force_refresh,enabled,sort_order";
    private TileProvider ReadMapProvider(SqliteDataReader row)
    {
        var (storedUrl, preload, styleDirectory) = UnwrapStoredMapUrl(row.GetString(3));
        var mvt = storedUrl.StartsWith(MvtUrlPrefix, StringComparison.Ordinal);
        var archive = storedUrl.StartsWith(PmtilesUrlPrefix, StringComparison.Ordinal);
        return new(row.GetString(0), row.GetString(1), row.GetString(2),
            archive ? storedUrl[PmtilesUrlPrefix.Length..] : mvt ? storedUrl[MvtUrlPrefix.Length..] : storedUrl,
            row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7),
            row.GetInt32(8) != 0, row.GetInt32(9) != 0, row.GetInt32(10), archive ? "pmtiles" : mvt ? "mvt" : "raster", preload, styleDirectory,
            styleDirectory.Length > 0 && File.Exists(Path.Combine(MapCacheDirectory(styleDirectory), "style.json")));
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
            MapKey = Text(request.MapKey, "地图密钥", 2000), Subdomains = Text(request.Subdomains, "子域名", 200),
            StyleDirectory = Text(request.StyleDirectory, "矢量样式目录", 1000)
        };
        if (provider.Scheme is not ("xyz" or "tms")) throw new ApiException(400, "图块坐标应为 XYZ 或 TMS。");
        if (provider.Preload is < 0 or > 3) throw new ApiException(400, "图层加载深度必须在 0 到 3 之间。");
        if (provider.Format is not ("raster" or "mvt" or "pmtiles")) throw new ApiException(400, "图层类型应为图片、MVT 或 PMTiles。");
        if (provider.Format == "raster") provider = provider with { StyleDirectory = "" };
        if (IsPmtiles(provider) && (!provider.Folder.EndsWith(".pmtiles", StringComparison.OrdinalIgnoreCase) || provider.Scheme != "xyz"))
            throw new ApiException(400, "PMTiles 请填写 .pmtiles 文件路径并使用 XYZ 坐标。");
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
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ApiException(400, "缓存目录不可用，请检查路径和读写权限。"); }
        return provider;
    }
    public TileProvider CreateMapProvider(TileProvider request)
    {
        // Public create calls cannot persist or activate online tile providers.
        // Keep the legacy SQL columns untouched for schema compatibility.
        var provider = ValidateMapProvider(request with
        {
            Id = NewId(), UrlTemplate = "", ApiKey = "", MapKey = "",
            Subdomains = "0,1,2,3,4,5,6,7", ForceRefresh = false
        });
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("""
                INSERT INTO map_providers(id,name,folder,scheme,url_template,api_key,map_key,subdomains,force_refresh,enabled,sort_order)
                VALUES($id,$name,$folder,$scheme,$url,$key,$map,$subdomains,$force,$enabled,(SELECT COALESCE(MAX(sort_order),-1)+1 FROM map_providers));
                """, null, ("$id", provider.Id), ("$name", provider.Name), ("$folder", provider.Folder), ("$scheme", provider.Scheme), ("$url", StoredMapUrl(provider)),
                ("$key", provider.ApiKey), ("$map", provider.MapKey), ("$subdomains", provider.Subdomains), ("$force", provider.ForceRefresh ? 1 : 0), ("$enabled", provider.Enabled ? 1 : 0));
            command.ExecuteNonQuery(); return GetMapProvider(provider.Id);
        }
    }
    public TileProvider SaveMapProvider(string id, TileProvider request)
    {
        if (request.Id != id) throw new ApiException(400, "地图图层无效。");
        lock (_gate)
        {
            // A saved legacy row may contain an online URL and keys. Preserve
            // those bytes when editing local settings, but never use them.
            var legacy = GetMapProvider(id);
            var provider = ValidateMapProvider(request with
            {
                UrlTemplate = legacy.UrlTemplate, ApiKey = legacy.ApiKey,
                MapKey = legacy.MapKey, Subdomains = legacy.Subdomains,
                ForceRefresh = legacy.ForceRefresh
            });
            PrepareWrite();
            using var command = Command("""
                UPDATE map_providers SET name=$name,folder=$folder,scheme=$scheme,url_template=$url,api_key=$key,map_key=$map,subdomains=$subdomains,force_refresh=$force WHERE id=$id;
                """, null, ("$id", provider.Id), ("$name", provider.Name), ("$folder", provider.Folder), ("$scheme", provider.Scheme),
                ("$url", StoredMapUrl(provider)), ("$key", provider.ApiKey), ("$map", provider.MapKey), ("$subdomains", provider.Subdomains), ("$force", provider.ForceRefresh ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "地图图层"); return GetMapProvider(id);
        }
    }
    public TileProvider SetMapProviderEnabled(string id, MapEnabledRequest request)
    {
        if (request.Enabled is null) throw new ApiException(400, "请选择图层启用状态。");
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("UPDATE map_providers SET enabled=$enabled WHERE id=$id;", null, ("$id", id), ("$enabled", request.Enabled.Value ? 1 : 0));
            RequireChanged(command.ExecuteNonQuery(), "地图图层"); return GetMapProvider(id);
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
            transaction.Commit();
        }
    }
    public void DeleteMapProvider(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("DELETE FROM map_providers WHERE id=$id;", null, ("$id", id));
            RequireChanged(command.ExecuteNonQuery(), "地图图层");
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
                var (url, preload, styleDirectory) = UnwrapStoredMapUrl(rows.GetString(1));
                layers.Add(new(rows.GetString(0),
                    url.StartsWith(PmtilesUrlPrefix, StringComparison.Ordinal) ? "pmtiles" :
                    url.StartsWith(MvtUrlPrefix, StringComparison.Ordinal) ? "mvt" : "raster", preload,
                    styleDirectory.Length > 0 && File.Exists(Path.Combine(MapCacheDirectory(styleDirectory), "style.json"))
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
        if (IsPmtiles(provider)) return await CheckPmtilesAsync(provider, cancellationToken);
        // Inspect local files only; legacy online URL and keys are ignored.
        try
        {
            var cached = SampleCachedMapTile(MapCacheDirectory(provider.Folder), TileExtensions(provider));
            return cached.HasValue
                ? new("cached", "检测到本地图块，可在已有瓦片区域离线使用；未检查全部区域。", true, false)
                : new("empty", "未发现本地图块。请将 z/x/y 图片或 PBF 文件放入配置目录。", false, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new("error", "无法读取本地图块目录，请检查路径和访问权限。", false, false);
        }
    }

    private async Task<MapLayerCheckDto> CheckPmtilesAsync(TileProvider provider, CancellationToken token)
    {
        var local = MapCacheDirectory(provider.Folder);
        try
        {
            if (!File.Exists(local))
                return new("empty", "尚未找到本地 PMTiles 文件。可将归档复制到设置的路径。", false, false);
            await using var input = File.OpenRead(local);
            var header = new byte[127];
            var read = 0;
            while (read < header.Length)
            {
                var count = await input.ReadAsync(header.AsMemory(read), token);
                if (count == 0) break;
                read += count;
            }
            if (read != 127 || !ValidPmtilesHeader(header))
                return new("unavailable", "本地文件不是有效的 PMTiles v3 归档。", false, false);
            var type = header[99] switch
            {
                1 => "MVT/PBF 矢量",
                2 => "PNG 图片",
                3 => "JPG 图片",
                4 => "WebP 图片",
                _ => "未知图块类型"
            };
            return new("cached", $"本地 PMTiles v3 归档可读取。图块类型：{type}，缩放级别：{header[100]}–{header[101]}。未逐一检查图块。", true, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new("error", "无法读取 PMTiles 文件，请检查路径与权限。", false, false); }
    }

    private async Task ServePmtilesArchiveAsync(HttpContext context)
    {
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(Api.Id(context));
        if (!provider.Enabled || !IsPmtiles(provider)) { context.Response.StatusCode = 404; return; }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.AcceptRanges = "bytes";
        var local = MapCacheDirectory(provider.Folder);
        if (!File.Exists(local)) { context.Response.StatusCode = 404; return; }
        // ASP.NET Core handles byte ranges directly from the local file.
        await Results.File(local, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
    }

    private static string? CachedMapTile(string path, TileProvider provider) => TileExtensions(provider).Select(extension => path + extension).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    private string? GetMapTile(string layer, int z, int x, int y)
    {
        if (z < 0 || z > 18 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return null;
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(layer);
        if (!provider.Enabled || IsPmtiles(provider)) return null;
        var row = IsTms(provider) ? (1 << z) - 1 - y : y;
        var path = Path.Combine(MapCacheDirectory(provider.Folder),
            z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), row.ToString(CultureInfo.InvariantCulture));
        return CachedMapTile(path, provider);
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
        app.MapGet("/api/map/style/{id}/style.json", ServeMapStyleJsonAsync);
        app.MapGet("/api/map/style/{id}/assets/{**asset}", ServeMapStyleAssetAsync);
        app.MapPost("/api/map/settings", create);
        app.MapPut("/api/map/settings/order", order);
        app.MapPut("/api/map/settings/{id}/enabled", enabled);
        app.MapDelete("/api/map/settings/{id}", delete);
        app.MapGet("/api/map/settings", settings);
        app.MapGet("/api/map/settings/{id}/check", check);
        app.MapPut("/api/map/settings/{id}", save);
        RequestDelegate tile = async context =>
        {
            var file = GetMapTile((string)context.Request.RouteValues["layer"]!, int.Parse((string)context.Request.RouteValues["z"]!, CultureInfo.InvariantCulture), int.Parse((string)context.Request.RouteValues["x"]!, CultureInfo.InvariantCulture), int.Parse((string)context.Request.RouteValues["y"]!, CultureInfo.InvariantCulture));
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
