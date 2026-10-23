using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

public sealed record CachePreviewRequest(string Source);
public sealed record CachePreviewDto(string Token, int Files, long Bytes, int MinZoom, int MaxZoom);
public sealed record CacheStartRequest(string Token, string Name, string Scheme);
public sealed record CacheJobDto(string Id, string State, int Copied, int Total, long BytesCopied, long TotalBytes, string Error, string? LayerId);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CachePreviewRequest))]
[JsonSerializable(typeof(CachePreviewDto))]
[JsonSerializable(typeof(CacheStartRequest))]
[JsonSerializable(typeof(CacheJobDto))]
public partial class CacheJsonContext : JsonSerializerContext;

public sealed partial class Store
{
    private sealed record CacheFile(string Source, string Relative, long Size, long Modified);
    private sealed record CacheScan(CachePreviewDto Preview, CacheFile[] Files, DateTimeOffset Expires);
    private sealed class CacheJob(CacheJobDto value)
    {
        public CacheJobDto Value = value;
        public readonly CancellationTokenSource Cancellation = new();
    }
    private readonly ConcurrentDictionary<string, CacheScan> _cacheScans = new();
    private readonly ConcurrentDictionary<string, CacheJob> _cacheJobs = new();

    public CachePreviewDto PreviewCacheImport(string source)
    {
        source = Path.GetFullPath(Text(source, "缓存文件夹", 2000, true));
        if (!Directory.Exists(source)) throw new ApiException(400, "缓存文件夹不存在，请选择包含地图级别子目录的文件夹。");
        var files = new List<CacheFile>(); var levels = new List<int>();
        foreach (var zDirectory in new DirectoryInfo(source).EnumerateDirectories())
        {
            if (zDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint) || !int.TryParse(zDirectory.Name, out var z) || z is < 0 or > 22) continue;
            foreach (var xDirectory in zDirectory.EnumerateDirectories())
            {
                if (xDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint) || !int.TryParse(xDirectory.Name, out var x) || x < 0 || x >= 1 << z) continue;
                foreach (var file in xDirectory.EnumerateFiles())
                {
                    var extension = file.Extension.ToLowerInvariant();
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || extension is not (".png" or ".jpg" or ".jpeg" or ".webp") || !int.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var y) || y < 0 || y >= 1 << z) continue;
                    if (file.Length is < 1 or > 20 * 1024 * 1024) throw new ApiException(400, "发现空白或异常大的地图图片，请先检查缓存文件夹。");
                    files.Add(new(file.FullName, Path.Combine(z.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture) + (extension == ".jpeg" ? ".jpg" : extension)), file.Length, file.LastWriteTimeUtc.Ticks)); levels.Add(z);
                    if (files.Count > 1_000_000) throw new ApiException(400, "单次缓存超过100万张，请分批导入。");
                }
            }
        }
        if (files.Count == 0) throw new ApiException(400, "没有找到地图缓存。文件夹中应为“级别 / 列号 / 行号.png”结构，例如选择原Map的 wwwroot/jlyh。");
        if (files.Select(x => x.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new ApiException(400, "同一地图位置存在重复图片，请先整理文件夹。");
        foreach (var old in _cacheScans.Where(x => x.Value.Expires < DateTimeOffset.Now).ToArray()) _cacheScans.TryRemove(old.Key, out _);
        var preview = new CachePreviewDto(NewId(), files.Count, files.Sum(x => x.Size), levels.Min(), levels.Max());
        _cacheScans[preview.Token] = new(preview, [.. files], DateTimeOffset.Now.AddMinutes(30));
        return preview;
    }

    public CacheJobDto StartCacheImport(CacheStartRequest request)
    {
        var name = Text(request.Name, "图层名称", 100, true);
        if (request.Scheme is not ("xyz" or "tms")) throw new ApiException(400, "请选择瓦片编号方式。");
        CacheScan scan; CacheJob job;
        lock (_gate)
        {
            if (_cacheJobs.TryGetValue(request.Token, out var previous)) return previous.Value;
            if (!_cacheScans.TryGetValue(request.Token, out scan!) || scan.Expires < DateTimeOffset.Now) throw new ApiException(409, "预览已过期，请重新读取文件夹。");
            if (_cacheJobs.Any(x => x.Value.Value.State == "copying")) throw new ApiException(409, "已有地图缓存正在导入，请稍后再试。");
            job = new CacheJob(new(request.Token, "copying", 0, scan.Preview.Files, 0, scan.Preview.Bytes, "", null));
            _cacheJobs[request.Token] = job;
        }
        _ = Task.Run(async () =>
        {
            var layerId = "cache-" + request.Token;
            var tileRoot = Path.Combine(RootDir, "tiles");
            var staging = Path.Combine(tileRoot, ".import-" + request.Token);
            var destination = Path.Combine(tileRoot, layerId);
            var published = false;
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var file in scan.Files)
                {
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    var current = new FileInfo(file.Source);
                    if (!current.Exists || current.Length != file.Size || current.LastWriteTimeUtc.Ticks != file.Modified) throw new IOException("源缓存有变化，请重新预览再导入。");
                    var target = Path.Combine(staging, file.Relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output, job.Cancellation.Token);
                    job.Value = job.Value with { Copied = job.Value.Copied + 1, BytesCopied = job.Value.BytesCopied + file.Size };
                }
                job.Cancellation.Token.ThrowIfCancellationRequested();
                Directory.Move(staging, destination);
                SaveMapLayer(new(layerId, name, layerId, request.Scheme, true, false, 1, scan.Preview.MinZoom, scan.Preview.MaxZoom, GetMapLayers().Length));
                published = true; job.Value = job.Value with { State = "complete", LayerId = layerId };
            }
            catch (OperationCanceledException) { job.Value = job.Value with { State = "cancelled" }; }
            catch (Exception e) { job.Value = job.Value with { State = "failed", Error = e is IOException or UnauthorizedAccessException ? "缓存复制失败，请检查源文件、磁盘空间和权限。" : "图层保存失败，请重试。" }; }
            finally
            {
                // Both paths are generated direct children of the application tile folder.
                if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                if (!published && Directory.Exists(destination)) try { Directory.Delete(destination, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                _cacheScans.TryRemove(request.Token, out _);
            }
        });
        return job.Value;
    }
    public CacheJobDto CacheImportStatus(string id) => _cacheJobs.TryGetValue(id, out var job) ? job.Value : throw new ApiException(404, "导入任务不存在，程序可能已重启，请重新读取缓存。");
    public void CancelCacheImport(string id) { if (_cacheJobs.TryGetValue(id, out var job)) job.Cancellation.Cancel(); }

    public void MapCacheImportApi(WebApplication app)
    {
        static void Local(HttpContext c) { if (!ListeningService.IsLoopback(c.Connection.RemoteIpAddress)) throw new ApiException(403, "导入电脑文件夹请在主机浏览器中操作。"); }
        RequestDelegate preview = async c => { Local(c); var request = await Api.Read(c, CacheJsonContext.Default.CachePreviewRequest); var result = await Task.Run(() => PreviewCacheImport(request.Source), c.RequestAborted); await Api.Write(c, result, CacheJsonContext.Default.CachePreviewDto); };
        RequestDelegate start = async c => { Local(c); await Api.Write(c, StartCacheImport(await Api.Read(c, CacheJsonContext.Default.CacheStartRequest)), CacheJsonContext.Default.CacheJobDto); };
        RequestDelegate status = c => { Local(c); return Api.Write(c, CacheImportStatus(Api.Id(c)), CacheJsonContext.Default.CacheJobDto); };
        RequestDelegate cancel = c => { Local(c); CancelCacheImport(Api.Id(c)); return Api.Write(c, new OkDto(), AppJsonContext.Default.OkDto); };
        app.MapPost("/api/map/cache/preview", preview); app.MapPost("/api/map/cache/start", start); app.MapGet("/api/map/cache/{id}", status); app.MapPost("/api/map/cache/{id}/cancel", cancel);
    }
}
