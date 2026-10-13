using System;

namespace Spotlighter;

/// <summary>Runs without any desktop window or interactive session.</summary>
internal static unsafe class SelfTest
{
    internal static int Run()
    {
        try
        {
            const int width = 384, height = 256;
            byte[] texture = PaperTexture.Generate(width, height);
            byte min = 255, max = 0;
            foreach (byte t in texture)
            {
                if (t < min) min = t;
                if (t > max) max = t;
            }
            if (max - min < 12) return 10;

            uint[] source = new uint[width * height];
            uint[] output = new uint[width * height];
            for (int i = 0; i < source.Length; i++) source[i] = 0xfff2eee3u;
            for (int y = 230; y < 245; y++)
                for (int x = 150; x < 165; x++)
                    source[y * width + x] = 0xff101010u;

            fixed (uint* input = source)
            fixed (uint* result = output)
            {
                PaperPrintRenderer.Render(input, result, texture, width, height, 192, 215);
                int dark = (int)(result[236 * width + 155] & 255u);
                int pale = (int)(result[236 * width + 174] & 255u);
                if (dark >= pale - 20) return 11;
                if ((result[236 * width + 174] >> 24) != 255u) return 12;

                uint old = result[200 * width + 320];
                PaperPrintRenderer.Render(input, result, texture, width, height, 48, 210);
                if (old == result[200 * width + 320]) return 13;

                PaperPrintRenderer.Render(null, result, texture, width, height, 192, 215);
                uint fallbackAlpha = result[236 * width + 1] >> 24;
                if (fallbackAlpha == 0u || fallbackAlpha == 255u) return 14;
            }
            return 0;
        }
        catch
        {
            return 90;
        }
    }
}
