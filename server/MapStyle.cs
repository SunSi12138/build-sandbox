using System.IO.Compression;
using System.Text.Json;

namespace StreetEcoTool;

public sealed partial class Store
{
    // Map templates belong to writable user data, never to published web assets.
    private const long MaximumStyleZipBytes = 32L * 1024 * 1024;
    private const long MaximumExpandedStyleBytes = 96L * 1024 * 1024;
    private string MapStyleDirectory(string id) => Path.Combine(RootDir, "map-styles", id);

    public async Task<TileProvider> ImportMapStyleAsync(string id, Stream upload, CancellationToken token)
    {
        TileProvider provider;
        lock (_gate) provider = GetMapProvider(id);
        if (provider.Format is not ("mvt" or "pmtiles"))
            throw new ApiException(400, "只有 PBF/MVT 和 PMTiles 图层可以使用矢量样式。");

        var root = Path.Combine(RootDir, "map-styles");
        Directory.CreateDirectory(root);
        var tempId = NewId();
        var zipPath = Path.Combine(root, "." + tempId + ".zip");
        var staging = Path.Combine(root, "." + tempId);
        var destination = MapStyleDirectory(id);
        var previous = Path.Combine(root, "." + tempId + ".old");
        try
        {
            await using (var zipOutput = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long bytes = 0;
                int count;
                while ((count = await upload.ReadAsync(buffer, token)) > 0)
                {
                    bytes += count;
                    if (bytes > MaximumStyleZipBytes) throw new ApiException(413, "样式压缩包不能超过 32 MB。");
                    await zipOutput.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }

            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                if (archive.Entries.Count > 1500) throw new ApiException(400, "样式文件数量过多。");
                long expanded = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("/", StringComparison.Ordinal)) continue;
                    if (name.StartsWith("/", StringComparison.Ordinal) ||
                        name.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':')) ||
                        !IsAllowedMapStyleAsset(name) ||
                        (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                        throw new ApiException(400, "样式压缩包含有非法路径或文件类型。");
                    expanded += entry.Length;
                    if (expanded > MaximumExpandedStyleBytes) throw new ApiException(413, "样式解压后不能超过 96 MB。");
                    var output = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    await using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await using var input = entry.Open();
                    await input.CopyToAsync(stream, token);
                }
            }
            var styleFile = Path.Combine(staging, "style.json");
            if (!File.Exists(styleFile)) throw new ApiException(400, "压缩包根目录必须包含 style.json。");
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(styleFile, token)))
            {
                var style = json.RootElement;
                if (style.ValueKind != JsonValueKind.Object ||
                    !style.TryGetProperty("version", out var version) || version.GetInt32() != 8 ||
                    !style.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object ||
                    !style.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
                    throw new ApiException(400, "style.json 必须是包含 sources 和 layers 的 Mapbox Style v8。");
            }

            // Swap the entire template only after validating all entries.
            if (Directory.Exists(destination)) Directory.Move(destination, previous);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (Directory.Exists(previous)) Directory.Move(previous, destination);
                throw;
            }
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            lock (_gate) { _mapGeneration++; return GetMapProvider(id); }
        }
        catch (InvalidDataException) { throw new ApiException(400, "样式 ZIP 文件损坏或格式不正确。"); }
        catch (JsonException) { throw new ApiException(400, "style.json 格式不正确。"); }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(staging);
        }
    }

    private static bool IsAllowedMapStyleAsset(string name)
    {
        if (name == "style.json") return true;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return name.Contains('/', StringComparison.Ordinal) &&
            extension is ".pbf" or ".png" or ".json" or ".webp";
    }

    public void DeleteMapStyle(string id)
    {
        lock (_gate)
        {
            _ = GetMapProvider(id);
            var directory = MapStyleDirectory(id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            _mapGeneration++;
        }
    }

    private async Task ServeMapStyleAssetAsync(HttpContext context)
    {
        string id = Api.Id(context);
        var relative = (string?)context.Request.RouteValues["asset"] ?? "";
        lock (_gate) _ = GetMapProvider(id);
        if (relative.StartsWith("/", StringComparison.Ordinal) ||
            relative.Contains('\\', StringComparison.Ordinal) ||
            relative.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':')) ||
            !IsAllowedMapStyleAsset(relative))
        {
            context.Response.StatusCode = 404;
            return;
        }
        var directory = MapStyleDirectory(id);
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/x-protobuf"
        };
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }
}
