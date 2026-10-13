# Spotlighter v3 · GPU paper-light desktop mode

Windows 10/11 x64 desktop effect, C#/.NET 10 Native AOT. Hold **Ctrl alone for 800 ms** to enter or leave; use the tray icon for switching backend or exiting. The binary is self-contained and has no third-party DLLs or Chromium/Electron runtime.

## Design reference

Visual direction: https://codetap.org/project/paper-read-light-effect

The reference describes real-time cursor-following lighting using CSS blend masks over a textured reading page. Windows cannot assume the underlying content is a web page. Spotlighter now follows that **compositing strategy** rather than constantly photographing and reprinting all desktop windows.

### Default: GPU paper-light

This version implements a real **Direct3D 11 pixel shader** and a **DirectComposition premultiplied-alpha flip swap chain**, presented into an HWND with `WS_EX_NOREDIRECTIONBITMAP`.

- **No desktop screenshot or 4K CPU bitmap round trip** in the GPU path. Per-frame CPU input is only a 32-byte constant buffer holding screen dimensions, mouse, lamp angle and time.
- Desktop applications preserve their original pixel colours and typographic sharpness. The shader generates a dark neutral veil with a single spatially continuous lighting footprint; the centre becomes transparent, exposing the original desktop. It does *not* remap every window into desaturated brown newsprint.
- A multiscale procedural noise profile creates subtle matte/cold-pressed-paper tooth and slightly distorts the penumbra. There are no stacked independent concentric rings.
- The light has weak, bounded shaft scattering, with very low levels of bounce-like shadow fill; the surface itself has **no additive bloom**. All shader colour is premultiplied with alpha.
- A metal pendant and braided ceiling cable are SDF shapes calculated in the same shader as the shadow. Mouse motion smooths the lamp direction.
- The GPU layer uses `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE` and Win32 hit-test fallbacks so clicks should pass to lower windows, but full cross-process hit testing cannot be proven in headless CI. Long-hold Ctrl always turns it off.

### CPU paper-print fallback

If Direct3D/device/DirectComposition initialisation fails, Spotlighter falls back to the previous screenshot-based full-colour-print transformation. It also supports explicitly forcing the CPU renderer:

```powershell
./Spotlighter.exe --cpu
```

CPU fallback supports dynamic **AVX-512F (16 pixels per batch)**, **AVX2 (8 pixels per batch)**, and a scalar implementation, selected using `.IsSupported`. Every pixel of the accelerated output must match the scalar reference in the automated self-test. CPU saturation retention has been increased relative to v2.

**Important trade-off:** GPU mode and CPU mode are not pixel-identical. GPU mode is faster and maintains natural screen colours, but it is an **overlay**; it cannot truly change a browser page or application's underlying raster graphics into ink on paper. CPU mode can actually transform source RGB but captures/repaints the screen and is heavier. A future fully GPU-based screen-material transform would need DXGI Desktop Duplication or Windows Graphics Capture connected directly to GPU shaders, and careful capture-exclusion handling.

## How to run

1. Download the latest successful `Spotlighter-win-x64-native-aot` artifact on GitHub Actions.
2. Extract `Spotlighter.exe` and run; the app stays in the system tray. No .NET runtime installation required.
3. Hold **Ctrl** for 0.8s to toggle. The lamp follows the mouse; combinations such as Ctrl+C should not toggle.
4. Right-click its notification icon: toggle, switch GPU/CPU backend, or exit.
5. If the GPU initialization fails, inspect `%LOCALAPPDATA%\Spotlighter\gpu-fallback.log`. Switching back to GPU in the tray retries initialization.

If the new GPU-only window doesn't pass mouse clicks on your Windows graphics configuration, **hold Ctrl 0.8s to switch off**, then change to CPU backend from the tray; please report GPU model, Windows version and screenshot. The program is not code signed (SmartScreen may warn).

## Build

Requires .NET 10 SDK and Windows C++ build tools (GitHub Actions `windows-2022` runner supplies these):

```powershell
dotnet publish ./Spotlighter/Spotlighter.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o ./artifacts/publish
```

Native AOT headless tests (can run without a graphics desktop):

```powershell
./artifacts/publish/Spotlighter.exe --shader-test
./artifacts/publish/Spotlighter.exe --self-test
./artifacts/publish/Spotlighter.exe --benchmark ./artifacts/benchmark.txt
```

- `--shader-test` compiles HLSL `vs_4_0` and `ps_4_0` on Windows using `d3dcompiler_47.dll`, without requiring GPU/DWM.
- `--self-test` checks actual CPU material shading, colour neutrality, output alpha, dynamic light and SIMD/scalar correctness.
- `--benchmark` measures only CPU kernels at 1920×1080, *not* display capture, Present, UI thread timing, input latency or GPU frame times. GitHub's runner does **not** expose AVX2/AVX-512; hardware speedups need local measurement.
- **Passing CI validates build and shader source, not GPU/window behaviour.** Device removal, video-driver compatibility, DirectComposition rendering, cross-process hit-testing and HDR require a real Windows desktop.

## Source layout

| File | Purpose |
| --- | --- |
| `GpuOverlay.cs` | Native COM interop for D3D11/DXGI/DirectComposition, shader compilation and GPU rendering |
| `PaperLightShader.cs` | HLSL procedural paper, mask, scattering, metallic lamp |
| `Program.cs` | Win32 tray, global Ctrl hold, GPU selection and fallback, 60fps scheduling |
| `IDesktopOverlay.cs` | common backend contract |
| `DesktopSurface.cs` | legacy GDI screenshot renderer for compatibility |
| `PaperPrintRenderer.cs` | CPU ink and AVX-512/AVX2/scalar fallback |
| `SelfTest.cs` | headless verification and benchmark |

Windows UAC secure desktop, exclusive fullscreen games, DRM-protected content and some HDR modes are outside the supported rendering surface. A single virtual-desktop window is used across monitors. This project makes no claim of physically traced/global-illumination realism.
