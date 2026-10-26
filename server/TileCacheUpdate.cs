using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StreetEcoTool;

public sealed record TileUpdateRequest(string LayerId, double West, double South, double East, double North, int MinZoom, int MaxZoom, bool Replace = true);
public sealed record TileUpdateJob(string Id, string LayerId, string State, int Total, int Processed, int Downloaded, int Failed, string Error);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TileUpdateRequest))]
[JsonSerializable(typeof(TileUpdateJob))]
public partial class TileUpdateJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private static readonly HttpClient TileHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    private sealed class TileJob(TileUpdateJob value) { public TileUpdateJob Value = value; public CancellationTokenSource Stop = new(); }
    private readonly ConcurrentDictionary<string, TileJob> _tileJobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tileLocks = new();
    public static void ValidateTileTemplate(MapLayerDto layer)
    {
        if (layer.SourceMode is not ("offline" or "online" or "hybrid")) throw new ApiException(400, "请选择地图加载方式。");
        if (layer.UrlTemplate.Length > 4000 || layer.ApiKey.Length > 1000 || layer.Subdomains.Length > 100) throw new ApiException(400, "图层链接或密钥过长。");
        if (layer.UrlTemplate.Length == 0) { if (layer.SourceMode != "offline") throw new ApiException(400, "请填写瓦片链接。"); return; }
        if (!layer.UrlTemplate.Contains("{quadkey}", StringComparison.Ordinal) && (!layer.UrlTemplate.Contains("{z}", StringComparison.Ordinal) || !layer.UrlTemplate.Contains("{x}", StringComparison.Ordinal) || !(layer.UrlTemplate.Contains("{y}", StringComparison.Ordinal) || layer.UrlTemplate.Contains("{-y}", StringComparison.Ordinal)))) throw new ApiException(400, "链接需包含 {z}、{x}、{y}（或 {-y}），或 {quadkey}。");
        var url = BuildTileUrl(layer, 1, 0, 0);
        if (Regex.IsMatch(url, "[{}]") || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new ApiException(400, "请填写有效 HTTP/HTTPS 链接；支持 {z} {x} {y} {-y} {quadkey} {s} {key}。");
    }
    public static string BuildTileUrl(MapLayerDto layer, int z, int x, int y)
    {
        var subdomains = layer.Subdomains.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var quad = new char[z]; for (var i = z; i > 0; i--) quad[z-i] = (char)('0' + ((x & (1 << (i-1))) != 0 ? 1 : 0) + ((y & (1 << (i-1))) != 0 ? 2 : 0));
        return layer.UrlTemplate.Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{x}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{-y}", ((1 << z)-1-y).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", (layer.Scheme == "tms" ? (1 << z)-1-y : y).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{quadkey}", new string(quad), StringComparison.Ordinal).Replace("{s}", subdomains.Length > 0 ? subdomains[(x+y)%subdomains.Length] : "0", StringComparison.Ordinal)
            .Replace("{key}", Uri.EscapeDataString(layer.ApiKey), StringComparison.Ordinal);
    }
    private async Task<string?> DownloadTile(MapLayerDto layer, int z, int x, int y, CancellationToken cancellation, bool replace)
    {
        if (layer.UrlTemplate.Length == 0) return null;
        var gate = _tileLocks.GetOrAdd(layer.Id + ":" + z + ":" + x + ":" + y, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellation);
        try
        {
            var existing = FindTile(layer.Id, z, x, y); if (!replace && existing is not null) return existing;
            using var response = await TileHttp.GetAsync(BuildTileUrl(layer,z,x,y), HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 10*1024*1024) throw new IOException("瓦片文件过大。");
            using var bytes = new MemoryStream();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            {
                var buffer = new byte[81920]; int read;
                while ((read = await input.ReadAsync(buffer,cancellation)) > 0) { if (bytes.Length + read > 10*1024*1024) throw new IOException("瓦片文件过大。"); bytes.Write(buffer,0,read); }
            }
            var data = bytes.ToArray();
            var extension = data.Length >= 8 && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71 ? ".png"
                : data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255 ? ".jpg"
                : data.Length >= 12 && System.Text.Encoding.ASCII.GetString(data,0,4) == "RIFF" && System.Text.Encoding.ASCII.GetString(data,8,4) == "WEBP" ? ".webp" : throw new IOException("服务未返回 PNG、JPEG 或 WebP 图片。");
            var row = layer.Scheme == "tms" ? (1 << z)-1-y : y;
            var basePath = Path.Combine(RootDir,"tiles",layer.Folder,z.ToString(CultureInfo.InvariantCulture),x.ToString(CultureInfo.InvariantCulture),row.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            var temp = basePath + "." + NewId() + ".tmp";
            try { await File.WriteAllBytesAsync(temp,data,cancellation); File.Move(temp,basePath+extension,true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            foreach (var ext in new[]{".png",".jpg",".webp"}.Where(ext => ext != extension)) if (File.Exists(basePath+ext)) File.Delete(basePath+ext);
            return basePath+extension;
        }
        finally { gate.Release(); }
    }
    public async Task<string?> ResolveTile(string id,int z,int x,int y,CancellationToken cancellation)
    {
        var layer = GetMapLayers().FirstOrDefault(l => l.Id == id) ?? throw new ApiException(404,"图层不存在。");
        if (z < layer.MinZoom || z > layer.MaxZoom || x<0 || y<0 || x>=1<<z || y>=1<<z) return null;
        var existing = FindTile(id,z,x,y);
        if (layer.SourceMode == "offline" || layer.SourceMode == "hybrid" && existing is not null) return existing;
        try { return await DownloadTile(layer,z,x,y,cancellation,layer.SourceMode == "online"); }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { return existing; }
    }
    public TileUpdateJob StartTileUpdate(TileUpdateRequest request)
    {
        var layer = GetMapLayers().FirstOrDefault(l => l.Id == request.LayerId) ?? throw new ApiException(404,"图层不存在。");
        if (layer.UrlTemplate.Length == 0) throw new ApiException(400,"请先设置图层链接和密钥。");
        if (new[]{request.West,request.South,request.East,request.North}.Any(x=>!double.IsFinite(x)) || request.West < -180 || request.East > 180 || request.South < -85 || request.North > 85 || request.West >= request.East || request.South >= request.North || request.MinZoom < layer.MinZoom || request.MaxZoom > layer.MaxZoom || request.MinZoom > request.MaxZoom) throw new ApiException(400,"缓存范围或级别不正确。");
        var tiles = new List<(int Z,int X,int Y)>();
        static int X(double lon,int z) => Math.Clamp((int)Math.Floor((lon+180)/360*(1<<z)),0,(1<<z)-1);
        static int Y(double lat,int z) { var r=lat*Math.PI/180;return Math.Clamp((int)Math.Floor((1-Math.Asinh(Math.Tan(r))/Math.PI)/2*(1<<z)),0,(1<<z)-1); }
        for(var z=request.MinZoom;z<=request.MaxZoom;z++)
            for(var x=X(request.West,z);x<=X(request.East,z);x++)
                for(var y=Y(request.North,z);y<=Y(request.South,z);y++) { tiles.Add((z,x,y)); if(tiles.Count>10000) throw new ApiException(400,"本次超过 10000 张，请缩小范围或减少级别。"); }
        var job = new TileJob(new(NewId(),layer.Id,"running",tiles.Count,0,0,0,""));
        lock(_gate) { if(_tileJobs.Values.Any(j=>j.Value.State=="running")) throw new ApiException(409,"已有缓存更新进行中。"); _tileJobs[job.Value.Id]=job; }
        _ = Task.Run(async () =>
        {
            try
            {
                foreach(var (z,x,y) in tiles)
                {
                    job.Stop.Token.ThrowIfCancellationRequested();
                    try { await DownloadTile(layer,z,x,y,job.Stop.Token,request.Replace); job.Value=job.Value with { Downloaded=job.Value.Downloaded+1 }; }
                    catch(Exception e) when(e is HttpRequestException or IOException or TaskCanceledException) { job.Stop.Token.ThrowIfCancellationRequested(); job.Value=job.Value with {Failed=job.Value.Failed+1,Error="部分瓦片下载失败，请检查链接、密钥或网络后重试。"}; }
                    job.Value=job.Value with { Processed=job.Value.Processed+1 };
                }
                job.Value=job.Value with { State=job.Value.Failed==0?"complete":"partial" };
            }
            catch(OperationCanceledException) { job.Value=job.Value with {State="cancelled"}; }
            catch(Exception) { job.Value=job.Value with {State="failed",Error="缓存更新失败，请检查磁盘空间及图层配置。"}; }
            finally
            {
                lock(_gate) if(!_disposed) { using var update=Command("UPDATE map_layers SET cache_version=cache_version+1 WHERE id=$id;",null,("$id",layer.Id));update.ExecuteNonQuery(); }
            }
        });
        return job.Value;
    }
    public void MapTileUpdateApi(WebApplication app)
    {
        RequestDelegate start=async c=>await Api.Write(c,StartTileUpdate(await Api.Read(c,TileUpdateJsonContext.Default.TileUpdateRequest)),TileUpdateJsonContext.Default.TileUpdateJob);
        RequestDelegate status=c=>Api.Write(c,_tileJobs.TryGetValue(Api.Id(c),out var job)?job.Value:throw new ApiException(404,"缓存任务不存在，程序可能已重启。"),TileUpdateJsonContext.Default.TileUpdateJob);
        RequestDelegate stop=c=>{if(_tileJobs.TryGetValue(Api.Id(c),out var job))job.Stop.Cancel();return Api.Write(c,new OkDto(),AppJsonContext.Default.OkDto);};
        app.MapPost("/api/map/cache-update",start);app.MapGet("/api/map/cache-update/{id}",status);app.MapPost("/api/map/cache-update/{id}/cancel",stop);
    }
}
