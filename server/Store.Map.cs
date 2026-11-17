using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreetEcoTool;

public sealed partial class Store
{
    public MapLayerDto[] GetMapLayers()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,folder,scheme,enabled,is_default,opacity,min_zoom,max_zoom,sort_order,url_template,api_key,subdomains,update_cache,cache_version,annotation,map_key,tile_type,style_json FROM map_layers ORDER BY sort_order,id;");
            using var rows = command.ExecuteReader();
            var items = new List<MapLayerDto>();
            while (rows.Read()) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetInt32(4) != 0, rows.GetInt32(5) != 0, rows.GetDouble(6), rows.GetInt32(7), rows.GetInt32(8), rows.GetInt32(9), rows.GetString(10), rows.GetString(11), rows.GetString(12), rows.GetInt32(13) != 0, rows.GetInt32(14), rows.GetInt32(15) != 0, rows.GetString(16), rows.GetString(17), rows.GetString(18)));
            return [.. items];
        }
    }

    public MapLayerDto SaveMapLayer(MapLayerDto request)
    {
        if (!Regex.IsMatch(request.Id, "^[a-zA-Z0-9_-]{1,60}$")) throw new ApiException(400, "图层编号不正确。");
        var folder = Text(request.Folder, "本地缓存目录", 1024, true);
        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || (!Path.IsPathFullyQualified(folder) && (folder.Split(['/', '\\']).Any(x => x is ".." or "." or "")))) throw new ApiException(400, "本地缓存目录不正确。");
        if (request.TileType is not ("raster" or "mvt")) throw new ApiException(400, "请选择瓦片类型。");
        if (request.Scheme is not ("xyz" or "tms") || request.MinZoom < 0 || request.MaxZoom > 22 || request.MaxZoom < request.MinZoom || !double.IsFinite(request.Opacity) || request.Opacity < 0 || request.Opacity > 1) throw new ApiException(400, "图层参数不正确。");
        if (!string.IsNullOrWhiteSpace(request.StyleJson))
        {
            try { using var style = JsonDocument.Parse(request.StyleJson); if (style.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); }
            catch (JsonException) { throw new ApiException(400, "MVT 样式 JSON 不正确。"); }
        }
        var value = request with { Name = Text(request.Name, "图层名称", 100, true), Folder = folder, UpdateCache = false, UrlTemplate = "", ApiKey = "", MapKey = "" };
        lock (_gate)
        {
            PrepareWrite();
            using var transaction = _db.BeginTransaction();
            if (value.IsDefault)
            {
                using var current = Command("SELECT id FROM map_layers WHERE is_default=1 LIMIT 1;", transaction);

                using var reset = Command("UPDATE map_layers SET is_default=0;", transaction); reset.ExecuteNonQuery();
            }
            using (var command = Command("""
                INSERT INTO map_layers(id,name,folder,scheme,enabled,is_default,opacity,min_zoom,max_zoom,sort_order,url_template,api_key,subdomains,update_cache,annotation,map_key,tile_type,style_json)
                VALUES($id,$name,$folder,$scheme,$enabled,$default,$opacity,$min,$max,$order,$url,$key,$subdomains,$update,$annotation,$mk,$tileType,$styleJson)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,folder=excluded.folder,scheme=excluded.scheme,enabled=excluded.enabled,
                  is_default=excluded.is_default,opacity=excluded.opacity,min_zoom=excluded.min_zoom,max_zoom=excluded.max_zoom,sort_order=excluded.sort_order,
                  url_template=excluded.url_template,api_key=excluded.api_key,subdomains=excluded.subdomains,update_cache=excluded.update_cache,annotation=excluded.annotation,map_key=excluded.map_key,tile_type=excluded.tile_type,style_json=excluded.style_json;
                """, transaction, ("$id", value.Id), ("$name", value.Name), ("$folder", value.Folder), ("$scheme", value.Scheme),
                ("$enabled", value.Enabled ? 1 : 0), ("$default", value.IsDefault ? 1 : 0), ("$opacity", value.Opacity), ("$min", value.MinZoom), ("$max", value.MaxZoom), ("$order", value.SortOrder),
                ("$url", value.UrlTemplate), ("$key", value.ApiKey), ("$subdomains", value.Subdomains), ("$update", value.UpdateCache ? 1 : 0), ("$annotation", value.Annotation ? 1 : 0), ("$mk", value.MapKey), ("$tileType", value.TileType), ("$styleJson", value.StyleJson))) command.ExecuteNonQuery();
            transaction.Commit(); return value;
        }
    }

    public void DeleteMapLayer(string id)
    {
        lock (_gate)
        {
            PrepareWrite();
            using var command = Command("DELETE FROM map_layers WHERE id=$id;", null, ("$id", id));
            if (command.ExecuteNonQuery() == 0) throw new ApiException(404, "图层不存在。");
        }
    }

    public MapStateDto GetMapState()
    {
        lock (_gate)
        {
            var features = new List<MapFeatureDto>();
            using (var command = Command("""
                SELECT p.id,p.name,p.community_id,p.longitude,p.latitude,p.disabled,s.shape,COALESCE(s.revision,0),
                  (SELECT COUNT(*) FROM active_issues x JOIN active_inspections n ON n.id=x.inspection_id WHERE n.point_id=p.id AND x.status<>'closed')
                FROM points p LEFT JOIN map_shapes s ON s.kind='point' AND s.record_id=p.id;
                """))
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                {
                    var shape = rows.IsDBNull(6) ? rows.IsDBNull(3) || rows.IsDBNull(4) ? null : new GeoShape("Point", [[rows.GetDouble(3), rows.GetDouble(4)]])
                        : JsonSerializer.Deserialize(rows.GetString(6), CommunityJsonContext.Default.GeoShape);
                    if (shape is not null) features.Add(new(rows.GetString(0), "point", rows.GetString(1), rows.GetString(2), shape, rows.GetInt32(7), rows.GetInt32(8), rows.GetInt32(5) != 0));
                }
            using (var command = Command("SELECT c.id,c.name,s.shape,s.revision FROM communities c JOIN map_shapes s ON s.kind='grid' AND s.record_id=c.id;"))
            using (var rows = command.ExecuteReader())
                while (rows.Read()) features.Add(new(rows.GetString(0), "grid", rows.GetString(1), rows.GetString(0), JsonSerializer.Deserialize(rows.GetString(2), CommunityJsonContext.Default.GeoShape)!, rows.GetInt32(3)));

            var purposes = ListCommunityRecords("purpose-type", includeArchived:true);
            var uses = ListCommunityRecords("use").Where(x => JsonText(x.Data, "state") != "已结束").GroupBy(x => x.ParentId).ToDictionary(x => x.Key, x => x.ToArray());
            return new(GetMapLayers(), features.Select(f =>
            {
                if (f.Kind == "grid") return f with { Color = RequireCommunity(f.Id).Color, Uses = [] };
                var point = GetPointInternal(f.Id);
                var ids = uses.GetValueOrDefault(f.Id, []).Select(x => JsonText(x.Data, "purposeId")).Where(x => x.Length > 0).Distinct().ToArray();
                var labels = uses.GetValueOrDefault(f.Id, []).Select(x => {
                    var purpose = purposes.FirstOrDefault(p=>p.Id==JsonText(x.Data,"purposeId"));
                    return new MapUseLabel(x.Id,x.Name,purpose?.Name??"未分类",purpose is null?"📌":JsonText(purpose.Data,"icon"));
                }).ToArray();
                return f with { TypeId = point.TypeId, Color = point.Color, PurposeIds = ids, Uses = labels };
            }).ToArray());
        }
    }

    public MapFeatureDto SaveShape(string kind, string id, ShapeWrite request)
    {
        if (kind is not ("point" or "grid")) throw new ApiException(400, "只能标记场所或网格。");
        ValidateShape(request.Shape);
        if (kind == "grid" && request.Shape.Type == "Point") throw new ApiException(400, "请圈出网格范围。");
        lock (_gate)
        {
            PrepareWrite();
            if (kind == "point") { _ = GetPointInternal(id); RequireUnmergedPoint(id); } else _ = RequireCommunity(id);
            var oldRevision = 0; var before = "";
            using (var query = Command("SELECT shape,revision FROM map_shapes WHERE kind=$kind AND record_id=$id;", null, ("$kind", kind), ("$id", id)))
            using (var row = query.ExecuteReader()) if (row.Read()) { before = row.GetString(0); oldRevision = row.GetInt32(1); }
            if (oldRevision != request.Revision) throw new ApiException(409, "地图标记已被修改，请刷新后重新编辑。");
            var json = JsonSerializer.Serialize(request.Shape, CommunityJsonContext.Default.GeoShape);
            using var transaction = _db.BeginTransaction();
            using (var command = Command("INSERT INTO map_shapes(kind,record_id,shape,revision) VALUES($kind,$id,$shape,$revision) ON CONFLICT(kind,record_id) DO UPDATE SET shape=excluded.shape,revision=excluded.revision;", transaction,
                ("$kind", kind), ("$id", id), ("$shape", json), ("$revision", oldRevision + 1))) command.ExecuteNonQuery();
            if (kind == "point")
            {
                var xy = request.Shape.Coordinates[0];
                using var command = Command("UPDATE points SET longitude=$x,latitude=$y WHERE id=$id;", transaction, ("$id", id), ("$x", xy[0]), ("$y", xy[1]));
                command.ExecuteNonQuery();
            }
            AddCommunityHistory(kind, id, before, json, "修改地图位置", transaction);
            transaction.Commit();
            return GetMapState().Features.Single(x => x.Id == id && x.Kind == kind);
        }
    }

    public static void ValidateShape(GeoShape shape)
    {
        if (shape?.Type == "MultiPolygon")
        {
            if (shape.Areas is not { Length: >= 2 and <= 50 } || shape.Areas.Sum(x => x?.Length ?? 0) > 10000) throw new ApiException(400, "一个场所可画 2 至 50 个区域，总节点不超过 10000。");
            foreach (var area in shape.Areas) ValidateShape(new GeoShape("Polygon", area));
            if (shape.Coordinates is null || !shape.Coordinates.SelectMany(x => x).SequenceEqual(shape.Areas[0].SelectMany(x => x))) throw new ApiException(400, "多区域首块边界不一致。");
            return;
        }
        if (shape is null || shape.Type is not ("Point" or "Polygon") || shape.Coordinates is null || shape.Coordinates.Length > 10000) throw new ApiException(400, "地图标记格式不正确。");
        if (shape.Coordinates.Any(x => x is null || x.Length != 2 || !double.IsFinite(x[0]) || !double.IsFinite(x[1]) || x[0] < -180 || x[0] > 180 || x[1] < -85 || x[1] > 85)) throw new ApiException(400, "经纬度超出范围。");
        if (shape.Type == "Point" && shape.Coordinates.Length != 1) throw new ApiException(400, "请标记一个点。");
        if (shape.Type == "Polygon")
        {
            var ring = shape.Coordinates;
            if (ring.Length < 4 || !ring[0].SequenceEqual(ring[^1])) throw new ApiException(400, "区域至少需要三个点，并闭合边界。");
            var area = 0.0;
            for (var i = 0; i < ring.Length - 1; i++) area += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1];
            if (Math.Abs(area) < 1e-12) throw new ApiException(400, "区域不能只有一条线。");
            for (var i = 0; i < ring.Length - 1; i++)
            {
                if (ring[i].SequenceEqual(ring[i + 1])) throw new ApiException(400, "区域边界有重复点，请移除重复点。");
                for (var j = i + 2; j < ring.Length - 1; j++)
                {
                    if (i == 0 && j == ring.Length - 2) continue;
                    if (SegmentsIntersect(ring[i], ring[i + 1], ring[j], ring[j + 1])) throw new ApiException(400, "区域边界互相交叉，请调整交叉位置。");
                }
            }
        }
    }

    private static bool SegmentsIntersect(double[] a, double[] b, double[] c, double[] d)
    {
        static double Cross(double[] p, double[] q, double[] r) => (q[0]-p[0])*(r[1]-p[1])-(q[1]-p[1])*(r[0]-p[0]);
        static bool On(double[] p, double[] q, double[] r) => r[0]>=Math.Min(p[0],q[0]) && r[0]<=Math.Max(p[0],q[0]) && r[1]>=Math.Min(p[1],q[1]) && r[1]<=Math.Max(p[1],q[1]);
        var abC=Cross(a,b,c); var abD=Cross(a,b,d); var cdA=Cross(c,d,a); var cdB=Cross(c,d,b);
        return (abC*abD<0 && cdA*cdB<0) || (abC==0 && On(a,b,c)) || (abD==0 && On(a,b,d)) || (cdA==0 && On(c,d,a)) || (cdB==0 && On(c,d,b));
    }

    public string? FindTile(string layerId, int z, int x, int y)
    {
        var layer = GetMapLayers().FirstOrDefault(x => x.Id == layerId) ?? throw new ApiException(404, "图层不存在。");
        if (z < layer.MinZoom || z > layer.MaxZoom || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return null;
        var row = layer.Scheme == "tms" ? (1 << z) - 1 - y : y;
        var directory = Path.IsPathFullyQualified(layer.Folder) ? layer.Folder : Path.Combine(RootDir, "tiles", layer.Folder);
        var tilePath = Path.Combine(directory, z.ToString(), x.ToString(), row.ToString());
        return new[] { ".png", ".jpg", ".jpeg", ".webp", ".pbf", ".mvt" }.Select(ext => tilePath + ext).FirstOrDefault(File.Exists);
    }
}
