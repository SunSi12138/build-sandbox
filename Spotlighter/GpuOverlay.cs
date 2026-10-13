using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Spotlighter;

/// <summary>
/// GPU-only overlay: native D3D11 pixel shader -> premultiplied flip swap chain
/// -> DirectComposition target. Does NOT capture or upload desktop pixels.
/// Underlying applications keep their actual RGB, contrast and text sharpness.
/// Native AOT friendly: raw COM vtables, no RCWs/WinRT reflection/NuGet DLLs.
/// </summary>
internal sealed unsafe class GpuOverlay : IDesktopOverlay
{
    // Every COM vtable index below includes IUnknown and inherited methods.
    // Direct3D11 device context inherits ID3D11DeviceChild (four methods).
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x20;
    private const uint DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL = 3;
    private const uint DXGI_ALPHA_MODE_PREMULTIPLIED = 1;
    private const uint D3D11_BIND_CONSTANT_BUFFER = 4;
    private const uint D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4;
    private const uint DXGI_PRESENT_TEST = 1;
    private static readonly Guid FactoryIid = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid DcompIid = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private static readonly Guid TextureIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private nint _window, _device, _context, _factory, _dxgiDevice;
    private nint _swapChain, _dcomp, _target, _visual;
    private nint _vertexShader, _pixelShader, _constants;
    private bool _visible, _disposed;
    private readonly long _start = Environment.TickCount64;

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsGpu => true;

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSwapChainDesc1
    {
        public uint Width, Height, Format, Stereo, SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc
    {
        public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dViewport
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public float ScreenWidth, ScreenHeight, FocusX, FocusY;
        public float LampX, LampY, Seconds, Strength;
    }

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType,
        nint software, uint flags, nint requestedFeatureLevels, uint count,
        uint sdkVersion, out nint device, out uint actualFeatureLevel,
        out nint context);

    [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory2", ExactSpelling = true)]
    private static extern int CreateDXGIFactory2(uint flags, ref Guid iid, out nint result);

    [DllImport("dcomp.dll", EntryPoint = "DCompositionCreateDevice", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice(nint dxgiDevice, ref Guid iid, out nint result);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] code, nuint size, nint name,
        nint macros, nint include, [MarshalAs(UnmanagedType.LPStr)] string entry,
        [MarshalAs(UnmanagedType.LPStr)] string profile, uint flags1, uint flags2,
        out nint shaderBlob, out nint errorBlob);

    private static nint Method(nint obj, int index) => (*(nint**)obj)[index];

    private static int Query(nint obj, ref Guid iid, out nint result)
    {
        nint value = 0;
        Guid copy = iid;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
            Method(obj, 0))(obj, &copy, &value);
        result = value;
        return hr;
    }

    private static void Release(ref nint obj)
    {
        nint p = obj;
        obj = 0;
        if (p != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Method(p, 2))(p);
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException(operation + " returned HRESULT 0x" +
                ((uint)result).ToString("X8"));
    }

    internal GpuOverlay(string className, nint module)
    {
        Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        Width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        Height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        if (Width < 1 || Height < 1 || (long)Width * Height > 36_000_000)
            throw new InvalidOperationException("Unsupported virtual desktop size");

        _window = Native.CreateWindowExW(
            WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_TOPMOST |
            Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW,
            className, "Spotlighter hardware composition", Native.WS_POPUP,
            Left, Top, Width, Height, 0, 0, module, 0);
        if (_window == 0)
            throw new InvalidOperationException("Could not create DirectComposition window");

        try
        {
            // Hardware-only: a software WARP device hides latency rather than fixing it.
            Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7,
                out _device, out _, out _context), "D3D11CreateDevice(HARDWARE)");
            Guid factoryGuid = FactoryIid;
            Check(CreateDXGIFactory2(0, ref factoryGuid, out _factory), "CreateDXGIFactory2");

            DxgiSwapChainDesc1 desc = new()
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleCount = 1,
                SampleQuality = 0,
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = 0, // Stretch is required for composition
                SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED
            };

            nint chain = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, DxgiSwapChainDesc1*, nint, nint*, int>)
                Method(_factory, 24))(_factory, _device, &desc, 0, &chain),
                "CreateSwapChainForComposition");
            _swapChain = chain;

            Guid dxgiGuid = DxgiDeviceIid;
            Check(Query(_device, ref dxgiGuid, out _dxgiDevice), "ID3D11Device/IDXGIDevice");
            Guid compGuid = DcompIid;
            Check(DCompositionCreateDevice(_dxgiDevice, ref compGuid, out _dcomp),
                "DCompositionCreateDevice");

            nint target = 0, visual = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)
                Method(_dcomp, 6))(_dcomp, _window, 1, &target),
                "IDCompositionDevice.CreateTargetForHwnd");
            _target = target;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)
                Method(_dcomp, 7))(_dcomp, &visual), "IDCompositionDevice.CreateVisual");
            _visual = visual;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)
                Method(_visual, 15))(_visual, _swapChain), "IDCompositionVisual.SetContent");
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)
                Method(_target, 3))(_target, _visual), "IDCompositionTarget.SetRoot");
            Check(((delegate* unmanaged[Stdcall]<nint, int>)
                Method(_dcomp, 3))(_dcomp), "IDCompositionDevice.Commit");

            _vertexShader = BuildShader("vertexMain", "vs_4_0", true);
            _pixelShader = BuildShader("pixelMain", "ps_4_0", false);
            BufferDesc constantDesc = new()
            {
                ByteWidth = 32,
                Usage = 0, // D3D11_USAGE_DEFAULT, updated with UpdateSubresource
                BindFlags = D3D11_BIND_CONSTANT_BUFFER
            };
            nint cb = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)
                Method(_device, 3))(_device, &constantDesc, 0, &cb),
                "ID3D11Device.CreateBuffer");
            _constants = cb;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // This self-check works on Windows CI without an interactive desktop or
    // GPU. It catches HLSL syntax/profile problems before shipping the EXE.
    internal static int ValidateShader()
    {
        try
        {
            byte[] source = Encoding.ASCII.GetBytes(PaperLightShader.Source);
            foreach (string profile in new[] { "vs_4_0", "ps_4_0" })
            {
                string entry = profile.StartsWith("vs", StringComparison.Ordinal)
                    ? "vertexMain" : "pixelMain";
                int hr = D3DCompile(source, (nuint)source.Length, 0, 0, 0,
                    entry, profile, 1u << 15, 0, out nint blob, out nint errors);
                try
                {
                    if (hr < 0)
                    {
                        if (errors != 0)
                        {
                            nint data = ((delegate* unmanaged[Stdcall]<nint, nint>)
                                Method(errors, 3))(errors);
                            string? diagnostic = Marshal.PtrToStringAnsi(data);
                            System.IO.File.WriteAllText("shader-diagnostics.txt",
                                profile + ": " + (diagnostic ?? "shader failure"));
                        }
                        return profile == "vs_4_0" ? 61 : 62;
                    }
                }
                finally
                {
                    Release(ref blob);
                    Release(ref errors);
                }
            }
            return 0;
        }
        catch (Exception e)
        {
            System.IO.File.WriteAllText("shader-diagnostics.txt", e.ToString());
            return 63;
        }
    }

    private nint BuildShader(string entry, string profile, bool vertex)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(PaperLightShader.Source);
        int hr = D3DCompile(bytes, (nuint)bytes.Length, 0, 0, 0, entry, profile,
            1u << 15, 0, out nint blob, out nint errors);
        try
        {
            if (hr < 0)
            {
                string compilerMessage = "unknown";
                if (errors != 0)
                {
                    nint errorText = ((delegate* unmanaged[Stdcall]<nint, nint>)
                        Method(errors, 3))(errors);
                    compilerMessage = Marshal.PtrToStringAnsi(errorText) ?? "unknown";
                }
                throw new InvalidOperationException("HLSL " + profile + ": " + compilerMessage);
            }
            nint buffer = ((delegate* unmanaged[Stdcall]<nint, nint>)Method(blob, 3))(blob);
            nuint length = ((delegate* unmanaged[Stdcall]<nint, nuint>)Method(blob, 4))(blob);
            nint shader = 0;
            int result = vertex
                ? ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)
                    Method(_device, 12))(_device, buffer, length, 0, &shader)
                : ((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)
                    Method(_device, 15))(_device, buffer, length, 0, &shader);
            Check(result, "ID3D11Device.CreateShader " + profile);
            return shader;
        }
        finally
        {
            Release(ref blob);
            Release(ref errors);
        }
    }

    public void Activate()
    {
        if (_visible) return;
        Render(Left + Width * 0.5f, Top + Height * 0.6f);
        Native.ShowWindow(_window, Native.SW_SHOWNA);
        Native.SetWindowPos(_window, Native.HWND_TOPMOST, Left, Top, Width, Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _visible = true;
    }

    public void Deactivate()
    {
        if (!_visible) return;
        Native.ShowWindow(_window, Native.SW_HIDE);
        _visible = false;
    }

    public void Render(float mouseScreenX, float mouseScreenY)
    {
        if (_disposed) return;
        ShaderConstants settings = new()
        {
            ScreenWidth = Width,
            ScreenHeight = Height,
            FocusX = Math.Clamp(mouseScreenX - Left, 0, Width - 1),
            FocusY = Math.Clamp(mouseScreenY - Top, 0, Height - 1),
            LampX = Width * 0.5f,
            LampY = 83,
            Seconds = (Environment.TickCount64 - _start) / 1000f,
            Strength = 1.0f
        };

        // Update 32B of shader data, never a full-resolution desktop bitmap.
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)
            Method(_context, 48))(_context, _constants, 0, 0, &settings, 0, 0);

        nint texture = 0, view = 0;
        Guid textureGuid = TextureIid;
        try
        {
            nint backBuffer = 0;
            Guid* guid = &textureGuid;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)
                Method(_swapChain, 9))(_swapChain, 0, guid, &backBuffer),
                "IDXGISwapChain.GetBuffer");
            texture = backBuffer;

            nint renderTarget = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)
                Method(_device, 9))(_device, texture, 0, &renderTarget),
                "ID3D11Device.CreateRenderTargetView");
            view = renderTarget;

            nint* views = stackalloc nint[1] { view };
            nint* buffers = stackalloc nint[1] { _constants };
            D3dViewport viewport = new()
            {
                TopLeftX = 0,
                TopLeftY = 0,
                Width = Width,
                Height = Height,
                MinDepth = 0,
                MaxDepth = 1
            };
            // ID3D11DeviceContext inherits 4 ID3D11DeviceChild vtable entries.
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)
                Method(_context, 33))(_context, 1, views, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, D3dViewport*, void>)
                Method(_context, 44))(_context, 1, &viewport);
            ((delegate* unmanaged[Stdcall]<nint, uint, void>)
                Method(_context, 24))(_context, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)
                Method(_context, 11))(_context, _vertexShader, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)
                Method(_context, 9))(_context, _pixelShader, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)
                Method(_context, 16))(_context, 0, 1, buffers);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)
                Method(_context, 13))(_context, 3, 0);

            // Do not leave an RTV referencing the back buffer across Present.
            nint* clearViews = stackalloc nint[1] { 0 };
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)
                Method(_context, 33))(_context, 0, clearViews, 0);
        }
        finally
        {
            Release(ref view);
            Release(ref texture);
        }

        // Composition swap chain already performs tear-free DWM blending.
        Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)
            Method(_swapChain, 8))(_swapChain, 0, 0), "IDXGISwapChain.Present");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != 0) Native.ShowWindow(_window, Native.SW_HIDE);
        Release(ref _constants);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        Release(ref _visual);
        Release(ref _target);
        Release(ref _dcomp);
        Release(ref _swapChain);
        Release(ref _dxgiDevice);
        Release(ref _factory);
        Release(ref _context);
        Release(ref _device);
        if (_window != 0)
        {
            Native.DestroyWindow(_window);
            _window = 0;
        }
    }
}
