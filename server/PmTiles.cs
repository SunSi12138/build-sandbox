using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace StreetEcoTool;

public sealed record LocalArchiveTile(byte[] Data, string ContentType, string? ContentEncoding);

/// <summary>Reads local PMTiles v3 archives without a network provider or unpacking tiles.</summary>
public static class PmTiles
{
    private sealed record Entry(ulong Id, ulong Offset, ulong Length, ulong Run);
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
        decoder.CopyTo(result);
        if (result.Length > 32 * 1024 * 1024) throw new ApiException(400, "PMTiles 索引过大。");
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
        var length = checked((int)VarInt(bytes, ref position));
        if (length < 1 || length > 2_000_000) throw new ApiException(400, "PMTiles 索引条数不正确。");
        var items = new Entry[length];
        ulong previous = 0;
        for (var i = 0; i < length; i++)
        {
            previous += VarInt(bytes, ref position);
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
        if (!File.Exists(path)) return null;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < 127) throw new ApiException(400, "PMTiles 文件头不完整。");
        var header = new byte[127];
        stream.ReadExactly(header);
        if (Encoding.ASCII.GetString(header, 0, 7) != "PMTiles" || header[7] != 3)
            throw new ApiException(400, "仅支持 PMTiles v3 文件。");
        if (z < header[100] || z > header[101]) return null;
        var rootOffset = U64(header, 8);
        var rootLength = U64(header, 16);
        var leafOffset = U64(header, 40);
        var dataOffset = U64(header, 56);
        var compression = header[97];
        var tileCompression = header[98];
        var tileType = header[99];
        var id = TileId(z, x, y);
        var entries = Directory(ReadAt(stream, rootOffset, rootLength), compression);
        for (var depth = 0; depth < 4; depth++)
        {
            var match = entries.LastOrDefault(entry => entry.Id <= id);
            if (match is null) return null;
            if (match.Run != 0)
            {
                if (id >= match.Id + match.Run) return null;
                var contentType = tileType switch
                {
                    1 or 6 => "application/vnd.mapbox-vector-tile",
                    2 => "image/png",
                    3 => "image/jpeg",
                    4 => "image/webp",
                    5 => "image/avif",
                    _ => "application/octet-stream",
                };
                var encoding = tileCompression switch { 1 => null, 2 => "gzip", 3 => "br", _ => throw new ApiException(400, "暂不支持此 PMTiles 瓦片压缩格式。") };
                return new(ReadAt(stream, checked(dataOffset + match.Offset), match.Length), contentType, encoding);
            }
            entries = Directory(ReadAt(stream, checked(leafOffset + match.Offset), match.Length), compression);
        }
        throw new ApiException(400, "PMTiles 索引嵌套层数过多。");
    }
}
