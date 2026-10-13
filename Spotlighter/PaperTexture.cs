using System;

namespace Spotlighter;

/// <summary>
/// A cold-pressed, neutral ivory paper height/absorption map. Expensive
/// multioctave noise is built once into a 1024px tile; the screen-size copy
/// only adds subtle non-repeating micro-tooth. This avoids hundreds of
/// millions of noise evaluations at initial activation on 4K monitors.
/// </summary>
internal static class PaperTexture
{
    private const int TileSize = 1024;

    internal static byte[] Generate(int width, int height)
    {
        byte[] tile = new byte[TileSize * TileSize];
        for (int y = 0; y < TileSize; y++)
        {
            int row = y * TileSize;
            for (int x = 0; x < TileSize; x++)
            {
                uint h = Hash(x, y);
                float paperPulp = ValueNoise(x / 174f, y / 183f);
                float pressedFiber = ValueNoise(x / 4.5f, y / 77f);
                float fineTooth = ValueNoise(x / 21f, y / 29f);
                float microroughness = ((h & 255u) - 127.5f) / 127.5f;
                float value = 128f + paperPulp * 6f + pressedFiber * 5f
                    + fineTooth * 5f + microroughness * 8f;
                tile[row + x] = (byte)Math.Clamp((int)value, 0, 255);
            }
        }

        byte[] result = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int dst = y * width;
            int source = (y & (TileSize - 1)) * TileSize;
            for (int x = 0; x < width; x++)
            {
                int roughness = tile[source + (x & (TileSize - 1))];
                // Period-independent tooth avoids perceivable tiled micrograin.
                uint h = Hash(x ^ (y << 1), y ^ (x >> 2));
                roughness += (int)((h >> 20) & 7u) - 3;
                result[dst + x] = (byte)Math.Clamp(roughness, 0, 255);
            }
        }

        return result;
    }

    private static float ValueNoise(float x, float y)
    {
        int ix = (int)MathF.Floor(x);
        int iy = (int)MathF.Floor(y);
        float tx = x - ix, ty = y - iy;
        tx *= tx * (3f - 2f * tx);
        ty *= ty * (3f - 2f * ty);
        float a = Noise(ix, iy);
        float b = Noise(ix + 1, iy);
        float c = Noise(ix, iy + 1);
        float d = Noise(ix + 1, iy + 1);
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }

    private static float Noise(int x, int y) => (Hash(x, y) & 65535u) * (2f / 65535f) - 1f;

    private static uint Hash(int x, int y)
    {
        unchecked
        {
            uint hash = (uint)x * 374761393u + (uint)y * 668265263u + 0x9E3779B9u;
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return hash ^ (hash >> 16);
        }
    }
}
