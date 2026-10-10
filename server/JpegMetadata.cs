using System.Buffers.Binary;

namespace StreetEcoTool;

public readonly record struct JpegDimensions(int Width, int Height);

/// <summary>
/// Reads JPEG SOF dimensions without an image decoder. Validates the header
/// structure and scan boundary, but does not decode entropy-coded pixel data.
/// </summary>
public static class JpegMetadata
{
    public static JpegDimensions Read(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8)
            throw new InvalidDataException("照片必须是 JPEG 格式，请重新选择照片。");
        if (jpeg[^2] != 0xff || jpeg[^1] != 0xd9)
            throw new InvalidDataException("JPEG 照片未完整上传，请重新上传。");

        var position = 2;
        JpegDimensions? dimensions = null;
        var frameComponents = 0;
        while (position < jpeg.Length - 2)
        {
            if (jpeg[position++] != 0xff) throw InvalidHeader();
            // JPEG permits multiple FF fill bytes between marker segments.
            while (position < jpeg.Length && jpeg[position] == 0xff) position++;
            if (position >= jpeg.Length) throw InvalidHeader();
            var marker = jpeg[position++];
            if (marker == 0x01) continue; // TEM is the only standalone pre-scan marker.
            if (marker < 0xc0 || marker is >= 0xd0 and <= 0xd9) throw InvalidHeader();
            if (position + 2 > jpeg.Length - 2) throw InvalidHeader();

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(position, 2));
            if (length < 2 || length > jpeg.Length - 2 - position) throw InvalidHeader();
            var segment = jpeg.Slice(position, length);

            // SOF0..SOF15, excluding DHT, JPG and DAC (which are not SOF markers).
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (dimensions is not null || length < 8) throw InvalidHeader();
                var precision = segment[2];
                var height = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(5, 2));
                frameComponents = segment[7];
                if (precision is < 1 or > 16 || width == 0 || height == 0 || frameComponents == 0 || length != 8 + frameComponents * 3)
                    throw InvalidHeader();
                dimensions = new JpegDimensions(width, height);
            }
            else if (marker == 0xda)
            {
                if (dimensions is null || length < 6) throw InvalidHeader();
                var scanComponents = segment[2];
                if (scanComponents == 0 || scanComponents > frameComponents || length != 6 + scanComponents * 2)
                    throw InvalidHeader();
                if (position + length >= jpeg.Length - 2) throw InvalidHeader();
                // APP/COM payloads were skipped by length, so embedded thumbnails
                // cannot supply false dimensions. Stop before entropy-coded data.
                return dimensions.Value;
            }
            position += length;
        }
        throw InvalidHeader();
    }

    private static InvalidDataException InvalidHeader() => new("JPEG 照片文件头不完整或已损坏，请重新上传。");
}
