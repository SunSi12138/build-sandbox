using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private void LoadInitialMapProviders(SqliteTransaction transaction)
    {
        var config = Path.Combine(RootDir,"map-providers.local.json");
        if (File.Exists(config))
        {
            using var stream = File.OpenRead(config);
            var providers = JsonSerializer.Deserialize(stream,CommunityJsonContext.Default.MapLayerDtoArray) ?? [];
            foreach (var provider in providers)
            {
                ValidateTileTemplate(provider);
                using var update = Command("UPDATE map_layers SET url_template=$url,api_key=$key,map_key=$mk,min_zoom=$min,max_zoom=$max,annotation=$annotation,sort_order=$order WHERE id=$id;",transaction,
                    ("$id",provider.Id),("$url",provider.UrlTemplate),("$key",provider.ApiKey),("$mk",provider.MapKey),("$min",provider.MinZoom),("$max",provider.MaxZoom),("$annotation",provider.Annotation?1:0),("$order",provider.SortOrder));
                update.ExecuteNonQuery();
            }
        }
    }
    private static string MapColor(string color)
    {
        if (color is null || !System.Text.RegularExpressions.Regex.IsMatch(color,"^#[a-fA-F0-9]{6}$")) throw new ApiException(400,"请选择有效的地图颜色。");
        return color.ToLowerInvariant();
    }

}
