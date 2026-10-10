using System.Buffers.Binary;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class JpegMetadataTests
{
    [Theory]
    [InlineData(0xc0)]
    [InlineData(0xc1)]
    [InlineData(0xc2)]
    public void ReadsBaselineExtendedAndProgressiveFrameDimensions(int marker)
    {
        var jpeg = Jpeg((byte)marker, 1600, 900);
        Assert.Equal(new JpegDimensions(1600, 900), JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void IgnoresSofMarkersEmbeddedInsideExifOrCommentPayloads()
    {
        var embeddedThumbnail = Jpeg(0xc0, 32, 16);
        var jpeg = Jpeg(0xc2, 1200, 1600, Segment(0xe1, embeddedThumbnail), Segment(0xfe, embeddedThumbnail));
        Assert.Equal(new JpegDimensions(1200, 1600), JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void DoesNotMisinterpretHuffmanOrArithmeticTableMarkersAsFrames()
    {
        var jpeg = Jpeg(0xc0, 320, 240, Segment(0xc4, [1, 2, 3]), Segment(0xcc, [4, 5]));
        Assert.Equal(new JpegDimensions(320, 240), JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void AcceptsFillBytesBetweenMarkers()
    {
        var jpeg = Jpeg(0xc0, 640, 480);
        byte[] withFill = [.. jpeg.AsSpan(0, 2), 0xff, 0xff, .. jpeg.AsSpan(2)];
        Assert.Equal(new JpegDimensions(640, 480), JpegMetadata.Read(withFill));
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(1600, 0)]
    public void RejectsZeroDimensions(int width, int height)
    {
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(Jpeg(0xc0, width, height)));
    }

    [Fact]
    public void RejectsFrameComponentCountThatDisagreesWithSegmentLength()
    {
        var jpeg = Jpeg(0xc0, 1600, 900);
        jpeg[11] = 3; // SOF declares 3 components, but contains only 1 component tuple.
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void RejectsEveryTruncatedPrefixWithoutReadingPastTheBuffer()
    {
        var jpeg = Jpeg(0xc2, 1200, 1600, Segment(0xe0, [0x4a, 0x46, 0x49, 0x46, 0]));
        for (var length = 0; length < jpeg.Length; length++)
        {
            var truncated = jpeg.AsSpan(0, length).ToArray();
            Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(truncated));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    public void RejectsInvalidSegmentLengths(int length)
    {
        var jpeg = Jpeg(0xc0, 320, 240, Segment(0xe1, [1, 2, 3]));
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4, 2), (ushort)length);
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void RejectsSoiAndFrameWithoutAnImageScan()
    {
        var jpeg = Jpeg(0xc0, 320, 240);
        byte[] headerOnly = [.. jpeg.AsSpan(0, 15), 0xff, 0xd9];
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(headerOnly));
    }

    [Fact]
    public void RejectsScanBeforeAFrame()
    {
        var jpeg = Jpeg(0xc0, 320, 240);
        byte[] noFrame = [0xff, 0xd8, .. jpeg.AsSpan(15)];
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(noFrame));
    }

    [Fact]
    public void RejectsConflictingDuplicateFrames()
    {
        var first = Jpeg(0xc0, 320, 240);
        var second = Jpeg(0xc0, 640, 480);
        byte[] duplicate = [.. first.AsSpan(0, 15), .. second.AsSpan(2)];
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(duplicate));
    }

    [Fact]
    public void RejectsNonJpegFilesDespiteSofLikeBytes()
    {
        var jpeg = Jpeg(0xc0, 320, 240);
        jpeg[0] = 0x89;
        jpeg[1] = 0x50;
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(jpeg));
    }

    [Fact]
    public void RejectsByteStuffingBeforeTheFirstScan()
    {
        var jpeg = Jpeg(0xc0, 320, 240);
        byte[] badHeader = [0xff, 0xd8, 0xff, 0, .. jpeg.AsSpan(2)];
        Assert.Throws<InvalidDataException>(() => JpegMetadata.Read(badHeader));
    }

    // These fixtures exercise marker parsing, not entropy/pixel decoding.
    private static byte[] Jpeg(byte sof, int width, int height, params byte[][] leadingSegments)
    {
        byte[] frame = [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0];
        using var stream = new MemoryStream();
        stream.Write([0xff, 0xd8]);
        foreach (var segment in leadingSegments) stream.Write(segment);
        stream.Write(Segment(sof, frame));
        stream.Write(Segment(0xda, [1, 1, 0, 0, 63, 0]));
        stream.Write([0x11, 0xff, 0, 0x22, 0xff, 0xd9]);
        return stream.ToArray();
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = checked((ushort)(payload.Length + 2));
        return [0xff, marker, (byte)(length >> 8), (byte)length, .. payload];
    }
}
