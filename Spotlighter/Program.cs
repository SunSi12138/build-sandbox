using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spotlighter;

/// <summary>
/// Native AOT Win32 message pump. No WPF, WinForms, COM, browser runtime or JIT.
/// Hold Ctrl alone for 800ms to toggle; Ctrl+shortcuts do not trigger.
/// </summary>
internal static unsafe class Program
{
    private const string WindowClass = "Spotlighter.LayeredPaperWindow.1";
    private const uint TrayMessage = Native.WM_USER + 27;
    private static readonly nuint TimerId = 1;
    private const long LongPressMs = 800;

    // Keep reverse P/Invoke delegates alive for the lifetime of the process.
    private static readonly Native.WndProc WindowCallback = WindowProc;
    private static readonly Native.HookProc KeyboardCallback = KeyboardProc;

    private static nint _module, _messageWindow, _hook, _classNamePtr;
    private static Native.NOTIFYICONDATA _tray;
    private static bool _trayAdded, _exiting, _active, _ctrlHeld, _otherKeySeen, _triggered;
    private static long _ctrlSince, _lastRender, _lastRenderCost;
    private static IDesktopOverlay? _surface;
    private static bool _preferGpu = true;
    private static float _aimX, _aimY;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            Environment.ExitCode = SelfTest.Run();
            return;
        }
        if (args.Length == 1 && args[0] == "--shader-test")
        {
            Environment.ExitCode = GpuOverlay.ValidateShader();
            return;
        }
        if (args.Length == 1 && args[0] == "--cpu") _preferGpu = false;
        if (args.Length == 2 && args[0] == "--benchmark")
        {
            Environment.ExitCode = SelfTest.Benchmark(args[1]);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;

        using Mutex singleInstance = new(true, "Local\\Spotlighter.6D5C334E", out bool firstInstance);
        if (!firstInstance) return;
        try
        {
            Initialize();
            while (Native.GetMessageW(out Native.MSG msg, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
        catch (Exception e)
        {
            Native.MessageBoxW(0, e.Message, "Spotlighter could not start", 0x10);
        }
        finally
        {
            Shutdown();
        }
    }

    private static void Initialize()
    {
        // Negative four is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2.
        Native.SetProcessDpiAwarenessContext(new nint(-4));
        _module = Native.GetModuleHandleW(null);
        _classNamePtr = Marshal.StringToHGlobalUni(WindowClass);
        Native.WNDCLASSEX cls = new()
        {
            cbSize = (uint)sizeof(Native.WNDCLASSEX),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowCallback),
            hInstance = _module,
            lpszClassName = _classNamePtr
        };
        if (Native.RegisterClassExW(ref cls) == 0)
            throw new InvalidOperationException("Could not register native window class.");

        _messageWindow = Native.CreateWindowExW(0, WindowClass, "Spotlighter", 0,
            0, 0, 0, 0, 0, 0, _module, 0);
        if (_messageWindow == 0)
            throw new InvalidOperationException("Could not create the tray message window.");

        InstallTray();
        _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, KeyboardCallback, _module, 0);
        if (_hook == 0)
            throw new InvalidOperationException("Global keyboard hook is unavailable.");
        if (Native.SetTimer(_messageWindow, TimerId, 16, 0) == 0)
            throw new InvalidOperationException("Unable to start the window timer.");
    }

    private static void InstallTray()
    {
        Native.NOTIFYICONDATA data = new()
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATA),
            hWnd = _messageWindow,
            uID = 1,
            uFlags = Native.NIF_ICON | Native.NIF_MESSAGE | Native.NIF_TIP,
            uCallbackMessage = TrayMessage,
            hIcon = Native.LoadIconW(0, new nint(32512))
        };
        const string tip = "Spotlighter - hold Ctrl 0.8s; right click for menu";
        for (int i = 0; i < tip.Length && i < 127; i++) data.szTip[i] = tip[i];
        _tray = data;
        _trayAdded = Native.Shell_NotifyIconW(Native.NIM_ADD, ref _tray);
    }

    private static nint WindowProc(nint hwnd, uint msg, nuint wp, nint lp)
    {
        // GPU no-redirection windows have no alpha hit-test. The extended
        // transparent style plus this response gives Windows a second
        // opportunity to pass clicks to the underlying application.
        if (hwnd != _messageWindow && msg == 0x0084) return new nint(-1);
        if (hwnd != _messageWindow && msg == 0x0021) return new nint(3);
        if (hwnd == _messageWindow)
        {
            if (msg == Native.WM_TIMER)
            {
                Tick();
                return 0;
            }
            if (msg == TrayMessage)
            {
                if ((uint)lp == Native.WM_RBUTTONUP) ShowTrayMenu();
                else if ((uint)lp == Native.WM_LBUTTONDBLCLK) Toggle();
                return 0;
            }
            if (msg == Native.WM_DISPLAYCHANGE)
            {
                RecreateDesktop();
                return 0;
            }
            if (msg == Native.WM_CLOSE)
            {
                Shutdown();
                return 0;
            }
            if (msg == Native.WM_DESTROY)
            {
                Native.PostQuitMessage(0);
                return 0;
            }
        }
        return Native.DefWindowProcW(hwnd, msg, wp, lp);
    }

    private static nint KeyboardProc(int code, nuint wp, nint lp)
    {
        if (code == Native.HC_ACTION && lp != 0)
        {
            Native.KBDLLHOOKSTRUCT* info = (Native.KBDLLHOOKSTRUCT*)lp;
            uint key = info->vkCode;
            uint ev = (uint)wp;
            bool down = ev is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = ev is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            bool isCtrl = key is Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL;
            if (isCtrl)
            {
                if (down && !_ctrlHeld)
                {
                    _ctrlHeld = true;
                    _otherKeySeen = false;
                    _triggered = false;
                    _ctrlSince = Environment.TickCount64;
                }
                else if (up)
                {
                    _ctrlHeld = false;
                    _otherKeySeen = false;
                    _triggered = false;
                }
            }
            else if (down && _ctrlHeld)
            {
                // Ctrl+C, Ctrl+V, Ctrl+Shift, etc. must not toggle the mode.
                _otherKeySeen = true;
            }
        }
        return Native.CallNextHookEx(_hook, code, wp, lp);
    }

    private static void Tick()
    {
        if (_exiting) return;
        long now = Environment.TickCount64;
        if (_ctrlHeld && !_otherKeySeen && !_triggered && now - _ctrlSince >= LongPressMs)
        {
            _triggered = true;
            Toggle();
        }
        if (!_active || _surface is null) return;

        // Full native-resolution print processing is CPU intensive; cap it by area.
        long pixels = (long)_surface.Width * _surface.Height;
        int targetInterval = _surface.IsGpu ? 16 :
            pixels >= 7_000_000 ? 33 : pixels >= 3_000_000 ? 25 : 16;
        int interval = _surface.IsGpu ? targetInterval :
            Math.Max(targetInterval, (int)Math.Min(145, _lastRenderCost * 1.30));
        if (now - _lastRender < interval) return;
        _lastRender = now;
        try
        {
            if (Native.GetCursorPos(out Native.POINT position))
            {
                float easing = _surface.IsGpu ? 0.39f : 0.23f;
                _aimX += (position.X - _aimX) * easing;
                _aimY += (position.Y - _aimY) * easing;
            }
            long started = Environment.TickCount64;
            _surface.Render(_aimX, _aimY);
            _lastRenderCost = Math.Max(1, Environment.TickCount64 - started);
        }
        catch (Exception e)
        {
            bool gpuFailed = _surface?.IsGpu == true;
            _active = false;
            _surface?.Dispose();
            _surface = null;
            if (gpuFailed)
            {
                // Recover from device removal, shader/driver errors.
                ReportGpuFailure(e);
                _preferGpu = false;
                Toggle();
            }
            else Native.MessageBoxW(0, e.Message, "Spotlighter rendering failed", 0x30);
        }
    }

    private static void Toggle()
    {
        if (_exiting) return;
        try
        {
            if (_active)
            {
                _active = false;
                _surface?.Deactivate();
                return;
            }
            if (_surface is null)
            {
                if (_preferGpu)
                {
                    try { _surface = new GpuOverlay(WindowClass, _module); }
                    catch (Exception gpuError)
                    {
                        ReportGpuFailure(gpuError);
                        _preferGpu = false;
                    }
                }
                _surface ??= new DesktopSurface(WindowClass, _module);
            }
            if (Native.GetCursorPos(out Native.POINT pt))
            {
                _aimX = pt.X;
                _aimY = pt.Y;
            }
            else
            {
                _aimX = _surface.Left + _surface.Width * 0.5f;
                _aimY = _surface.Top + _surface.Height * 0.6f;
            }
            _surface.Activate();
            _lastRender = 0;
            _active = true;
        }
        catch (Exception e)
        {
            _active = false;
            _surface?.Dispose();
            _surface = null;
            Native.MessageBoxW(0, e.Message, "Spotlighter cannot activate", 0x30);
        }
    }

    private static void RecreateDesktop()
    {
        bool wasActive = _active;
        _active = false;
        _surface?.Dispose();
        _surface = null;
        if (wasActive) Toggle();
    }

    private static void ShowTrayMenu()
    {
        nint menu = Native.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, 1, _active ? "Disable Spotlighter" : "Enable Spotlighter");
            Native.AppendMenuW(menu, Native.MF_STRING, 3,
                _preferGpu ? "Switch to CPU print mode" : "Try GPU paper-light mode");
            Native.AppendMenuW(menu, Native.MF_STRING, 2, "Exit");
            Native.GetCursorPos(out Native.POINT cursor);
            Native.SetForegroundWindow(_messageWindow);
            uint choice = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                cursor.X, cursor.Y, 0, _messageWindow, 0);
            if (choice == 1) Toggle();
            else if (choice == 2) Shutdown();
            else if (choice == 3)
            {
                bool resume = _active;
                _active = false;
                _surface?.Dispose();
                _surface = null;
                _preferGpu = !_preferGpu;
                if (resume) Toggle();
            }
        }
        finally { Native.DestroyMenu(menu); }
    }

    private static void ReportGpuFailure(Exception ex)
    {
        // GPU fails open to the old screenshot renderer. Keep a diagnostic
        // without blocking the user behind an activation failure dialog.
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Spotlighter");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "gpu-fallback.log"),
                DateTimeOffset.Now + Environment.NewLine + ex);
        }
        catch { }
    }

    private static void Shutdown()
    {
        if (_exiting) return;
        _exiting = true;
        if (_messageWindow != 0) Native.KillTimer(_messageWindow, TimerId);
        if (_hook != 0) { Native.UnhookWindowsHookEx(_hook); _hook = 0; }
        _active = false;
        _surface?.Dispose();
        _surface = null;
        if (_trayAdded) { Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _tray); _trayAdded = false; }
        if (_messageWindow != 0)
        {
            nint hwnd = _messageWindow;
            _messageWindow = 0;
            Native.DestroyWindow(hwnd);
        }
        Native.PostQuitMessage(0);
        if (_classNamePtr != 0) { Marshal.FreeHGlobal(_classNamePtr); _classNamePtr = 0; }
    }
}
