using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    // The style directory is a user-configured filesystem path, relative to the map cache root.
    // Neither the files nor their contents are included in the web bundle or copied on save.
    private (TileProvider Provider, string Directory) ConfiguredMapStyle(string id)
    {
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(id);
        if (provider.Format is not ("mvt" or "pmtiles") || provider.StyleDirectory.Length == 0)
            throw new ApiException(404, "该图层未配置矢量样式目录。");
        return (provider, MapCacheDirectory(provider.StyleDirectory));
    }

    private async Task ServeMapStyleJsonAsync(HttpContext context)
    {
        var id = Api.Id(context);
        var (provider, directory) = ConfiguredMapStyle(id);
        var path = Path.Combine(directory, "style.json");
        if (!File.Exists(path)) { context.Response.StatusCode = 404; return; }

        using var input = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: context.RequestAborted);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("sources", out var sources) ||
            sources.ValueKind != JsonValueKind.Object)
            throw new ApiException(400, "样式目录中的 style.json 缺少 sources。");

        // A configured style is local data, not permission to fetch map assets
        // over the network. MapLibre supports imports and remote sources, but
        // those capabilities are deliberately not exposed here.
        if (document.RootElement.TryGetProperty("imports", out var imports) &&
            imports.ValueKind == JsonValueKind.Array && imports.GetArrayLength() != 0)
            throw new ApiException(400, "离线地图样式不支持在线 style imports，请合并到本地 style.json。");

        var prefix = "/api/map/style/" + Uri.EscapeDataString(id) + "/assets/";
        var tiles = provider.Format == "pmtiles"
            ? "hbtiles://" + id + "/{z}/{x}/{y}"
            : "/api/map/tiles/" + Uri.EscapeDataString(id) + "/{z}/{x}/{y}?cache=server";

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name == "sources")
                {
                    writer.WritePropertyName("sources");
                    writer.WriteStartObject();
                    foreach (var entry in sources.EnumerateObject())
                    {
                        writer.WritePropertyName(entry.Name);
                        if (entry.Value.ValueKind != JsonValueKind.Object ||
                            !entry.Value.TryGetProperty("type", out var sourceType) ||
                            sourceType.ValueKind != JsonValueKind.String)
                            throw new ApiException(400, "离线样式源必须声明类型。");
                        if (sourceType.GetString() != "vector")
                        {
                            // Inline GeoJSON is local; remote raster, metadata
                            // or externally referenced GeoJSON are not.
                            if (sourceType.GetString() is not "geojson" ||
                                !entry.Value.TryGetProperty("data", out var data) ||
                                data.ValueKind is not JsonValueKind.Object)
                                throw new ApiException(400, "离线样式只支持矢量源或内嵌 GeoJSON，不能引用在线图层。");
                            entry.Value.WriteTo(writer);
                            continue;
                        }
                        writer.WriteStartObject();
                        foreach (var attribute in entry.Value.EnumerateObject())
                        {
                            if (attribute.Name is "url" or "tiles") continue;
                            attribute.WriteTo(writer);
                        }
                        writer.WritePropertyName("tiles");
                        writer.WriteStartArray();
                        writer.WriteStringValue(tiles);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                else if (property.Name is "glyphs" or "sprite")
                {
                    if (property.Value.ValueKind != JsonValueKind.String)
                        throw new ApiException(400, "离线样式的字体与精灵图必须引用本地路径。");
                    var value = property.Value.GetString() ?? "";
                    if (value.StartsWith("//", StringComparison.Ordinal) ||
                        Uri.TryCreate(value, UriKind.Absolute, out _) ||
                        value.Contains('\\') || value.Split('/').Any(part => part is "." or ".."))
                        throw new ApiException(400, "离线样式的字体和精灵图必须使用样式目录内的本地路径。");
                    var local = value.TrimStart('/');
                    if (!(local.StartsWith("font/", StringComparison.Ordinal) ||
                          local.StartsWith("sprite", StringComparison.Ordinal)))
                        throw new ApiException(400, "离线字体和精灵图必须放在 font/ 或 sprite/ 目录下。");
                    writer.WriteString(property.Name, prefix + local);
                }
                else property.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(output.ToArray(), context.RequestAborted);
    }

    private async Task ServeMapStyleAssetAsync(HttpContext context)
    {
        var id = Api.Id(context);
        var relative = (string?)context.Request.RouteValues["asset"] ?? "";
        var (_, directory) = ConfiguredMapStyle(id);
        var parts = relative.Split('/');
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        if (relative.StartsWith("/", StringComparison.Ordinal) ||
            relative.Contains('\\', StringComparison.Ordinal) ||
            parts.Any(part => part is "" or "." or ".." || part.Contains(':')) ||
            parts[0] is not ("font" or "sprite") ||
            extension is not (".pbf" or ".png" or ".webp" or ".json"))
        {
            context.Response.StatusCode = 404;
            return;
        }
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = extension switch
        {
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/x-protobuf"
        };
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }
}
