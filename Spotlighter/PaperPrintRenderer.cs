using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace Spotlighter;

/// <summary>
/// Two-stage renderer: static printed-paper reflectance is calculated only
/// when the captured desktop changes; mouse movements only relight this cache.
/// AVX2 multiplies eight premultiplied BGRA pixels per batch. Falls back to a
/// bit-identical scalar path on pre-AVX2 hardware.
/// </summary>
internal static unsafe class PaperPrintRenderer
{
    private static readonly Vector256<int> Ramp = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector256<uint> Mask = Vector256.Create(255u);
    private static readonly Vector256<uint> Opaque = Vector256.Create(0xff000000u);
    private static readonly Vector512<int> Ramp512 = Vector512.Create(
        Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7),
        Vector256.Create(8, 9, 10, 11, 12, 13, 14, 15));
    private static readonly Vector512<uint> Mask512 = Vector512.Create(255u);
    private static readonly Vector512<uint> Opaque512 = Vector512.Create(0xff000000u);
    private static readonly int WorkerCount = Math.Clamp(Environment.ProcessorCount, 1, 8);

    internal static bool HasAvx2 => Avx2.IsSupported;
    internal static bool HasAvx512 => Avx512F.IsSupported;

    internal static void PrepareMaterial(uint* source, uint* material, byte[] paper,
        int width, int height)
    {
        nint inputPointer = (nint)source;
        nint outputPointer = (nint)material;
        Parallel.For(0, WorkerCount, worker =>
        {
            uint* input = (uint*)inputPointer;
            uint* output = (uint*)outputPointer;
            int firstY = worker * height / WorkerCount;
            int lastY = (worker + 1) * height / WorkerCount;
            for (int y = firstY; y < lastY; y++)
            {
                int index = y * width;
                int end = index + width;
                for (; index < end; index++)
                {
                    uint color = input[index];
                    int red = (int)((color >> 16) & 255u);
                    int green = (int)((color >> 8) & 255u);
                    int blue = (int)(color & 255u);
                    int luminance = (red * 77 + green * 150 + blue * 29) >> 8;
                    int pigment = 255 - luminance;
                    int tooth = paper[index] - 128;

                    // Matte, almost-neutral cellulose. Shadows are ink pigment,
                    // not a uniformly brown translucent filter. Preserve a little
                    // chromatic information for charts, icons and syntax colours.
                    int fibre = (tooth * luminance >> 8) / 3;
                    int pr = 239 + fibre - (pigment * 206 >> 8)
                        + ((red - luminance) * 48 >> 7);
                    int pg = 237 + fibre - (pigment * 207 >> 8)
                        + ((green - luminance) * 46 >> 7);
                    int pb = 233 + fibre - (pigment * 202 >> 8)
                        + ((blue - luminance) * 44 >> 7);
                    output[index] = 0xff000000u |
                        (uint)(ClampByte(pr) << 16 | ClampByte(pg) << 8 | ClampByte(pb));
                }
            }
        });
    }

    internal static void Shade(uint* material, uint* output, LightField light,
        int width, int height, bool allowAvx2 = true)
    {
        bool simd = allowAvx2 && Avx2.IsSupported;
        bool simd512 = allowAvx2 && Avx512F.IsSupported;
        nint materialPointer = (nint)material;
        nint outputPointer = (nint)output;
        int columns = light.Columns;
        ushort[] gains = light.Gains;

        Parallel.For(0, WorkerCount, worker =>
        {
            uint* source = (uint*)materialPointer;
            uint* target = (uint*)outputPointer;
            int firstY = worker * height / WorkerCount;
            int lastY = (worker + 1) * height / WorkerCount;

            for (int y = firstY; y < lastY; y++)
            {
                int gridY = y / LightField.Cell;
                int fy = y - gridY * LightField.Cell;
                int baseGrid = gridY * columns;
                int row = y * width;

                for (int gridX = 0, x = 0; x < width; gridX++, x += LightField.Cell)
                {
                    int offset = baseGrid + gridX;
                    int left = (gains[offset] * (LightField.Cell - fy)
                        + gains[offset + columns] * fy + 8) >> 4;
                    int right = (gains[offset + 1] * (LightField.Cell - fy)
                        + gains[offset + columns + 1] * fy + 8) >> 4;
                    int slope = right - left;
                    int count = Math.Min(LightField.Cell, width - x);
                    int k = 0;

                    if (simd512)
                    {
                        for (; k + 16 <= count; k += 16)
                        {
                            Vector512<int> factors = Avx512F.ShiftRightArithmetic(
                                Avx512F.Add(Vector512.Create(left * 16 + slope * k + 8),
                                    Avx512F.MultiplyLow(Vector512.Create(slope), Ramp512)), 4);
                            ShadeSixteen(source + row + x + k, target + row + x + k, factors);
                        }
                    }

                    if (simd)
                    {
                        for (; k + 8 <= count; k += 8)
                        {
                            // Ramped per-lane gain, so even fine penumbras have
                            // no 8-pixel block boundaries or staircase artefacts.
                            Vector256<int> factors = Avx2.ShiftRightArithmetic(
                                Avx2.Add(Vector256.Create(left * 16 + slope * k + 8),
                                    Avx2.MultiplyLow(Vector256.Create(slope), Ramp)), 4);
                            ShadeEight(source + row + x + k, target + row + x + k, factors);
                        }
                    }

                    for (; k < count; k++)
                    {
                        int gain = (left * 16 + slope * k + 8) >> 4;
                        target[row + x + k] = ShadePixel(source[row + x + k], gain);
                    }
                }
            }
        });
    }

    internal static void ShadeFallback(uint* output, byte[] paper, LightField light,
        int width, int height)
    {
        // On systems where WDA_EXCLUDEFROMCAPTURE is unsupported, keep all
        // applications accessible and do not repeatedly capture the overlay.
        // Unlike the full mode, this fallback cannot remap actual desktop RGB.
        nint outputPointer = (nint)output;
        Parallel.For(0, WorkerCount, worker =>
        {
            uint* result = (uint*)outputPointer;
            int firstY = worker * height / WorkerCount;
            int lastY = (worker + 1) * height / WorkerCount;
            for (int y = firstY; y < lastY; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x;
                    int gain = light.Sample(x, y);
                    int alpha = Math.Clamp(245 - (gain * 162 >> 8), 76, 235);
                    int grain = (paper[index] - 128) / 6;
                    int r = Math.Clamp((36 + grain) * alpha / 255, 0, alpha);
                    int g = Math.Clamp((37 + grain) * alpha / 255, 0, alpha);
                    int b = Math.Clamp((40 + grain) * alpha / 255, 0, alpha);
                    result[index] = (uint)(alpha << 24 | r << 16 | g << 8 | b);
                }
            }
        });
    }

    private static void ShadeSixteen(uint* input, uint* output, Vector512<int> factors)
    {
        Vector512<uint> pixel = Vector512.Load(input);
        Vector512<uint> red = Avx512F.And(Avx512F.ShiftRightLogical(pixel, 16), Mask512);
        Vector512<uint> green = Avx512F.And(Avx512F.ShiftRightLogical(pixel, 8), Mask512);
        Vector512<uint> blue = Avx512F.And(pixel, Mask512);
        red = Avx512F.ShiftRightLogical(
            Avx512F.MultiplyLow(red.AsInt32(), factors).AsUInt32(), 8);
        green = Avx512F.ShiftRightLogical(
            Avx512F.MultiplyLow(green.AsInt32(), factors).AsUInt32(), 8);
        blue = Avx512F.ShiftRightLogical(
            Avx512F.MultiplyLow(blue.AsInt32(), factors).AsUInt32(), 8);
        Vector512<uint> result = Avx512F.Or(
            Avx512F.Or(Avx512F.ShiftLeftLogical(red, 16),
                Avx512F.ShiftLeftLogical(green, 8)),
            Avx512F.Or(blue, Opaque512));
        result.Store(output);
    }

    private static void ShadeEight(uint* input, uint* output, Vector256<int> factors)
    {
        Vector256<uint> pixel = Vector256.Load(input);
        Vector256<uint> red = Avx2.And(Avx2.ShiftRightLogical(pixel, 16), Mask);
        Vector256<uint> green = Avx2.And(Avx2.ShiftRightLogical(pixel, 8), Mask);
        Vector256<uint> blue = Avx2.And(pixel, Mask);

        red = Avx2.ShiftRightLogical(Avx2.MultiplyLow(red.AsInt32(), factors).AsUInt32(), 8);
        green = Avx2.ShiftRightLogical(Avx2.MultiplyLow(green.AsInt32(), factors).AsUInt32(), 8);
        blue = Avx2.ShiftRightLogical(Avx2.MultiplyLow(blue.AsInt32(), factors).AsUInt32(), 8);

        Vector256<uint> rgb = Avx2.Or(
            Avx2.Or(Avx2.ShiftLeftLogical(red, 16), Avx2.ShiftLeftLogical(green, 8)),
            Avx2.Or(blue, Opaque));
        rgb.Store(output);
    }

    private static uint ShadePixel(uint color, int gain)
    {
        int red = (int)((color >> 16) & 255u) * gain >> 8;
        int green = (int)((color >> 8) & 255u) * gain >> 8;
        int blue = (int)(color & 255u) * gain >> 8;
        return 0xff000000u | (uint)(red << 16 | green << 8 | blue);
    }

    private static int ClampByte(int value) => Math.Clamp(value, 0, 255);
}
