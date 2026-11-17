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
                            sourceType.GetString() != "vector")
                        {
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
                else if ((property.Name is "glyphs" or "sprite") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString() ?? "";
                    writer.WriteString(property.Name,
                        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                            ? value : prefix + value.TrimStart('/'));
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
