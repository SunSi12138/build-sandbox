using System.Text.Json;

namespace StreetEcoTool;

internal static class OfflineMapStyle
{
    private static bool IsFontDirectory(string value) => value is "font" or "fonts" or "glyphs";
    private static bool IsSpriteDirectory(string value) => value is "sprite" or "sprites";

    private static string LocalStyleResource(string value, bool sprite)
    {
        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains(':') || value.Contains('\\') ||
            value.Contains('?') || value.Contains('#') || value.Contains('%'))
            throw new ApiException(400, "离线样式的字体和精灵图必须使用样式目录内的本地路径。");
        var local = value.TrimStart('/');
        while (local.StartsWith("./", StringComparison.Ordinal)) local = local[2..];
        var parts = local.Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
            throw new ApiException(400, "离线样式资源路径无效。");
        var allowed = sprite
            ? IsSpriteDirectory(parts[0]) || parts.Length == 1 && parts[0].StartsWith("sprite", StringComparison.Ordinal)
            : IsFontDirectory(parts[0]);
        if (!allowed) throw new ApiException(400, "离线字体或精灵图目录无效。");
        return local;
    }

    internal static bool IsFullStyle(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out var number) && number == 8 &&
                document.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("layers", out var layers) && layers.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }

    internal static async Task WriteAsync(HttpContext context, JsonDocument document, string id, int minZoom, int maxZoom)
    {
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
        var tiles = "/api/map/tiles/" + Uri.EscapeDataString(id) + "/{z}/{x}/{y}?cache=server";

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
                            if (attribute.Name is "url" or "tiles" or "scheme" or "minzoom" or "maxzoom" or "encoding") continue;
                            attribute.WriteTo(writer);
                        }
                        writer.WriteString("scheme", "xyz");
                        writer.WriteNumber("minzoom", minZoom);
                        writer.WriteNumber("maxzoom", maxZoom);
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
                    if (property.Name == "sprite" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WritePropertyName("sprite");
                        writer.WriteStartArray();
                        foreach (var sprite in property.Value.EnumerateArray())
                        {
                            if (sprite.ValueKind != JsonValueKind.Object ||
                                !sprite.TryGetProperty("id", out var spriteId) || spriteId.ValueKind != JsonValueKind.String ||
                                !sprite.TryGetProperty("url", out var spriteUrl) || spriteUrl.ValueKind != JsonValueKind.String)
                                throw new ApiException(400, "离线精灵图列表格式无效。");
                            writer.WriteStartObject();
                            writer.WriteString("id", spriteId.GetString());
                            writer.WriteString("url", prefix + LocalStyleResource(spriteUrl.GetString()!, true));
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    else
                    {
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw new ApiException(400, "离线样式的字体与精灵图必须引用本地路径。");
                        writer.WriteString(property.Name, prefix + LocalStyleResource(property.Value.GetString()!, property.Name == "sprite"));
                    }
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

    internal static async Task AssetAsync(HttpContext context, string directory, string relative)
    {
        var parts = relative.Split('/');
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        if (relative.StartsWith("/", StringComparison.Ordinal) ||
            relative.Contains('\\', StringComparison.Ordinal) ||
            parts.Any(part => part is "" or "." or ".." || part.Contains(':')) ||
            !(IsFontDirectory(parts[0]) && extension == ".pbf" ||
                (IsSpriteDirectory(parts[0]) || parts.Length == 1 && parts[0].StartsWith("sprite", StringComparison.Ordinal)) &&
                extension is ".png" or ".webp" or ".json"))
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
        if (extension == ".pbf")
        {
            using var stream = File.OpenRead(path);
            var signature = new byte[2];
            if (stream.Read(signature) == 2 && signature[0] == 0x1f && signature[1] == 0x8b)
            {
                stream.Position = 0;
                if (stream.Length > 24 * 1024 * 1024) throw new ApiException(400, "字体资源过大。");
                var bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
                await PmTiles.WriteTileAsync(context, PmTiles.EncodeForClient(bytes, context.Response.ContentType!, 2, context.Request.Headers.AcceptEncoding.ToString()));
                return;
            }
        }
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }
}
