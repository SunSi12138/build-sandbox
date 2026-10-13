# Spotlighter — .NET 10 Native AOT desktop paper lamp

A click-through desktop print and lighting overlay for Windows 10 (2004+) and Windows 11 x64. **Single-file native executable**, written in C#/.NET 10 with Win32; no browser engine, WPF, WinForms, JIT, or third-party runtime DLL.

## Install & use

1. Open **GitHub Actions → Spotlighter - Windows Native AOT → latest successful run** on the `spotlighter` branch.
2. Download the artifact `Spotlighter-win-x64-native-aot`, extract `Spotlighter.exe`, and run it.
3. Hold **Ctrl alone for 0.8 seconds** to enable/disable. Ctrl+normal shortcut keys do not toggle.
4. The pendant lamp aims at the mouse with easing. Use the notification area icon to toggle or exit.

## v2 improvements: material first, light second

The original prototype recoloured the entire screen and evaluated costly floating point radial/power calculations **at every pixel on every mouse frame**. This version splits the work:

- **Static reflectance**: desktop frames are captured periodically (about 80–145 ms, depending on resolution) and transformed into *neutral, lightly ivory cellulose paper and ink*, retaining sharp text and low-chroma application colours. Unlike a transparent sepia overlay, actual screenshot pixels are recomposed as pigment on paper.
- **Tactile grain**: 1024×1024 multiscale paper noise is generated only once and copied to the target resolution with additional non-repeating micro-tooth. Fibres are anisotropic; no artificial aged-yellow colour wash.
- **Matte lighting**: mouse movement updates a sparse 16×16-pixel irradiance grid with bilinear interpolation. The grid contains a soft finite-area lamp lobe, long-tail diffuse/bounced illumination, restrained beam scattering, and ambient floor. RGB values are **multiplied** by illumination and capped; the paper is never additively bloomed.
- **Fast pixels**: AVX2 CPU intrinsic batches process eight pixels at a time where available; a bit-identical scalar path supports older CPUs. Render work is split across up to eight CPU worker lanes. Cursor frames are decoupled from desktop recapture.
- **Adaptive timing**: frames throttle based on the duration of the last frame instead of forcing expensive 4K repainting at an unreasonable rate.
- **Safety**: topmost no-activate click-through window; WDA_EXCLUDEFROMCAPTURE avoids self-capture feedback when supported. A neutral translucent fallback is used on systems where capture exclusion fails.

### Important benchmark boundary

A headless Windows Native AOT benchmark in GitHub Actions (1920×1080) measures **only paper preparation and the moving-light pixel kernel**, not full desktop capture, transparency/composition, the screen driver, or input latency. The hosted Windows runner reports **AVX2 not available**, so that measurement exercises the *scalar* path; the AVX2 path is compiled and selected automatically on capable hardware but still requires physical-hardware validation.

### Build

Requires .NET 10 SDK plus Microsoft C++ build tools. GitHub Actions builds on Windows.

```powershell
dotnet publish ./Spotlighter/Spotlighter.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o ./artifacts/publish
```

For headless verification (no desktop window needed):

```powershell
./artifacts/publish/Spotlighter.exe --self-test
./artifacts/publish/Spotlighter.exe --benchmark ./benchmark.txt
```

A zero exit code means the test passed. The benchmark writes numerical results to the supplied file.

## Architectural limitations / potential GPU v3

Despite the optimized CPU shading, this is **not yet GPU-first**. GDI `BitBlt` and `UpdateLayeredWindow` transfer a full display-sized frame through CPU memory; those copies can still be the bottleneck at 4K, multi-monitor, HDR, and high refresh rates.

For a true GPU path, move the *whole pipeline*, not just the color math:

1. DXGI Desktop Duplication or Windows Graphics Capture -> capture surface on GPU.
2. Direct3D 11 shader / HLSL -> screen print mapping, 2D normal/roughness-map-based paper scattering, and optical falloff, all on GPU.
3. A DirectComposition alpha swap chain -> display directly from GPU without readback/GDI.
4. Use **Vortice.Windows / Vortice.Direct3D11** for C# binding (supports .NET 10) or custom native COM vtables, but verify trimming/Native AOT and device loss on real hardware.

Changing only the GPU shader while preserving CPU roundtrips will not solve the total-frame latency.

Other limitations: exclusive fullscreen games, protected video, UAC/secure desktop, anti-cheat software, some HDR modes and asymmetric multi-monitor layouts may not work as expected. The lamp is anchored to the top of the virtual desktop. The EXE is unsigned; some Windows machines may show a SmartScreen warning.

## File map

| File | Role |
| --- | --- |
| `Program.cs` | native Win32 pump, Ctrl hotkey, animation, tray, adaptive timing |
| `Native.cs` | AOT-friendly Win32 P/Invoke declarations |
| `DesktopSurface.cs` | desktop capture, material cache, click-through layer |
| `PaperTexture.cs` | cellulose fibre and paper tooth |
| `PaperPrintRenderer.cs` | static ink preparation, SIMD/scalar matte shading |
| `LightField.cs` | sample-sparse penumbra, bounce and scattering |
| `LampPainter.cs` | mouse-tracking brass pendant and bulb |
| `SelfTest.cs` | Native AOT smoke tests, kernel benchmark |
