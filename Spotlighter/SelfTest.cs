using System;
using System.Diagnostics;
using System.IO;

namespace Spotlighter;

/// <summary>
/// Headless smoke test: runs the *published* Native AOT EXE in GitHub Actions,
/// without a graphical desktop or GPU. Also checks AVX2 vs scalar bit-exactness.
/// </summary>
internal static unsafe class SelfTest
{
    internal static int Run()
    {
        try
        {
            const int width = 384, height = 256;
            byte[] texture = PaperTexture.Generate(width, height);
            byte min = 255, max = 0;
            foreach (byte value in texture)
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
            if (max - min < 11) return 10;

            uint[] source = new uint[width * height];
            uint[] material = new uint[width * height];
            uint[] simd = new uint[width * height];
            uint[] scalar = new uint[width * height];
            for (int i = 0; i < source.Length; i++) source[i] = 0xfff2f2f0u;
            for (int y = 230; y < 245; y++)
                for (int x = 150; x < 165; x++)
                    source[y * width + x] = 0xff101010u;

            LightField light = new(width, height);
            light.Update(192f, 215f);
            fixed (uint* input = source)
            fixed (uint* ink = material)
            fixed (uint* accelerated = simd)
            fixed (uint* reference = scalar)
            {
                PaperPrintRenderer.PrepareMaterial(input, ink, texture, width, height);
                uint white = material[236 * width + 175];
                uint black = material[236 * width + 155];
                int whiteR = (int)((white >> 16) & 255u);
                int whiteB = (int)(white & 255u);
                int blackR = (int)((black >> 16) & 255u);
                if (whiteR - whiteB > 16) return 11; // cannot look like antique yellow newspaper
                if (blackR >= whiteR - 120) return 12; // preserve print contrast

                PaperPrintRenderer.Shade(ink, accelerated, light, width, height, allowAvx2: true);
                PaperPrintRenderer.Shade(ink, reference, light, width, height, allowAvx2: false);
                for (int i = 0; i < simd.Length; i++)
                    if (simd[i] != scalar[i]) return 13; // guard banding & SIMD regression

                int lit = (int)((simd[205 * width + 192] >> 16) & 255u);
                int shadow = (int)((simd[205 * width + 0] >> 16) & 255u);
                if (lit <= shadow + 30) return 14;
                if ((simd[205 * width + 192] >> 24) != 255u) return 15;

                light.Update(25, 220);
                PaperPrintRenderer.Shade(ink, accelerated, light, width, height);
                if (simd[205 * width + 192] == scalar[205 * width + 192]) return 16;

                PaperPrintRenderer.ShadeFallback(accelerated, texture, light, width, height);
                uint alpha = simd[2 * width + 2] >> 24;
                if (alpha != 255u) return 17;
                alpha = accelerated[2 * width + 2] >> 24;
                if (alpha == 0u || alpha == 255u) return 18;
            }
            return 0;
        }
        catch
        {
            return 90;
        }
    }

    internal static int Benchmark(string filename)
    {
        try
        {
            const int width = 1920, height = 1080;
            byte[] texture = PaperTexture.Generate(width, height);
            uint[] source = new uint[width * height];
            uint[] material = new uint[width * height];
            uint[] output = new uint[width * height];
            for (int i = 0; i < source.Length; i++)
            {
                int x = i % width;
                int shade = x * 255 / width;
                source[i] = 0xff000000u | (uint)(shade << 16 | shade << 8 | shade);
            }
            LightField light = new(width, height);
            Stopwatch clock = Stopwatch.StartNew();
            fixed (uint* input = source)
            fixed (uint* ink = material)
            fixed (uint* result = output)
            {
                PaperPrintRenderer.PrepareMaterial(input, ink, texture, width, height);
                long preparation = clock.ElapsedMilliseconds;
                clock.Restart();
                for (int frame = 0; frame < 20; frame++)
                {
                    light.Update(470f + frame * 30f, 610f);
                    PaperPrintRenderer.Shade(ink, result, light, width, height);
                }
                double average = clock.Elapsed.TotalMilliseconds / 20d;
                File.WriteAllText(filename,
                    "Spotlighter Native AOT headless benchmark\n" +
                    "AVX2 supported: " + PaperPrintRenderer.HasAvx2 + "\n" +
                    "Material build (1920x1080): " + preparation + " ms\n" +
                    "Lighting + SIMD shading, average (1920x1080): " +
                    average.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " ms/frame\n" +
                    "Note: excludes GDI desktop capture, UpdateLayeredWindow and actual desktop compositing.\n");
            }
            return 0;
        }
        catch
        {
            return 91;
        }
    }
}
