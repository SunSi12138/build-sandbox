using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class PmTilesTests
{
    // A valid MVT point in a named layer, not merely a magic/header fixture.
    private static byte[] VectorTile(byte coordinate) =>
        [0x1a, 21, 0x0a, 5, (byte)'w', (byte)'a', (byte)'t', (byte)'e', (byte)'r',
         0x12, 7, 0x18, 1, 0x22, 3, 9, coordinate, coordinate,
         0x28, 0x80, 0x20, 0x78, 2];

    private static byte[] Compress(byte[] bytes, int compression)
    {
        if (compression == 1) return bytes;
        using var output = new MemoryStream();
        using (Stream encoder = compression == 2
            ? new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)
            : new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            encoder.Write(bytes);
        return output.ToArray();
    }

    private static byte[] VarInt(ulong value)
    {
        var result = new List<byte>();
        do
        {
            var next = (byte)(value & 127);
            value >>= 7;
            result.Add((byte)(next | (value == 0 ? 0 : 128)));
        } while (value != 0);
        return result.ToArray();
    }

    private static byte[] Directory(ulong tileId, ulong run, int length, int compression) =>
        Compress([.. VarInt(1), .. VarInt(tileId), .. VarInt(run), .. VarInt((ulong)length), .. VarInt(1)], compression);

    private static void WriteArchive(string path, byte[] tile, int compression, bool leaf = true, int tileType = 1)
    {
        // z1/x0/y1 = Hilbert tile ID 2. Include a leaf pointer in the root
        // to verify the bounded index cache works for both directory levels.
        var data = Compress(tile, compression);
        var leafDirectory = leaf ? Directory(2, 1, data.Length, compression) : [];
        var root = Directory(2, leaf ? 0UL : 1UL, leaf ? leafDirectory.Length : data.Length, compression);
        var metadata = Compress("{}"u8.ToArray(), compression);
        var header = new byte[127];
        "PMTiles"u8.CopyTo(header);
        header[7] = 3;
        void Write(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(offset, 8), value);
        var metadataOffset = 127 + root.Length;
        var leafOffset = metadataOffset + metadata.Length;
        var tileOffset = leafOffset + leafDirectory.Length;
        Write(8, 127); Write(16, (ulong)root.Length);
        Write(24, (ulong)metadataOffset); Write(32, (ulong)metadata.Length);
        Write(40, (ulong)leafOffset); Write(48, (ulong)leafDirectory.Length);
        Write(56, (ulong)tileOffset); Write(64, (ulong)data.Length);
        Write(72, 1); Write(80, 1); Write(88, 1);
        header[96] = 1;
        header[97] = (byte)compression;
        header[98] = (byte)compression;
        header[99] = (byte)tileType;
        header[100] = 1; header[101] = 1;
        File.WriteAllBytes(path, [.. header, .. root, .. metadata, .. leafDirectory, .. data]);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void ReadsActualMvtAndHonorsArchiveCoordinatesAndCompression(int compression, bool leaf)
    {
        using var env = new V2Environment();
        using var store = env.Open();
        var archive = Path.Combine(env.Root, "sample.pmtiles");
        var tile = VectorTile(2);
        WriteArchive(archive, tile, compression, leaf);
        var original = store.GetMapLayers()[0];
        // These stored values deliberately disagree with the archive.
        var saved = store.SaveMapLayer(original with { Id = "test-pmtiles", Folder = archive, TileType = "raster", Scheme = "tms", MinZoom = 16, MaxZoom = 20, IsDefault = false });
        var described = store.GetDisplayMapLayers().Single(layer => layer.Id == saved.Id);
        Assert.NotNull(described.Archive);
        Assert.Null(described.Archive!.Error);
        Assert.Equal("mvt", described.Archive.TileType);
        Assert.Equal(1, described.Archive.MinZoom);
        Assert.Equal(1, described.Archive.MaxZoom);
        Assert.Equal("raster", described.TileType);
        Assert.Equal(16, described.MinZoom);
        Assert.Equal("raster", store.GetMapLayers().Single(layer => layer.Id == saved.Id).TileType);
        Assert.Equal(described.Archive, store.GetMapState().Layers.Single(layer => layer.Id == saved.Id).Archive);
        var actual = store.ReadArchiveTile(saved.Id, 1, 0, 1);
        Assert.NotNull(actual);
        Assert.Equal(tile, actual!.Data);
        Assert.Equal("application/vnd.mapbox-vector-tile", actual.ContentType);
        Assert.Null(actual.ContentEncoding);
        Assert.Null(store.ReadArchiveTile(saved.Id, 1, 0, 0));
        Assert.Null(store.ReadArchiveTile(saved.Id, 2, 0, 1));
        Assert.Null(store.FindTile(saved.Id, 1, 0, 1));
        // Repeat and read in parallel to exercise shared index caching without
        // sharing a seekable stream across requests.
        Parallel.For(0, 12, _ => Assert.Equal(tile, PmTiles.Read(archive, 1, 0, 1)!.Data));
        V2Environment.AssertForeignKeys(store.DatabasePath);
    }

    [Fact]
    public void ReplacingArchiveInvalidatesIndexAndMetadataCache()
    {
        using var env = new V2Environment(); Directory.CreateDirectory(env.Root);
        var path = Path.Combine(env.Root, "replace.pmtiles");
        WriteArchive(path, VectorTile(2), 1, leaf: false);
        var first = PmTiles.Inspect(path);
        Assert.Equal(VectorTile(2), PmTiles.Read(path, 1, 0, 1)!.Data);
        WriteArchive(path, VectorTile(4), 2, leaf: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        Assert.NotEqual(first.Version, PmTiles.Inspect(path).Version);
        Assert.Equal(VectorTile(4), PmTiles.Read(path, 1, 0, 1)!.Data);
    }

    [Fact]
    public void BrokenArchiveIsReportedWithoutPreventingOtherLayers()
    {
        using var env = new V2Environment(); using var store = env.Open();
        var original = store.GetMapLayers()[0];
        var path = Path.Combine(env.Root, "broken.pmtiles");
        store.SaveMapLayer(original with { Id = "broken", Folder = path, IsDefault = false });
        Assert.NotNull(store.GetDisplayMapLayers().Single(layer => layer.Id == "broken").Archive!.Error);
        File.WriteAllBytes(path, [1, 2, 3]);
        var layers = store.GetDisplayMapLayers();
        Assert.NotNull(layers.Single(layer => layer.Id == "broken").Archive!.Error);
        Assert.Contains(layers, layer => layer.Id == original.Id && layer.Archive is null);
        Assert.Throws<ApiException>(() => PmTiles.Read(path, 1, 0, 1));
    }
}
