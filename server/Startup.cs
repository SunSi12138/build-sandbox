using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;

namespace StreetEcoTool;

internal static class Startup
{
    private const string MutexName = @"Global\StreetEcoTool";
    private const string PipeName = "StreetEcoTool.Address";

    // Keep the mutex owner on this synchronous entry thread throughout the host lifetime.
    // Mutex.ReleaseMutex must execute on the same thread that acquired it.
    public static int Run(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.Help)
            {
                Console.WriteLine("街道生态环保工具\n参数：--port 5180　--listen 0.0.0.0（可重复）　--no-browser　--seed-demo　--help\n默认仅本机；无桌面的设备使用 --listen 0.0.0.0。\n配置示例：{\"port\":5180,\"listen\":[\"127.0.0.1\"]}");
                return 0;
            }

            using var mutex = new Mutex(false, MutexName);
            bool ownsMutex;
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }

            if (!ownsMutex)
            {
                var address = ReadRunningAddress();
                if (address is null)
                    Console.WriteLine("工具已经在运行，当前未能取得它的地址。请查看已运行窗口中的访问网址。");
                else
                {
                    Console.WriteLine($"工具已经在运行：{address}");
                    if (!options.NoBrowser) OpenBrowser(address);
                }
                return 0;
            }

            try { RunServerAsync(options).GetAwaiter().GetResult(); }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception exception) when (IsAddressInUse(exception))
        {
            Console.Error.WriteLine("启动失败：端口已被其他程序占用。请关闭占用程序，或用 --port 5181 / appsettings.json 更换端口后重试。");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or SqliteException or ApiException)
        {
            Console.Error.WriteLine($"启动失败：{exception.Message}");
            Console.Error.WriteLine("请确认程序文件夹可写，且配置文件格式正确。");
            return 1;
        }
    }

    internal static LaunchOptions ParseArguments(string[] args, string? baseDirectory = null)
    {
        var port = 5180;
        var settingsPath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "appsettings.json");
        string[] listen = ["127.0.0.1"];
        if (File.Exists(settingsPath))
        {
            using var stream = File.OpenRead(settingsPath);
            var config = JsonSerializer.Deserialize(stream, StartupJsonContext.Default.StartupConfig) ?? throw new ArgumentException("配置文件不能为空");
            port = config.Port;
            listen = config.Listen ?? ["127.0.0.1"];
        }
        var noBrowser = false;
        var seedDemo = false;
        var help = false;
        var commandListen = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var value = args[i];
            if (value is "--help" or "-h") help = true;
            else if (value == "--no-browser") noBrowser = true;
            else if (value == "--seed-demo") seedDemo = true;
            else if (value == "--port" || value.StartsWith("--port=", StringComparison.Ordinal))
            {
                var text = value == "--port" ? (++i < args.Length ? args[i] : "") : value[7..];
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                    throw new ArgumentException("--port 后需要填写 1 至 65535 之间的整数。");
            }
            else if (value == "--listen" || value.StartsWith("--listen=", StringComparison.Ordinal))
            {
                var address = value == "--listen" ? (++i < args.Length ? args[i] : "") : value[9..];
                commandListen.Add(address);
            }
            else throw new ArgumentException($"无法识别参数：{value}。可运行 --help 查看用法。");
        }
        if (port is < 1 or > 65535) throw new ArgumentException("端口必须介于 1 至 65535。");
        return new LaunchOptions(port, ListeningService.Normalize(commandListen.Count > 0 ? [.. commandListen] : listen), noBrowser, seedDemo, help, commandListen.Count > 0);
    }

    private static async Task RunServerAsync(LaunchOptions options)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var staticDirectory = Path.Combine(baseDirectory, "wwwroot");
        Directory.CreateDirectory(staticDirectory);
        using var store = new Store(Path.Combine(baseDirectory, "data"));
        var clock = TimeProvider.System;
        Console.WriteLine($"当前时区：{clock.LocalTimeZone.Id}");
        var configuredZone = Environment.GetEnvironmentVariable("TZ");
        if (clock.LocalTimeZone.BaseUtcOffset == TimeSpan.Zero && !string.IsNullOrWhiteSpace(configuredZone) && configuredZone is not "UTC" and not "Etc/UTC" and not "GMT")
            Console.WriteLine("时区可能不正确，请安装 tzdata 或设置 TZ=Asia/Shanghai");
        // Run before demo data and before any HTTP writer can change today's database.
        store.RunDailyMaintenance(DateOnly.FromDateTime(clock.GetLocalNow().Date));
        if (options.SeedDemo) store.SeedDemo();
        var listening = new ListeningService(baseDirectory, options.Port, options.Listen);
        if (options.ListenOverride) Console.WriteLine("命令行 --listen 已覆盖 appsettings.json 中的监听设置。");
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        EventHandler exit = (_, _) => shutdown.Cancel();
        Console.CancelKeyPress += interrupt;
        AppDomain.CurrentDomain.ProcessExit += exit;
        using var termination = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; shutdown.Cancel(); }) : null;
        using var maintenance = new DailyMaintenanceScheduler(clock, store.RunDailyMaintenance,
            error => Console.Error.WriteLine($"每日快照或清理失败：{error.Message}"));
        WebApplication? app = null;
        try
        {
            app = await StartHostAsync(options, store, listening, listening.Current, baseDirectory, staticDirectory);
            await using var announcement = new InstanceAnnouncement(() => listening.CurrentLocalUrl);
            PrintAddresses(listening);
            Console.WriteLine($"数据库：{store.DataDir}\n照片：{store.PhotosDir}\n备份：{store.BackupDir}\n临时文件：{store.TemporaryDir}\n按 Ctrl+C 正常关闭。\n");
            if (!options.NoBrowser) OpenBrowser(listening.CurrentLocalUrl);
            while (await listening.Changes.WaitToReadAsync(shutdown.Token))
            {
                while (listening.Changes.TryRead(out _)) { }
                await Task.Delay(300, shutdown.Token);
                var requested = listening.TakePending();
                var previous = listening.Current;
                await app.StopAsync();
                await app.DisposeAsync();
                app = null;
                try
                {
                    app = await StartHostAsync(options, store, listening, requested, baseDirectory, staticDirectory);
                    listening.Applied(requested);
                }
                catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"监听切换失败：{error.Message}");
                    var recovered = ListeningService.ResolveAvailable(previous, ListeningService.GetInterfaces(), out _);
                    app = await StartHostAsync(options, store, listening, recovered, baseDirectory, staticDirectory);
                    listening.Applied(recovered, "监听切换失败，已恢复此前监听地址");
                }
                PrintAddresses(listening);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally
        {
            Console.CancelKeyPress -= interrupt;
            AppDomain.CurrentDomain.ProcessExit -= exit;
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }
    }

    private static async Task<WebApplication> StartHostAsync(LaunchOptions options, Store store, ListeningService listening,
        string[] addresses, string baseDirectory, string staticDirectory)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = baseDirectory, WebRootPath = staticDirectory
        });
        builder.Services.AddSingleton<IHostLifetime>(new RebindHostLifetime());
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = 10L * 1024 * 1024;
            foreach (var address in addresses)
            {
                if (address == "127.0.0.1") server.ListenLocalhost(options.Port);
                else if (address == "0.0.0.0") server.ListenAnyIP(options.Port);
                else server.Listen(IPAddress.Parse(address), options.Port);
            }
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(log => { log.SingleLine = true; log.TimestampFormat = "HH:mm:ss "; });
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
            json.SerializerOptions.TypeInfoResolverChain.Insert(1, StartupJsonContext.Default);
        });
        var app = builder.Build();
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not "GET" and not "HEAD" and not "OPTIONS")
                    store.EnsurePreviousDaySnapshot(DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().Date));
                await next(context);
            }
            catch (Exception error) when (!context.Response.HasStarted && error is ApiException or JsonException or BadHttpRequestException or SqliteException or IOException or UnauthorizedAccessException)
            {
                var (status, message) = error switch
                {
                    ApiException api => (api.StatusCode, api.Message),
                    JsonException => (400, "提交的数据格式不正确，请检查填写内容后重试。"),
                    BadHttpRequestException bad => (bad.StatusCode, bad.StatusCode == 413 ? "上传文件超过大小限制。照片请先压缩到 10 MB 以内。" : "请求内容不完整，请重试。"),
                    SqliteException sql when sql.SqliteErrorCode == 19 => (409, "数据存在重复或仍被其他记录引用，请检查后重试。"),
                    SqliteException sql when sql.SqliteErrorCode is 5 or 6 => (503, "数据库正在处理其他操作，请稍后重试。"),
                    _ => (500, "数据读写失败，请检查磁盘剩余空间及程序文件夹权限。")
                };
                if (status >= 500) app.Logger.LogError(error, "数据处理失败");
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new ErrorDto(message), AppJsonContext.Default.ErrorDto, contentType: null, cancellationToken: context.RequestAborted);
            }
        });

        var fileProvider = app.Environment.WebRootFileProvider;
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = response =>
            {
                var name = response.File.Name;
                if (name is "index.html" or "200.html" or "404.html")
                    response.Context.Response.Headers.CacheControl = "no-store, max-age=0";
                else if (response.Context.Request.Path.StartsWithSegments("/_nuxt") && Path.GetExtension(name) is ".js" or ".css" or ".wasm" or ".svg" or ".png" or ".jpg")
                    response.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                else response.Context.Response.Headers.CacheControl = "no-cache";
            }
        });

        Api.Map(app, store);
        PhotoApi.Map(app, store);
        store.MapBackupApi(app);
        store.MapWordApi(app);
        RequestDelegate systemInfo = context => Api.Write(context, listening.GetInfo(context.Connection.RemoteIpAddress), StartupJsonContext.Default.SystemInfoResponse);
        RequestDelegate changeListen = async context =>
        {
            if (!ListeningService.IsLoopback(context.Connection.RemoteIpAddress))
                throw new ApiException(403, "请在运行程序的电脑上修改监听设置");
            var result = listening.Prepare(await Api.Read(context, StartupJsonContext.Default.ListenRequest), context.Connection.RemoteIpAddress);
            context.Response.OnCompleted(() => { listening.SignalChange(); return Task.CompletedTask; });
            await Api.Write(context, result, StartupJsonContext.Default.ListenResponse);
        };
        app.MapGet("/api/system", systemInfo);
        app.MapPut("/api/system/listen", changeListen);
        RequestDelegate missingApi = async context =>
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new ErrorDto("接口不存在。"), AppJsonContext.Default.ErrorDto, contentType: null, cancellationToken: context.RequestAborted);
        };
        app.Map("/api/{**path}", missingApi);
        RequestDelegate fallback = async context =>
        {
            if (context.Request.Path.StartsWithSegments("/photos") || Path.HasExtension(context.Request.Path))
            { context.Response.StatusCode = 404; return; }
            var index = Path.Combine(staticDirectory, "index.html");
            context.Response.Headers.CacheControl = "no-store, max-age=0";
            context.Response.ContentType = "text/html; charset=utf-8";
            if (File.Exists(index)) await context.Response.SendFileAsync(index, context.RequestAborted);
            else await context.Response.WriteAsync("<!doctype html><meta charset=utf-8><title>街道环保工具</title><h1>后端已启动</h1><p>请先构建前端，将 web/.output/public 复制到程序同目录的 wwwroot 文件夹。</p>", context.RequestAborted);
        };
        app.MapFallback(fallback);

        try { await app.StartAsync(); return app; }
        catch { await app.DisposeAsync(); throw; }
    }

    private static void PrintAddresses(ListeningService listening)
    {
        var info = listening.GetInfo(IPAddress.Loopback);
        Console.WriteLine($"\n街道生态环保工具已启动\n当前监听：{string.Join(", ", info.Listen)}");
        foreach (var address in info.Addresses) Console.WriteLine($"可访问：{address}");
    }

    private sealed class RebindHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal static string[] GetLanAddresses(int port) => ListeningService.GetInterfaces().Select(n => $"http://{n.Address}:{port}").ToArray();

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } || current is Microsoft.AspNetCore.Connections.AddressInUseException) return true;
        return false;
    }

    private static void OpenBrowser(string address)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux() && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))))
            {
                var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(address);
                Process.Start(start);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Console.WriteLine($"未能自动打开浏览器，请手动访问 {address}"); }
    }

    private static string? ReadRunningAddress()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In, PipeOptions.Asynchronous);
            client.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            using var reader = new StreamReader(client);
            var text = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "http" && (uri.Host == "localhost" || IPAddress.TryParse(uri.Host, out var host) && (ListeningService.IsLoopback(host) || ListeningService.GetInterfaces().Any(n => n.Address == host.ToString()))) ? text : null;
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return null; }
    }

    private sealed class InstanceAnnouncement : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _task;
        public InstanceAnnouncement(Func<string> address) => _task = AnnounceAsync(address, _cancellation.Token);
        private static async Task AnnounceAsync(Func<string> address, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.Out, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(cancellationToken);
                    await using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
                    await writer.WriteLineAsync(address().AsMemory(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is SocketException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"当前系统不允许发布已运行实例的访问地址：{error.Message}。主服务继续运行，请使用控制台显示的网址。");
                    break;
                }
                catch (IOException)
                {
                    try { await Task.Delay(200, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            await _task;
            _cancellation.Dispose();
        }
    }
}
