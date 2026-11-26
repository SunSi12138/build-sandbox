using System.Buffers.Binary;
using System.IO.Compression;

namespace StreetEcoTool;

public sealed record LocalArchiveTile(byte[] Data, string ContentType, string? ContentEncoding);
public sealed record MapArchiveInfo(string? TileType, int MinZoom, int MaxZoom, string Version, string? Error = null);

/// <summary>Reads local PMTiles v3 archives without a network provider or unpacking tiles.</summary>
public static class PmTiles
{
    private readonly record struct Entry(ulong Id, ulong Offset, ulong Length, ulong Run);
    private readonly record struct DirectoryKey(string File, long Size, long Modified, ulong Offset, ulong Length, int Compression);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<DirectoryKey, Entry[]> DirectoryCache = new();
    private static readonly Queue<DirectoryKey> CacheOrder = new();
    private const int CacheEntryLimit = 250_000;
    private static int _cachedEntries;

    private static bool IsArchive(string path) => path.EndsWith(".pmtiles", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pbtiles", StringComparison.OrdinalIgnoreCase);
    internal static bool IsArchivePath(string path) => IsArchive(path);

    private static byte[] Header(FileStream stream)
    {
        if (stream.Length < 127) throw new ApiException(400, "PMTiles 文件头不完整。");
        var header = new byte[127];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 7).SequenceEqual("PMTiles"u8) || header[7] != 3)
            throw new ApiException(400, "仅支持 PMTiles v3 文件。");
        if (header[100] > header[101] || header[100] > 22)
            throw new ApiException(400, "PMTiles 缩放范围不受支持。");
        if (header[97] is < 1 or > 3 || header[98] is < 1 or > 3)
            throw new ApiException(400, "PMTiles 压缩格式不受支持，请使用无压缩、Gzip 或 Brotli。");
        if (header[99] is < 1 or > 5)
            throw new ApiException(400, "PMTiles 图块类型不受支持（需要 MVT 或图片）。");
        var offset = U64(header, 8);
        var length = U64(header, 16);
        if (offset < 127 || offset > (ulong)stream.Length || length == 0 || length > (ulong)stream.Length - offset)
            throw new ApiException(400, "PMTiles 文件根索引不完整。");
        return header;
    }

    public static MapArchiveInfo Inspect(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(null, 0, 0, "", "未找到 PMTiles 文件。");
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var header = Header(stream);
            return new(header[99] == 1 ? "mvt" : "raster", header[100], Math.Min((int)header[101], 22),
                $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}");
        }
        catch (ApiException error) { return new(null, 0, 0, "", error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(null, 0, 0, "", "无法读取 PMTiles 文件，请检查路径和访问权限。"); }
    }

    private static Entry[] ReadDirectory(FileStream stream, FileInfo file, ulong offset, ulong length, int compression)
    {
        var key = new DirectoryKey(file.FullName, stream.Length, file.LastWriteTimeUtc.Ticks, offset, length, compression);
        lock (CacheGate)
            if (DirectoryCache.TryGetValue(key, out var cached)) return cached;
        var entries = Directory(ReadAt(stream, offset, length), compression);
        if (entries.Length > CacheEntryLimit) return entries;
        lock (CacheGate)
        {
            if (DirectoryCache.TryGetValue(key, out var existing)) return existing;
            while (CacheOrder.Count > 0 && (CacheOrder.Count >= 64 || _cachedEntries + entries.Length > CacheEntryLimit))
            {
                var removed = CacheOrder.Dequeue();
                _cachedEntries -= DirectoryCache[removed].Length;
                DirectoryCache.Remove(removed);
            }
            DirectoryCache.Add(key, entries);
            CacheOrder.Enqueue(key);
            _cachedEntries += entries.Length;
        }
        return entries;
    }

    private static int FindEntry(Entry[] entries, ulong id)
    {
        var lo = 0; var hi = entries.Length - 1;
        while (lo <= hi)
        {
            var middle = lo + (hi - lo) / 2;
            if (entries[middle].Id <= id) lo = middle + 1;
            else hi = middle - 1;
        }
        return hi;
    }
    private static ulong U64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

    private static byte[] ReadAt(FileStream stream, ulong offset, ulong length)
    {
        if (length == 0 || length > 24 * 1024 * 1024 || offset > (ulong)stream.Length ||
            length > (ulong)stream.Length - offset)
            throw new ApiException(400, "PMTiles 文件索引或瓦片大小不正确。");
        var data = new byte[(int)length];
        stream.Position = checked((long)offset);
        stream.ReadExactly(data);
        return data;
    }

    private static byte[] Decompress(byte[] data, int compression)
    {
        if (compression == 1) return data;
        using var input = new MemoryStream(data);
        using Stream decoder = compression switch
        {
            2 => new GZipStream(input, CompressionMode.Decompress),
            3 => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new ApiException(400, "暂不支持此 PMTiles 索引压缩格式。"),
        };
        using var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = decoder.Read(buffer)) > 0)
        {
            if (result.Length + count > 32 * 1024 * 1024) throw new ApiException(400, "PMTiles 解压内容过大。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static ulong VarInt(byte[] bytes, ref int index)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (index >= bytes.Length) throw new ApiException(400, "PMTiles 索引不完整。");
            var b = bytes[index++];
            if (shift == 63 && (b & 0x7f) > 1) throw new ApiException(400, "PMTiles 索引溢出。");
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new ApiException(400, "PMTiles 索引溢出。");
    }

    private static Entry[] Directory(byte[] compressed, int compression)
    {
        var bytes = Decompress(compressed, compression);
        var position = 0;
        var count = VarInt(bytes, ref position);
        if (count > 2_000_000 || count > (ulong)(bytes.Length - position) / 4)
            throw new ApiException(400, "PMTiles 索引条数不正确。");
        var length = (int)count;
        var items = new Entry[length];
        ulong previous = 0;
        for (var i = 0; i < length; i++)
        {
            var delta = VarInt(bytes, ref position);
            if (delta > ulong.MaxValue - previous || (i > 0 && delta == 0)) throw new ApiException(400, "PMTiles 索引编号不正确。");
            previous += delta;
            items[i] = new Entry(previous, 0, 0, 0);
        }
        for (var i = 0; i < length; i++) items[i] = items[i] with { Run = VarInt(bytes, ref position) };
        for (var i = 0; i < length; i++) items[i] = items[i] with { Length = VarInt(bytes, ref position) };
        ulong end = 0;
        for (var i = 0; i < length; i++)
        {
            var encoded = VarInt(bytes, ref position);
            var offset = encoded == 0 && i > 0 ? end : encoded == 0 ? throw new ApiException(400, "PMTiles 索引偏移不正确。") : encoded - 1;
            items[i] = items[i] with { Offset = offset };
            end = checked(offset + items[i].Length);
        }
        return items;
    }

    private static ulong TileId(int z, int x, int y)
    {
        var n = 1 << z;
        ulong distance = 0;
        for (var step = n / 2; step > 0; step /= 2)
        {
            var rx = (x & step) != 0 ? 1 : 0;
            var ry = (y & step) != 0 ? 1 : 0;
            distance += (ulong)step * (ulong)step * (uint)((3 * rx) ^ ry);
            if (ry != 0) continue;
            if (rx == 1) { x = step - 1 - x; y = step - 1 - y; }
            (x, y) = (y, x);
        }
        return (((1UL << (2 * z)) - 1) / 3) + distance;
    }

    public static LocalArchiveTile? Read(string path, int z, int x, int y)
    {
        if (z < 0 || z > 22 || x < 0 || y < 0 || x >= (1 << z) || y >= (1 << z)) return null;
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var header = Header(stream);
        if (z < header[100] || z > header[101]) return null;
        var rootOffset = U64(header, 8);
        var rootLength = U64(header, 16);
        var leafOffset = U64(header, 40);
        var dataOffset = U64(header, 56);
        var compression = header[97];
        var tileCompression = header[98];
        var tileType = header[99];
        var id = TileId(z, x, y);
        var entries = ReadDirectory(stream, file, rootOffset, rootLength, compression);
        for (var depth = 0; depth < 4; depth++)
        {
            var index = FindEntry(entries, id);
            if (index < 0) return null;
            var match = entries[index];
            if (match.Run != 0)
            {
                if (id - match.Id >= match.Run) return null;
                var contentType = tileType switch
                {
                    1 => "application/vnd.mapbox-vector-tile",
                    2 => "image/png",
                    3 => "image/jpeg",
                    4 => "image/webp",
                    5 => "image/avif",
                    _ => "application/octet-stream",
                };
                var data = ReadAt(stream, checked(dataOffset + match.Offset), match.Length);
                // Decode locally so older/insecure LAN browsers do not depend on
                // negotiating archive-specific Content-Encoding (notably Brotli).
                return new(Decompress(data, tileCompression), contentType, null);
            }
            entries = ReadDirectory(stream, file, checked(leafOffset + match.Offset), match.Length, compression);
        }
        throw new ApiException(400, "PMTiles 索引嵌套层数过多。");
    }
}
