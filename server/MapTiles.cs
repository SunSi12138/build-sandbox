using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

// Private deployment configuration. Keys never enter browser responses or source control.
public sealed record TileProvider(string Id, string Folder, string Scheme = "xyz", string UrlTemplate = "", string ApiKey = "", string MapKey = "", string Subdomains = "0,1,2,3,4,5,6,7");
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TileProvider[]))]
public partial class MapConfigJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private static readonly HttpClient MapHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _mapDownloads = new(6);

    public void MapApi(WebApplication app)
    {
        var config = Path.Combine(RootDir, "map-providers.local.json");
        var providers = File.Exists(config) ? JsonSerializer.Deserialize(File.ReadAllText(config), MapConfigJsonContext.Default.TileProviderArray) ?? [] : [];
        RequestDelegate tile = async context =>
        {
            var id = (string)context.Request.RouteValues["layer"]!;
            var z = int.Parse((string)context.Request.RouteValues["z"]!, CultureInfo.InvariantCulture);
            var x = int.Parse((string)context.Request.RouteValues["x"]!, CultureInfo.InvariantCulture);
            var y = int.Parse((string)context.Request.RouteValues["y"]!, CultureInfo.InvariantCulture);
            if (id is not ("jlyh" or "labels") || z < 0 || z > 18 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) { context.Response.StatusCode = 404; return; }
            var provider = providers.FirstOrDefault(p => p.Id == (id == "labels" ? "tdt-cva" : id));
            // Fixed folder names prevent deployment configuration from escaping the tile directory.
            var folder = id == "labels" ? "tdt-cva" : "jlyh";
            var row = id == "jlyh" ? (1 << z) - 1 - y : y;
            var path = Path.Combine(RootDir, "tiles", folder, z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), row.ToString(CultureInfo.InvariantCulture));
            static string? Cached(string path) => new[] { ".png", ".jpg", ".webp" }.Select(ext => path + ext).FirstOrDefault(File.Exists);
            var file = Cached(path);
            if (file is null && provider is not null && provider.UrlTemplate.Length > 0)
            {
                await _mapDownloads.WaitAsync(context.RequestAborted);
                try
                {
                    file = Cached(path);
                    if (file is null)
                    {
                        var hosts = provider.Subdomains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var url = provider.UrlTemplate.Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                            .Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{y}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                            .Replace("{s}", hosts.Length > 0 ? hosts[(x + y) % hosts.Length] : "0", StringComparison.Ordinal)
                            .Replace("{key}", Uri.EscapeDataString(provider.ApiKey), StringComparison.Ordinal).Replace("{mk}", Uri.EscapeDataString(provider.MapKey), StringComparison.Ordinal);
                        using var response = await MapHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new IOException("地图图片过大。");
                        using var bytes = new MemoryStream();
                        await using (var input = await response.Content.ReadAsStreamAsync(context.RequestAborted))
                        {
                            var buffer = new byte[81920]; int read;
                            while ((read = await input.ReadAsync(buffer, context.RequestAborted)) > 0)
                            {
                                if (bytes.Length + read > 10 * 1024 * 1024) throw new IOException("地图图片过大。");
                                bytes.Write(buffer, 0, read);
                            }
                        }
                        var data = bytes.ToArray();
                        var extension = data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ? ".png"
                            : data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? ".jpg"
                            : data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WEBP" ? ".webp" : throw new IOException("地图服务未返回图片。");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var temp = path + "." + NewId() + ".tmp";
                        try { await File.WriteAllBytesAsync(temp, data, context.RequestAborted); File.Move(temp, path + extension, true); }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                        file = path + extension;
                    }
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException) { file = Cached(path); }
                finally { _mapDownloads.Release(); }
            }
            if (file is null) { context.Response.StatusCode = 404; return; }
            context.Response.ContentType = Path.GetExtension(file) switch { ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png" };
            context.Response.Headers.CacheControl = "public,max-age=86400";
            await context.Response.SendFileAsync(file, context.RequestAborted);
        };
        app.MapGet("/api/map/tiles/{layer}/{z:int}/{x:int}/{y:int}", tile);
    }
}
