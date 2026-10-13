# Spotlighter

A tactile, lamp-lit **desktop print mode** for Windows. Written in C# on **.NET 10** with a pure Win32 frontend, and published as a **Native AOT win-x64 executable** (no .NET installation required).

## Interaction

1. Launch `Spotlighter.exe` (it lives in the notification area, not the taskbar).
2. Hold **either Ctrl key by itself for 0.8 seconds** to enable the mode.
3. The pendant lamp turns smoothly towards the mouse pointer. Move the mouse to illuminate the desktop.
4. Hold Ctrl again for 0.8 seconds to turn the mode off.
5. Right-click the notification area icon to toggle or exit. Ctrl+ordinary shortcuts do not toggle the effect.

## Material model

The first implementation performs a genuine **pixel transformation** instead of placing a spotlight CSS radial gradient over the original desktop.

- A screenshot of the virtual desktop is mapped to a warm paper/ink palette; the input luminance still preserves readable text and UI structure.
- Paper tooth contains an anisotropic cellulose-fibre field, two spatial scales of pulp variation, per-pixel micrograin, and rare inclusions.
- Ink deposition follows source luminance with reduced chroma and micrograin-dependent pigment absorption.
- A finite-sized direct source, soft nonlinear penumbra, broad bounce component and anisotropic scattering cone are composed in linear-ish RGB, with a warm reflected tint.
- A rasterized hanging cable, metallic lamp head, brim, glowing bulb and adjustable lamp axis follow the same lighting geometry.
- Capture-exclusion is requested with `WDA_EXCLUDEFROMCAPTURE` so the overlay does not capture itself; if unavailable, the app uses a visibly less faithful transparent fallback rather than an infinite hall-of-mirrors feedback loop.
- The window is topmost, no-activate, tool-window, layered and click-through.

## Build on GitHub

Open **Actions → Spotlighter - Windows Native AOT → latest successful run**, then download the `Spotlighter-win-x64-native-aot` artifact ZIP. Extract `Spotlighter.exe` and run it on **Windows 10 version 2004+ or Windows 11 (x64)**.

The workflow executes:

```powershell
dotnet publish ./Spotlighter/Spotlighter.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o ./artifacts/publish
```

On a local Windows machine with .NET 10 SDK and MSVC C++ build tools, this command also works. Builds require the native Windows toolchain; GitHub's `windows-2022` runner supplies it.

## Current trade-offs

- **CPU prototype**: the filter processes full-resolution desktop pixels in a top-down GDI DIB. It is visually substantial but not a GPU shader. The UI timer throttles rendering based on display area; expect reduced frame rate and elevated CPU load on 4K/multiple monitors. The logical next step for 60 FPS 4K is a D3D11 fullscreen pixel shader with a capture surface.
- **Approximate scattering**: the lamp uses a deliberately layered, physically inspired light model, not real multi-bounce global-illumination/path tracing. Surface-paper fibers and pigment granularity are real procedural texture fields.
- **Capture limits**: games using exclusive fullscreen, protected video, secure desktops/UAC prompts, anti-cheat applications, and some HDR pipelines may be unavailable or distorted when desktop capture is active. The program does not attempt to intercept privileged windows.
- The lamp is anchored at the top center of the **virtual desktop**. On an asymmetric multi-monitor setup this may fall on a bezel; monitor-specific positioning is future work.
- Frame timing and brightness currently live in source code. The project is a runnable first edition rather than a polished configuration-heavy product.
- The executable is **not code signed**. Windows SmartScreen may ask for a confirmation.

## Source map

| File | Purpose |
| --- | --- |
| `Program.cs` | Win32 message loop, global keyboard hook, Ctrl hold, tray menu |
| `Native.cs` | AOT-friendly Win32/GDI/shell P/Invoke signatures |
| `DesktopSurface.cs` | click-through virtual-desktop overlay, capture DIB, ULW_ALPHA |
| `PaperTexture.cs` | pre-generated multi-scale paper tooth and cellulose fiber noise |
| `PaperPrintRenderer.cs` | real screenshot print transformation, bounce/scattering |
| `LampPainter.cs` | rotated brass lampshade, emissive bulb, hanging cable |

This project does not reference WPF, WinForms, web runtimes or built-in COM interop, which keeps the actual Windows executable suitable for Native AOT.
