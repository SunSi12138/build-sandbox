using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StreetEcoTool;

public sealed partial class Store
{
    private void InitializeGridAndProviderDefaults(SqliteTransaction transaction)
    {
        var columns = new HashSet<string>();
        using (var info = Command("PRAGMA table_info(communities);",transaction))
        using (var rows = info.ExecuteReader()) while (rows.Read()) columns.Add(rows.GetString(1));
        if (!columns.Contains("parent_id")) { using var add = Command("ALTER TABLE communities ADD COLUMN parent_id TEXT NOT NULL DEFAULT '';",transaction);add.ExecuteNonQuery(); }
        if (!columns.Contains("color")) { using var add = Command("ALTER TABLE communities ADD COLUMN color TEXT NOT NULL DEFAULT '#64748b';",transaction);add.ExecuteNonQuery(); }
        var pointColumns = new HashSet<string>();
        using(var info=Command("PRAGMA table_info(points);",transaction))
        using(var rows=info.ExecuteReader())while(rows.Read())pointColumns.Add(rows.GetString(1));
        if(!pointColumns.Contains("color")){using var add=Command("ALTER TABLE points ADD COLUMN color TEXT NOT NULL DEFAULT '#087f70';",transaction);add.ExecuteNonQuery();}
        using (var remove = Command("DELETE FROM map_layers WHERE id='tdt-vec'; UPDATE map_layers SET annotation=1,sort_order=10000 WHERE id='tdt-cva';",transaction)) remove.ExecuteNonQuery();
        var config = Path.Combine(RootDir,"map-providers.local.json");
        if (File.Exists(config))
        {
            using var stream = File.OpenRead(config);
            var providers = JsonSerializer.Deserialize(stream,CommunityJsonContext.Default.MapLayerDtoArray) ?? [];
            foreach (var provider in providers)
            {
                ValidateTileTemplate(provider);
                using var update = Command("UPDATE map_layers SET url_template=$url,api_key=$key,map_key=$mk,min_zoom=$min,max_zoom=$max,annotation=$annotation,sort_order=$order WHERE id=$id AND url_template='';",transaction,
                    ("$id",provider.Id),("$url",provider.UrlTemplate),("$key",provider.ApiKey),("$mk",provider.MapKey),("$min",provider.MinZoom),("$max",provider.MaxZoom),("$annotation",provider.Annotation?1:0),("$order",provider.SortOrder));
                update.ExecuteNonQuery();
            }
        }
        using (var stamp = Command("SELECT COUNT(*) FROM community_records WHERE id='map-defaults-v3';",transaction))
            if (Convert.ToInt32(stamp.ExecuteScalar()) > 0) return;
        using (var rename = Command("UPDATE point_types SET name='永久建筑' WHERE name='建筑物' AND NOT EXISTS(SELECT 1 FROM point_types WHERE name='永久建筑');",transaction)) rename.ExecuteNonQuery();
        foreach (var name in new[]{"地块","独立空间","院落","公共区域","其他"})
        {
            using var remove = Command("DELETE FROM point_types WHERE name=$name AND id NOT IN(SELECT type_id FROM points) AND id NOT IN(SELECT type_id FROM inspections);",transaction,("$name",name)); remove.ExecuteNonQuery();
        }
        foreach (var name in new[]{"永久建筑","临时建筑"})
        {
            using var add = Command("INSERT INTO point_types(id,name) SELECT $id,$name WHERE NOT EXISTS(SELECT 1 FROM point_types WHERE name=$name);",transaction,("$id",NewId()),("$name",name));add.ExecuteNonQuery();
        }
        using var complete = Command("INSERT INTO community_records(id,kind,parent_id,name,body,updated_at) VALUES('map-defaults-v3','system','','地图默认配置','{}',$at);",transaction,("$at",DateTimeOffset.Now.ToString("O")));complete.ExecuteNonQuery();
    }
    private static string MapColor(string color)
    {
        if (color is null || !System.Text.RegularExpressions.Regex.IsMatch(color,"^#[a-fA-F0-9]{6}$")) throw new ApiException(400,"请选择有效的地图颜色。");
        return color.ToLowerInvariant();
    }

}
