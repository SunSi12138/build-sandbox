using System;

namespace Spotlighter;

/// <summary>
/// GDI desktop capture is deliberately decoupled from print material creation
/// and from high-frequency mouse-follow lighting. The latter is SIMD accelerated.
/// One Native AOT layered window remains click-through and capture-excluded.
/// </summary>
internal sealed unsafe class DesktopSurface : IDisposable
{
    private readonly nint _window;
    private readonly nint _screenDc;
    private readonly nint _captureDc, _outputDc;
    private readonly nint _captureBitmap, _outputBitmap;
    private readonly nint _oldCaptureBitmap, _oldOutputBitmap;
    private readonly nint _captureBits, _outputBits;
    private readonly byte[] _paper;
    private readonly uint[] _material;
    private readonly LightField _light;
    private bool _excluded;
    private bool _hasMaterial;
    private bool _visible;
    private bool _disposed;
    private long _lastCapture;

    internal int Left { get; }
    internal int Top { get; }
    internal int Width { get; }
    internal int Height { get; }

    internal DesktopSurface(string windowClass, nint module)
    {
        Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        Width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        Height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (Width < 1 || Height < 1 || (long)Width * Height > 28_000_000)
            throw new InvalidOperationException("Unsupported virtual desktop dimensions.");

        _window = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE
                | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW,
            windowClass, "Spotlighter paper & light surface", Native.WS_POPUP,
            Left, Top, Width, Height, 0, 0, module, 0);
        if (_window == 0)
            throw new InvalidOperationException("Failed to create layered desktop window.");

        try
        {
            _screenDc = Native.GetDC(0);
            _captureDc = Native.CreateCompatibleDC(_screenDc);
            _outputDc = Native.CreateCompatibleDC(_screenDc);
            if (_screenDc == 0 || _captureDc == 0 || _outputDc == 0)
                throw new InvalidOperationException("Unable to create desktop device contexts.");

            Native.BITMAPINFO info = new()
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(Native.BITMAPINFOHEADER),
                    biWidth = Width,
                    biHeight = -Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Native.BI_RGB
                }
            };
            _captureBitmap = Native.CreateDIBSection(_screenDc, ref info, Native.DIB_RGB_COLORS,
                out _captureBits, 0, 0);
            _outputBitmap = Native.CreateDIBSection(_screenDc, ref info, Native.DIB_RGB_COLORS,
                out _outputBits, 0, 0);
            if (_captureBitmap == 0 || _outputBitmap == 0 || _captureBits == 0 || _outputBits == 0)
                throw new InvalidOperationException("Unable to allocate desktop images.");

            _oldCaptureBitmap = Native.SelectObject(_captureDc, _captureBitmap);
            _oldOutputBitmap = Native.SelectObject(_outputDc, _outputBitmap);
            _paper = PaperTexture.Generate(Width, Height);
            _material = new uint[checked(Width * Height)];
            _light = new LightField(Width, Height);
            _excluded = Native.SetWindowDisplayAffinity(_window, Native.WDA_EXCLUDEFROMCAPTURE);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Activate()
    {
        if (_visible) return;
        _lastCapture = 0;
        Native.GetCursorPos(out Native.POINT cursor);
        Render(cursor.X, cursor.Y); // before the overlay becomes visible
        Native.ShowWindow(_window, Native.SW_SHOWNA);
        Native.SetWindowPos(_window, Native.HWND_TOPMOST, Left, Top, Width, Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        bool excludeAfterShow = Native.SetWindowDisplayAffinity(_window, Native.WDA_EXCLUDEFROMCAPTURE);
        if (excludeAfterShow && !_excluded)
        {
            _excluded = true;
            _lastCapture = 0;
            Render(cursor.X, cursor.Y);
        }
        _visible = true;
    }

    internal void Deactivate()
    {
        if (!_visible) return;
        Native.ShowWindow(_window, Native.SW_HIDE);
        _visible = false;
    }

    internal void Render(float aimScreenX, float aimScreenY)
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        long area = (long)Width * Height;
        // Capture GDI at a measured, lower rate; moving a lamp only relights
        // the cached paper pixels. Huge desktop captures can otherwise
        // dominate GPU/CPU bandwidth even with a very fast SIMD shader.
        int captureInterval = area >= 7_000_000 ? 145 : area >= 3_000_000 ? 112 : 78;

        if (_excluded && (!_hasMaterial || _lastCapture == 0 || now - _lastCapture >= captureInterval))
        {
            if (Native.BitBlt(_captureDc, 0, 0, Width, Height,
                    _screenDc, Left, Top, Native.SRCCOPY | Native.CAPTUREBLT))
            {
                fixed (uint* materialPointer = _material)
                {
                    PaperPrintRenderer.PrepareMaterial(
                        (uint*)_captureBits, materialPointer, _paper, Width, Height);
                }
                _hasMaterial = true;
                _lastCapture = now;
            }
            else
            {
                _hasMaterial = false;
            }
        }

        float focusX = aimScreenX - Left;
        float focusY = aimScreenY - Top;
        _light.Update(focusX, focusY);

        if (_excluded && _hasMaterial)
        {
            fixed (uint* materialPointer = _material)
            {
                PaperPrintRenderer.Shade(
                    materialPointer, (uint*)_outputBits, _light, Width, Height);
            }
        }
        else
        {
            PaperPrintRenderer.ShadeFallback((uint*)_outputBits,
                _paper, _light, Width, Height);
        }

        LampPainter.Draw((uint*)_outputBits, Width, Height, focusX, focusY);
        Native.POINT destination = new(Left, Top);
        Native.POINT origin = new(0, 0);
        Native.SIZE size = new(Width, Height);
        Native.BLENDFUNCTION blend = new()
        {
            BlendOp = 0,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1
        };
        if (!Native.UpdateLayeredWindow(_window, _screenDc, ref destination, ref size,
                _outputDc, ref origin, 0, ref blend, Native.ULW_ALPHA))
            throw new InvalidOperationException("UpdateLayeredWindow failed. The display mode might be unsupported.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != 0) Native.DestroyWindow(_window);
        if (_captureDc != 0 && _oldCaptureBitmap != 0)
            Native.SelectObject(_captureDc, _oldCaptureBitmap);
        if (_outputDc != 0 && _oldOutputBitmap != 0)
            Native.SelectObject(_outputDc, _oldOutputBitmap);
        if (_captureBitmap != 0) Native.DeleteObject(_captureBitmap);
        if (_outputBitmap != 0) Native.DeleteObject(_outputBitmap);
        if (_captureDc != 0) Native.DeleteDC(_captureDc);
        if (_outputDc != 0) Native.DeleteDC(_outputDc);
        if (_screenDc != 0) Native.ReleaseDC(0, _screenDc);
    }
}
