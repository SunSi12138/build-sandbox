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
        var archive = IsPmtiles(provider) ? PmTiles.Inspect(MapCacheDirectory(provider.Folder)) : null;
        if (archive is not null && (archive.Error is not null || archive.TileType != "mvt"))
            throw new ApiException(400, archive.Error ?? "该归档不是 MVT 矢量地图。");
        using var input = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(input, cancellationToken: context.RequestAborted);
        await OfflineMapStyle.WriteAsync(context, document, id, archive?.MinZoom ?? 0, archive?.MaxZoom ?? 18);
    }

    private Task ServeMapStyleAssetAsync(HttpContext context)
    {
        var (_, directory) = ConfiguredMapStyle(Api.Id(context));
        return OfflineMapStyle.AssetAsync(context, directory, (string?)context.Request.RouteValues["asset"] ?? "");
    }
}
