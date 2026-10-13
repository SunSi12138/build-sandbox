using System;

namespace Spotlighter;

/// <summary>
/// A low-resolution, continuously interpolated irradiance field.
/// All expensive exponentials are evaluated at one sample per 16x16 pixels,
/// not once per display pixel. The field is rebuilt when the lamp moves.
/// </summary>
internal sealed class LightField
{
    internal const int Cell = 16;
    internal int Columns { get; }
    internal int Rows { get; }
    internal ushort[] Gains { get; }

    private readonly int _width;
    private readonly int _height;

    internal LightField(int width, int height)
    {
        _width = width;
        _height = height;
        Columns = (width + Cell - 1) / Cell + 1;
        Rows = (height + Cell - 1) / Cell + 1;
        Gains = new ushort[checked(Columns * Rows)];
    }

    internal void Update(float focusX, float focusY)
    {
        focusX = Math.Clamp(focusX, 0f, _width - 1f);
        focusY = Math.Clamp(focusY, 0f, _height - 1f);
        float lampX = _width * 0.5f;
        const float lampY = 82f;
        float directionX = focusX - lampX;
        float directionY = focusY - lampY;
        float directionSquared = MathF.Max(1f, directionX * directionX + directionY * directionY);
        float inverseLength = 1f / MathF.Sqrt(directionSquared);

        // A real matte surface reflects incident illumination. There is no
        // additive white bloom on the page or emissive paper highlights.
        float radiusX = Math.Clamp(_width * 0.225f, 150f, 670f);
        float radiusY = Math.Clamp(_height * 0.285f, 165f, 545f);
        float inverseRx2 = 1f / (radiusX * radiusX);
        float inverseRy2 = 1f / (radiusY * radiusY);

        for (int gridY = 0; gridY < Rows; gridY++)
        {
            float y = Math.Min(_height - 1, gridY * Cell);
            float dy = y - focusY;
            float beamY = y - lampY;
            float radialY = dy * dy * inverseRy2;
            int offset = gridY * Columns;
            for (int gridX = 0; gridX < Columns; gridX++)
            {
                float x = Math.Min(_width - 1, gridX * Cell);
                float dx = x - focusX;
                float radial = dx * dx * inverseRx2 + radialY;

                // Soft direct area light with a long, low-energy bounce tail.
                float direct = MathF.Exp(-1.18f * radial);
                float bounce = MathF.Exp(-0.20f * radial);

                // A weak, wide scattering lobe that grows with distance from
                // the shade. It is atmospheric depth, not a hard triangle.
                float beamX = x - lampX;
                float projection = (beamX * directionX + beamY * directionY) / directionSquared;
                float scatter = 0f;
                if (projection > 0f && projection < 1.12f)
                {
                    float distanceFromAxis = (beamX * directionY - beamY * directionX) * inverseLength;
                    float softness = 23f + 190f * projection;
                    float ratio = distanceFromAxis / softness;
                    float arrival = MathF.Min(1f, projection * 5f);
                    float departure = MathF.Min(1f, (1.12f - projection) * 7f);
                    scatter = MathF.Exp(-2.3f * ratio * ratio) * arrival * departure;
                }

                // Low ambient light gives the unlit page some dimensionality
                // instead of crushing all distant text into pure black.
                float irradiance = 0.145f + 0.542f * direct + 0.245f * bounce + 0.032f * scatter;
                Gains[offset + gridX] =
                    (ushort)Math.Clamp((int)MathF.Round(irradiance * 256f), 0, 248);
            }
        }
    }

    internal int Sample(int x, int y)
    {
        int gx = Math.Clamp(x / Cell, 0, Columns - 2);
        int gy = Math.Clamp(y / Cell, 0, Rows - 2);
        int fx = x - gx * Cell;
        int fy = y - gy * Cell;
        int index = gy * Columns + gx;
        int t = Gains[index] * (Cell - fx) + Gains[index + 1] * fx;
        int b = Gains[index + Columns] * (Cell - fx) + Gains[index + Columns + 1] * fx;
        return (t * (Cell - fy) + b * fy + Cell * Cell / 2) / (Cell * Cell);
    }
}
