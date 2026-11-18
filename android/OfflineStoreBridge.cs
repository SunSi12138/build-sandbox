using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    // Android-only adapters expose the original Store's validated local paths
    // without allowing the HTTP layer to choose arbitrary filesystem locations.
    public string OfflinePmtilesPath(string id)
    {
        lock (_gate)
        {
            var layer = GetMapProvider(id);
            if (!layer.Enabled || layer.Format != "pmtiles")
                throw new ApiException(404, "没有可用的本地 PMTiles 图层。");
            return MapCacheDirectory(layer.Folder);
        }
    }

    public string? OfflineTilePath(string id, int z, int x, int y) => GetMapTile(id, z, x, y);

    public (TileProvider Provider, string Directory) OfflineStyleFolder(string id) =>
        ConfiguredMapStyle(id);

    public string OfflineTilesRoot => Path.Combine(RootDir, "tiles");

    public string OfflinePhotoPath(string photoId)
    {
        // OpenPhoto performs the same database and path checks as desktop.
        // We return file bytes through OpenPhoto instead of exposing paths.
        using var stream = OpenPhoto(photoId);
        return stream is FileStream file ? file.Name : throw new ApiException(404, "照片不存在。");
    }

    public void UpdateOfflineMapStyle(string providerId, string styleFolder)
    {
        var layer = GetMapSettings().Providers.FirstOrDefault(p => p.Id == providerId)
            ?? throw new ApiException(404, "地图图层不存在。");
        SaveMapProvider(providerId, layer with { StyleDirectory = styleFolder });
    }
}
