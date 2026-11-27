using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    // Preserve the existing inline JSON exactly when no style directory is set.
    // A reserved envelope in the existing column stores only the optional path
    // and the original inline fallback; no migration or schema change.
    private static MapLayerDto DecodeMapStyle(MapLayerDto layer)
    {
        if (string.IsNullOrWhiteSpace(layer.StyleJson)) return layer;
        try
        {
            using var document = JsonDocument.Parse(layer.StyleJson);
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("$communityGisStyleDirectory", out var directory) && directory.ValueKind == JsonValueKind.String &&
                value.TryGetProperty("$inlineStyle", out var inline) && inline.ValueKind == JsonValueKind.String)
                return layer with { StyleDirectory = directory.GetString()!, StyleJson = inline.GetString()! };
        }
        catch (JsonException) { /* Existing malformed legacy style remains a fallback. */ }
        return layer;
    }

    private static string EncodeMapStyle(MapLayerDto layer)
    {
        if (layer.StyleDirectory.Length == 0) return layer.StyleJson;
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("$communityGisStyleDirectory", layer.StyleDirectory);
            writer.WriteString("$inlineStyle", layer.StyleJson);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private string ResolveStyleDirectory(string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(RootDir, "tiles", path));

    private (MapLayerDto Layer, string Directory, string? Inline, int Min, int Max) ResolveMapStyle(string id)
    {
        var layer = GetMapLayers().FirstOrDefault(item => item.Id == id) ?? throw new ApiException(404, "图层不存在。");
        var path = MapLayerPath(layer);
        var archive = PmTiles.IsArchivePath(path) ? PmTiles.Inspect(path) : null;
        if (archive?.Error is not null) throw new ApiException(400, archive.Error);
        if ((archive?.TileType ?? layer.TileType) != "mvt") throw new ApiException(404, "该图层不是矢量地图。");
        var min = layer.MinZoom; var max = layer.MaxZoom;
        if (archive is not null)
        {
            min = Math.Max(min, archive.MinZoom); max = Math.Min(max, archive.MaxZoom);
            if (min > max) { min = archive.MinZoom; max = archive.MaxZoom; }
        }
        if (layer.StyleDirectory.Length > 0)
        {
            var directory = ResolveStyleDirectory(layer.StyleDirectory);
            if (File.Exists(Path.Combine(directory, "style.json"))) return (layer, directory, null, min, max);
        }
        if (OfflineMapStyle.IsFullStyle(layer.StyleJson))
        {
            var directory = PmTiles.IsArchivePath(path) ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);
            return (layer, directory, layer.StyleJson, min, max);
        }
        throw new ApiException(404, "未找到本地矢量样式。");
    }

    internal void MapOfflineStyleApi(WebApplication app)
    {
        RequestDelegate style = async context =>
        {
            var id = Api.Id(context);
            var (_, directory, inline, min, max) = ResolveMapStyle(id);
            if (inline is not null)
            {
                using var document = JsonDocument.Parse(inline);
                await OfflineMapStyle.WriteAsync(context, document, id, min, max);
            }
            else
            {
                using var stream = File.OpenRead(Path.Combine(directory, "style.json"));
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: context.RequestAborted);
                await OfflineMapStyle.WriteAsync(context, document, id, min, max);
            }
        };
        RequestDelegate asset = context =>
        {
            var (_, directory, _, _, _) = ResolveMapStyle(Api.Id(context));
            return OfflineMapStyle.AssetAsync(context, directory, (string?)context.Request.RouteValues["asset"] ?? "");
        };
        app.MapGet("/api/map/style/{id}/style.json", style);
        app.MapGet("/api/map/style/{id}/assets/{**asset}", asset);
    }
}
