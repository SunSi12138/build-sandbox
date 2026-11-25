using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using QRCoder;

try
{
    Console.OutputEncoding = Encoding.UTF8;
    string directory = Path.Combine(AppContext.BaseDirectory, "Files");
    IPAddress? ip = null;
    int port = 8899;
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--help")
        {
            Console.WriteLine("FileAssistant [--dir PATH] [--ip IPv4] [--port PORT]");
            return 0;
        }
        if (i + 1 >= args.Length) throw new ArgumentException("缺少参数值");
        string key = args[i], value = args[++i];
        switch (key)
        {
            case "--dir": directory = value; break;
            case "--ip":
                if (!IPAddress.TryParse(value, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                    ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
                    throw new ArgumentException("请指定本机 IPv4 地址");
                break;
            case "--port":
                if (!int.TryParse(value, out port) || port is < 1 or > 65535)
                    throw new ArgumentException("端口无效");
                break;
            default: throw new ArgumentException("未知参数：" + key);
        }
    }

    ip ??= FindAddress();
    var address = ip.ToString();
    var url = $"http://{address}:{port}";
    using var receiver = new FileReceiver(Path.GetFullPath(directory), address, port);
    var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
    {
        Args = [],
        ContentRootPath = AppContext.BaseDirectory,
        EnvironmentName = "Production"
    });
    builder.Logging.ClearProviders();
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.AddServerHeader = false;
        options.Listen(ip, port);
        options.Limits.MaxRequestBodySize = FileReceiver.ChunkSize;
        options.Limits.MaxConcurrentConnections = 64;
        options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(20);
    });
    await using var app = builder.Build();
    app.Run(receiver.HandleAsync);
    await app.StartAsync();

    Console.WriteLine("文件助手");
    Console.WriteLine($"保存目录：{receiver.DirectoryPath}");
    Console.WriteLine(url);
    if (!Console.IsOutputRedirected)
    {
        using var generator = new QRCodeGenerator();
        using var qr = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        foreach (var row in qr.ModuleMatrix)
        {
            foreach (bool dark in row)
            {
                Console.BackgroundColor = dark ? ConsoleColor.Black : ConsoleColor.White;
                Console.Write("  ");
            }
            Console.ResetColor();
            Console.WriteLine();
        }
    }
    Console.WriteLine("微信扫码上传文件 · Ctrl+C 退出");
    await app.WaitForShutdownAsync();
    return 0;
}
catch (Exception e)
{
    Console.ResetColor();
    Console.Error.WriteLine("启动失败：" + e.Message);
    return 1;
}

static IPAddress FindAddress()
{
    var candidates = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
        .SelectMany(n =>
        {
            var p = n.GetIPProperties();
            bool gateway = p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));
            bool virtualAdapter = new[] { "docker", "veth", "virbr", "vmnet", "vbox", "wsl", "tailscale" }
                .Any(prefix => n.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return p.UnicastAddresses.Select(a => (Ip: a.Address, Score: (gateway ? 4 : 0) + (virtualAdapter ? 0 : 2)));
        })
        .Where(a => a.Ip.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.IsLoopback(a.Ip) && !a.Ip.Equals(IPAddress.Any))
        .OrderByDescending(a => a.Score).ToArray();
    if (candidates.Length == 0)
        throw new InvalidOperationException("未找到局域网 IPv4，请连接网络后重试或使用 --ip");
    return candidates[0].Ip;
}
