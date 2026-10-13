using System;

namespace Spotlighter;

/// <summary>
/// Deterministic anisotropic fractional noise: long cellulose fibres, fine tooth,
/// low-frequency pulp clouds and rare microscopic specks. Generated only on resize.
/// </summary>
internal static class PaperTexture
{
    internal static byte[] Generate(int width, int height)
    {
        byte[] result = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                uint h = Hash(x, y);
                float micro = ((h & 255u) - 127.5f) / 127.5f;
                float pulp = ValueNoise(x / 112f, y / 130f);
                float tooth = ValueNoise(x / 17f, y / 24f);
                float fibre = ValueNoise(x / 3.2f, y / 36f);
                float tone = 128f + micro * 12f + pulp * 11f + tooth * 9f + fibre * 6f;
                if ((h & 8191u) == 97u) tone -= 28f;
                if ((h & 16383u) == 116u) tone += 26f;
                result[row + x] = (byte)Math.Clamp((int)tone, 0, 255);
            }
        }
        return result;
    }

    private static float ValueNoise(float x, float y)
    {
        int ix = (int)MathF.Floor(x);
        int iy = (int)MathF.Floor(y);
        float tx = x - ix, ty = y - iy;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
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
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + 0x9E3779B9u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }
}
