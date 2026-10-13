using System;

namespace Spotlighter;

/// <summary>
/// Re-renders captured desktop pixels as ink on fibrous paper, then applies
/// a soft-area lamp, anisotropic penumbra, wide bounce light and cone scattering.
/// Unlike an alpha-tinted overlay, this changes the actual captured RGB values.
/// </summary>
internal static unsafe class PaperPrintRenderer
{
    internal static void Render(uint* source, uint* output, byte[] paper,
        int width, int height, float aimX, float aimY)
    {
        float lampX = width * 0.5f;
        float lampY = 78f;
        aimX = Math.Clamp(aimX, 0f, width - 1f);
        aimY = Math.Clamp(aimY, 0f, height - 1f);

        float rayX = aimX - lampX;
        float rayY = aimY - lampY;
        float rayLen2 = Math.Max(1f, rayX * rayX + rayY * rayY);
        float invRayLen2 = 1f / rayLen2;
        float invRayLen = 1f / MathF.Sqrt(rayLen2);

        float radiusX = Math.Clamp(width * 0.22f, 220f, 540f);
        float radiusY = Math.Clamp(height * 0.26f, 185f, 440f);
        float invRadiusX2 = 1f / (radiusX * radiusX);
        float invRadiusY2 = 1f / (radiusY * radiusY);

        fixed (byte* texture = paper)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                float yFocus = y - aimY;
                float yLamp = y - lampY;
                float yFocus2 = yFocus * yFocus * invRadiusY2;
                float projY = yLamp * rayY;
                float crossY = yLamp * rayX;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x;
                    float xFocus = x - aimX;
                    float xLamp = x - lampX;
                    float d2 = xFocus * xFocus * invRadiusX2 + yFocus2;

                    // Finite-area source + multiple-scattering approximation.
                    // Broad bounce prevents the surroundings from collapsing to pure black.
                    float direct = 1f / (1f + d2 * (0.42f + 0.83f * d2));
                    float bounce = 1f / (1f + 0.17f * d2);
                    float axis = (xLamp * rayX + projY) * invRayLen2;

                    // Scattering in the dust-filled cone: anisotropic and bounded.
                    float haze = 0f;
                    if (axis > 0f && axis < 1.18f)
                    {
                        float perp = (xLamp * rayY - crossY) * invRayLen;
                        float opening = 19f + 155f * axis;
                        float scatter = 1f - perp * perp / (opening * opening);
                        if (scatter > 0f)
                        {
                            scatter *= scatter;
                            float entrance = MathF.Min(1f, axis * 8f);
                            float exit = MathF.Min(1f, (1.18f - axis) * 9f);
                            haze = scatter * entrance * exit;
                        }
                    }

                    float illumination = Math.Clamp(
                        0.205f + 0.665f * direct + 0.175f * bounce + 0.125f * haze,
                        0f, 1.06f);
                    int grain = texture[index] - 128;
                    float warmth = 0.970f + 0.025f * direct;
                    float microShadow = grain * 0.00035f;
                    illumination = Math.Clamp(illumination + microShadow, 0f, 1.09f);

                    if (source == null)
                    {
                        // Failsafe when capture-exclusion is not supported. Still click-through,
                        // but intentionally does NOT claim a true pixel-level print transform.
                        float alpha = Math.Clamp(0.86f - illumination * 0.55f, 0.20f, 0.83f);
                        float lightTint = Math.Clamp(direct + haze * 0.25f, 0f, 1f);
                        int a = ClampByte(alpha * 255f);
                        int overlayRed = ClampByte((35f + lightTint * 200f + grain * 0.45f) * alpha);
                        int overlayGreen = ClampByte((28f + lightTint * 184f + grain * 0.4f) * alpha);
                        int overlayBlue = ClampByte((21f + lightTint * 153f + grain * 0.35f) * alpha);
                        output[index] = (uint)(a << 24 | overlayRed << 16 | overlayGreen << 8 | overlayBlue);
                        continue;
                    }

                    uint original = source[index];
                    int blue0 = (int)(original & 255u);
                    int green0 = (int)((original >> 8) & 255u);
                    int red0 = (int)((original >> 16) & 255u);
                    int luminance = (red0 * 54 + green0 * 183 + blue0 * 19) >> 8;
                    int darkness = 255 - luminance;

                    // Pigment absorption on a warm cellulose substrate. Retain a restrained
                    // trace of the source hue so charts and UI affordances are legible.
                    float inkR = 243f + grain * 0.48f - darkness * 0.825f
                        + (red0 - luminance) * 0.092f;
                    float inkG = 232f + grain * 0.44f - darkness * 0.815f
                        + (green0 - luminance) * 0.078f;
                    float inkB = 210f + grain * 0.38f - darkness * 0.725f
                        + (blue0 - luminance) * 0.062f;

                    // Print granulation: fibres distort ink absorption more where pigment
                    // coverage is strong, without destroying one-pixel text glyphs.
                    float inkTooth = grain * darkness * (1f / 255f) * 0.055f;
                    float glow = MathF.Max(0f, direct - 0.60f) * 12f + haze * 3f;
                    int red = ClampByte((inkR - inkTooth) * illumination + glow * 1.03f);
                    int green = ClampByte((inkG - inkTooth) * illumination * warmth + glow * 0.82f);
                    int blue = ClampByte((inkB - inkTooth * 0.85f) * illumination * (warmth - 0.032f) + glow * 0.55f);

                    output[index] = 0xff000000u | (uint)(red << 16 | green << 8 | blue);
                }
            }
        }

        // A physically legible brass shade and articulated highlight, drawn
        // after tone mapping and attached to the actual light origin.
        LampPainter.Draw(output, width, height, aimX, aimY);
    }

    private static int ClampByte(float value) => (int)Math.Clamp(value, 0f, 255f);
}
