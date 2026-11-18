using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StreetEcoTool;

namespace StreetEcoTool.Mobile;

internal static partial class OfflineMap
{
    private static readonly Regex RangePattern = new(@"^bytes=(\d+)-(\d+)$", RegexOptions.CultureInvariant);

    public static async Task<OfflineResponse> ArchiveAsync(Store store, OfflineRequest request, CancellationToken token)
    {
        if (request.Method != "GET") return OfflineResponse.Text("Method not allowed", 405);
        var id = request.Path["/api/map/archive/".Length..];
        var path = store.OfflinePmtilesPath(id);
        if (!File.Exists(path)) return OfflineResponse.Text("PMTiles not found", 404);
        var match = RangePattern.Match(request.Header("Range"));
        if (!match.Success ||
            !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            end < start || end - start >= 16 * 1024 * 1024)
            return new OfflineResponse(416, "application/octet-stream", [],
                new Dictionary<string, string> { ["Accept-Ranges"] = "bytes" });
        await using var file = File.OpenRead(path);
        if (start >= file.Length) return OfflineResponse.Text("Range not satisfiable", 416);
        end = Math.Min(end, file.Length - 1);
        file.Seek(start, SeekOrigin.Begin);
        var data = new byte[(int)(end - start + 1)];
        await file.ReadExactlyAsync(data, token);
        return new OfflineResponse(206, "application/octet-stream", data,
            new Dictionary<string, string>
            {
                ["Accept-Ranges"] = "bytes",
                ["Content-Range"] = $"bytes {start}-{end}/{file.Length}",
            });
    }

    public static OfflineResponse Tile(Store store, OfflineRequest request)
    {
        if (request.Method != "GET") return OfflineResponse.Text("Method not allowed", 405);
        var segments = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 7 ||
            !int.TryParse(segments[4], out var z) ||
            !int.TryParse(segments[5], out var x) ||
            !int.TryParse(segments[6], out var y))
            return OfflineResponse.Text("Not found", 404);
        var file = store.OfflineTilePath(segments[3], z, x, y);
        if (file is null || !File.Exists(file)) return OfflineResponse.Text("Tile not found", 404);
        var mime = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".pbf" or ".mvt" => "application/vnd.mapbox-vector-tile",
            ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png"
        };
        var bytes = File.ReadAllBytes(file);
        var headers = mime == "application/vnd.mapbox-vector-tile" && bytes.Length > 2 &&
            bytes[0] == 0x1f && bytes[1] == 0x8b
            ? new Dictionary<string, string> { ["Content-Encoding"] = "gzip" }
            : null;
        return new OfflineResponse(200, mime, bytes, headers);
    }

    public static OfflineResponse Style(Store store, OfflineRequest request)
    {
        if (request.Method != "GET") return OfflineResponse.Text("Method not allowed", 405);
        var pieces = request.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length < 5 || pieces[0] != "api" || pieces[1] != "map" || pieces[2] != "style")
            return OfflineResponse.Text("Not found", 404);
        var id = pieces[3];
        var (provider, folder) = store.OfflineStyleFolder(id);
        if (pieces.Length == 5 && pieces[4] == "style.json")
        {
            var path = Path.Combine(folder, "style.json");
            if (!File.Exists(path)) return OfflineResponse.Text("Style not found", 404);
            var json = RewriteStyle(File.ReadAllText(path), id, provider.Format);
            return OfflineResponse.Json(System.Text.Encoding.UTF8.GetBytes(json));
        }
        if (pieces.Length < 6 || pieces[4] != "assets")
            return OfflineResponse.Text("Not found", 404);
        var rel = string.Join('/', pieces.Skip(5));
        if (rel.Contains('\\') || pieces[5] is not ("font" or "sprite") ||
            pieces.Skip(5).Any(part => part is "." or ".." or "" || part.Contains(':')))
            return OfflineResponse.Text("Not found", 404);
        var extension = Path.GetExtension(rel).ToLowerInvariant();
        if (extension is not (".pbf" or ".png" or ".webp" or ".json"))
            return OfflineResponse.Text("Not found", 404);
        var root = Path.GetFullPath(folder);
        var local = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!local.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(local))
            return OfflineResponse.Text("Not found", 404);
        var mime = extension switch
        {
            ".json" => "application/json", ".png" => "image/png",
            ".webp" => "image/webp", _ => "application/x-protobuf"
        };
        return new OfflineResponse(200, mime, File.ReadAllBytes(local));
    }

    private static string RewriteStyle(string text, string id, string format)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new ApiException(400, "style.json 格式无效。");
        if (root["imports"] is JsonArray imports && imports.Count > 0)
            throw new ApiException(400, "离线样式不允许在线 imports。");
        if (root["sources"] is not JsonObject sources)
            throw new ApiException(400, "style.json 缺少 sources。");
        foreach (var item in sources.ToList())
        {
            if (item.Value is not JsonObject source)
                throw new ApiException(400, "地图样式数据源无效。");
            var type = (string?)source["type"];
            if (type == "vector")
            {
                source.Remove("url");
                source.Remove("tiles");
                var tileUrl = format == "pmtiles"
                    ? $"hbtiles://{id}/{{z}}/{{x}}/{{y}}"
                    : $"/api/map/tiles/{Uri.EscapeDataString(id)}/{{z}}/{{x}}/{{y}}?cache=server";
                source["tiles"] = new JsonArray(JsonValue.Create(tileUrl));
            }
            else if (type != "geojson" || source["data"] is not JsonObject)
                throw new ApiException(400, "样式不能使用在线数据源。");
        }
        var prefix = "/api/map/style/" + Uri.EscapeDataString(id) + "/assets/";
        foreach (var field in new[] { "glyphs", "sprite" })
        {
            if (!root.ContainsKey(field)) continue;
            if (root[field] is not JsonValue value ||
                !value.TryGetValue<string>(out var path) ||
                string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("//", StringComparison.Ordinal) ||
                Uri.TryCreate(path, UriKind.Absolute, out _) ||
                path.Contains('\\') || path.Split('/').Any(part => part is "." or ".."))
                throw new ApiException(400, "样式资源必须使用本地字体和精灵路径。");
            var clean = path.TrimStart('/');
            if (!clean.StartsWith("font/", StringComparison.Ordinal) &&
                !clean.StartsWith("sprite", StringComparison.Ordinal))
                throw new ApiException(400, "样式目录内请使用 font/ 或 sprite/。");
            root[field] = prefix + clean;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
