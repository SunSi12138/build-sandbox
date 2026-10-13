namespace Spotlighter;

/// <summary>
/// HLSL compiled by the Windows built-in d3dcompiler_47.dll on GPU startup.
/// Paper Read Light Effect-inspired: soft mask over intact source pixels,
/// no CSS/Cef/Electron, no screenshot, no independent concentric rings.
/// </summary>
internal static class PaperLightShader
{
    internal const string Source = """
cbuffer Settings : register(b0)
{
    float4 View;      // width, height, focusX, focusY
    float4 Lamp;      // hanging origin X/Y, animation time, strength
};

struct Varying
{
    float4 position : SV_Position;
};

Varying vertexMain(uint vertexId : SV_VertexID)
{
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    Varying v;
    v.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return v;
}

float hash(float2 p)
{
    // Stable blue-ish noise used only for pigment tooth and edge microstructure.
    float3 h = frac(float3(p.xyx) * 0.1031);
    h += dot(h, h.yzx + 33.33);
    return frac((h.x + h.y) * h.z);
}

float smoothNoise(float2 p)
{
    float2 base = floor(p);
    float2 w = frac(p);
    w = w * w * (3.0 - 2.0 * w);
    float a = lerp(hash(base), hash(base + float2(1, 0)), w.x);
    float b = lerp(hash(base + float2(0, 1)), hash(base + float2(1, 1)), w.x);
    return lerp(a, b, w.y);
}

float4 blendLamp(float4 underneath, float3 lampColor, float opacity)
{
    opacity = saturate(opacity);
    return float4(
        lampColor * opacity + underneath.rgb * (1.0 - opacity),
        opacity + underneath.a * (1.0 - opacity));
}

float4 pixelMain(Varying input) : SV_Target
{
    float2 p = input.position.xy;
    float2 size = View.xy;
    float2 cursor = View.zw;
    float2 anchor = Lamp.xy;

    // One unified, continuous irradiance footprint. Deliberate distortion
    // avoids synthetic target rings. No stacked smoothstep ellipses.
    float2 span = float2(max(170.0, size.x * 0.215),
                        max(160.0, size.y * 0.290));
    float xNoise = smoothNoise(p * 0.0033);
    float yNoise = smoothNoise(p.yx * 0.0054 + 14.0);
    float2 local = (p - cursor) / span;
    local += (float2(xNoise, yNoise) - 0.5) * 0.038;

    float r2 = dot(local, local);
    float incident = exp2(-1.80 * r2);
    float face = pow(saturate(incident), 0.84);

    // Paper bump map: minute pressed-cellulose fibres create local
    // diffuse shading without fake light-emitting white borders.
    float lineFibre = sin(p.x * 0.084 + sin(p.y * 0.022) * 2.2) * 0.5;
    float pulp = smoothNoise(p * float2(0.023, 0.012));
    float micro = hash(floor(p * 0.71)) - 0.5;
    float tooth = lineFibre * 0.30 + (pulp - 0.5) * 0.48 + micro * 0.22;

    // Low intensity broad reflected light. It changes the shadow floor,
    // not the bright spot shape, so it cannot form another visible ring.
    float ambient = 0.095 + 0.055 * saturate(1.0 - p.y / max(size.y, 1.0));
    float irradiance = saturate(ambient + face * 0.86);
    float matte = irradiance * (1.0 + tooth * 0.035 * face);

    // Paper neither emits light nor uses a sepia colour grade.
    // Let the real RGB of underlying apps remain untouched in the lit area.
    // Darkness is a GPU alpha-composited physically motivated screen veil.
    float alpha = saturate(0.845 - matte * 0.778);
    alpha = saturate(alpha + tooth * lerp(0.012, 0.036, face));
    float3 inkShadow = float3(0.020, 0.023, 0.030);
    float3 ivoryScatter = float3(0.82, 0.81, 0.78);
    float3 veil = lerp(inkShadow, ivoryScatter, face * 0.75);
    float4 result = float4(veil * alpha, alpha);

    // A small directional light shaft, almost invisible under ambient.
    // Unlike a second spotlight, it modifies opacity only inside a
    // physically bounded cone between the pendant and the reading point.
    float2 direction = normalize(cursor - anchor + float2(0.0, 0.01));
    float2 fromLamp = p - anchor;
    float along = dot(fromLamp, direction);
    float across = fromLamp.x * direction.y - fromLamp.y * direction.x;
    float travel = max(1.0, distance(cursor, anchor));
    float progress = along / travel;
    if (progress > 0.08 && progress < 1.0)
    {
        float aperture = 18.0 + progress * 138.0;
        float beamEdge = saturate(1.0 - abs(across) / aperture);
        float lowHaze = 0.055 * beamEdge * beamEdge *
                        smoothstep(0.08, 0.32, progress) *
                        (1.0 - smoothstep(0.65, 1.0, progress));
        // Restore underlying desktop rather than painting a glowing triangle.
        float a2 = max(0.0, result.a - lowHaze);
        result.rgb *= a2 / max(result.a, 0.001);
        result.a = a2;
    }

    // Suspended brushed-brass shade, drawn as a rotated SDF in the same GPU pass.
    // No pasted PNG and no circular "bloom" sprites.
    float2 q = p - anchor;
    float lampAlong = dot(q, direction);
    float lampAcross = q.x * direction.y - q.y * direction.x;
    float halfSpan = lerp(7.5, 27.0, saturate((lampAlong + 19.0) / 51.0));
    float crossCoverage = saturate(halfSpan + 0.65 - abs(lampAcross));
    float topCoverage = smoothstep(-20.0, -18.0, lampAlong);
    float bottomCoverage = 1.0 - smoothstep(30.5, 32.7, lampAlong);
    float metal = crossCoverage * topCoverage * bottomCoverage;
    if (metal > 0.001)
    {
        float highlight = pow(saturate(1.0 - abs(lampAcross) / halfSpan), 1.65);
        float brush = sin(lampAlong * 0.94 + lampAcross * 0.10) * 0.020;
        float3 brass = float3(0.37, 0.28, 0.18) +
            highlight * float3(0.24, 0.18, 0.11) +
            float3(brush, brush * 0.8, brush * 0.5);
        result = blendLamp(result, brass, metal);
    }

    // Rim, bulb and ceiling cord are intentionally small, restrained accents.
    float rim = saturate(1.1 - abs(lampAlong - 32.0) / 1.25) *
                saturate(28.0 - abs(lampAcross));
    result = blendLamp(result, float3(0.66, 0.52, 0.34), rim * 0.95);

    float socket = saturate(1.0 - length((q - direction * 37.0) / float2(9.0, 9.0)));
    result = blendLamp(result, float3(0.89, 0.78, 0.57), socket * 0.87);

    float cord = (1.0 - smoothstep(0.6, 1.8, abs(p.x - anchor.x))) *
                 (1.0 - smoothstep(anchor.y - 7.0, anchor.y - 2.0, p.y));
    result = blendLamp(result, float3(0.17, 0.15, 0.14), cord * 0.90);

    return saturate(result);
}
""";
}
