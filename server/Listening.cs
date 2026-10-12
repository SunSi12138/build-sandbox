using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace StreetEcoTool;

internal sealed record StartupConfig(int Port = 5180, string[]? Listen = null);
internal sealed record LaunchOptions(int Port, string[] Listen, bool NoBrowser, bool SeedDemo, bool Help, bool ListenOverride);
public sealed record NetworkInterfaceDto(string Name, string Address, bool Other);
public sealed record SystemInfoResponse(int Port, string LocalUrl, string[] Addresses, string Today, string Version,
    string[] Listen, NetworkInterfaceDto[] Interfaces, bool CanConfigure, string[] UnavailableSaved, bool Switching, string? LastError);
public sealed record ListenRequest(string[] Listen);
public sealed record ListenResponse(bool Ok, string[] Listen, string[] Addresses, string LocalUrl, bool Rebinding);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(StartupConfig))]
[JsonSerializable(typeof(SystemInfoResponse))]
[JsonSerializable(typeof(ListenRequest))]
[JsonSerializable(typeof(ListenResponse))]
internal partial class StartupJsonContext : JsonSerializerContext;

/// <summary>Owns listening configuration across WebApplication instances.</summary>
internal sealed class ListeningService
{
    private readonly object _gate = new();
    private readonly string _baseDirectory;
    private readonly Func<NetworkInterfaceDto[]> _interfaces;
    private string[] _current;
    private string[] _saved;
    private string[]? _pending;
    private bool _switching;
    private string? _lastError;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });

    internal ListeningService(string baseDirectory, int port, string[] configured, Func<NetworkInterfaceDto[]>? interfaces = null)
    {
        _baseDirectory = baseDirectory;
        Port = port;
        _interfaces = interfaces ?? GetInterfaces;
        _saved = Normalize(configured);
        _current = ResolveAvailable(_saved, _interfaces(), out var unavailable);
        foreach (var address in unavailable) Console.WriteLine($"警告：已保存的 {address} 当前不可用，已跳过。");
        if (_current.SequenceEqual(new[] { "127.0.0.1" }) && unavailable.Length == _saved.Length)
            Console.WriteLine("警告：没有可用的监听地址，已回退到 127.0.0.1。");
    }

    internal int Port { get; }
    internal ChannelReader<bool> Changes => _changes.Reader;
    internal string[] Current { get { lock (_gate) return [.. _current]; } }
    internal string CurrentLocalUrl => LocalUrl(Current, Port);

    internal SystemInfoResponse GetInfo(IPAddress? remoteAddress)
    {
        var adapters = _interfaces();
        lock (_gate)
        {
            var missing = _saved.Where(ip => ip is not "127.0.0.1" and not "0.0.0.0" && !adapters.Any(n => n.Address == ip)).ToArray();
            return new SystemInfoResponse(Port, LocalUrl(_current, Port), AccessAddresses(_current, Port, adapters),
                DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "2.0.0", [.. _current], adapters,
                IsLoopback(remoteAddress), missing, _switching, _lastError);
        }
    }

    internal ListenResponse Prepare(ListenRequest request, IPAddress? remoteAddress)
    {
        if (!IsLoopback(remoteAddress)) throw new ApiException(403, "请在运行程序的电脑上修改监听设置");
        var selected = Normalize(request.Listen);
        var adapters = _interfaces();
        if (selected.Any(ip => ip is not "127.0.0.1" and not "0.0.0.0" && !adapters.Any(n => n.Address == ip)))
            throw new ApiException(400, "监听地址必须属于本机网卡");
        lock (_gate)
        {
            if (_switching) throw new ApiException(409, "正在切换监听地址，请稍后重试");
            SaveConfiguration(_baseDirectory, Port, selected);
            _saved = selected;
            _pending = selected;
            _switching = true;
            _lastError = null;
        }
        return new ListenResponse(true, selected, AccessAddresses(selected, Port, adapters), LocalUrl(selected, Port), true);
    }

    internal void SignalChange() => _changes.Writer.TryWrite(true);
    internal string[] TakePending()
    {
        lock (_gate)
        {
            var value = _pending ?? _current;
            _pending = null;
            return [.. value];
        }
    }

    internal void Applied(string[] addresses, string? error = null)
    {
        lock (_gate)
        {
            _current = [.. addresses];
            _switching = false;
            _lastError = error;
        }
    }

    internal static bool IsLoopback(IPAddress? address) => address is not null &&
        (IPAddress.IsLoopback(address) || address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()));

    internal static string[] Normalize(string[]? addresses)
    {
        if (addresses is null || addresses.Length is < 1 or > 64) throw new ApiException(400, "请至少选择一个监听地址");
        var normalized = new List<string>();
        foreach (var value in addresses)
        {
            if (!IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || parsed.ToString() != value)
                throw new ApiException(400, "监听地址必须是有效的 IPv4 地址");
            if (!normalized.Contains(value, StringComparer.Ordinal)) normalized.Add(value);
        }
        if (normalized.Contains("0.0.0.0", StringComparer.Ordinal) && normalized.Count != 1)
            throw new ApiException(400, "所有网卡不能与其他地址同时选择");
        return [.. normalized];
    }

    internal static string[] ResolveAvailable(string[] configured, NetworkInterfaceDto[] adapters, out string[] unavailable)
    {
        unavailable = configured.Where(ip => ip is not "127.0.0.1" and not "0.0.0.0" && !adapters.Any(n => n.Address == ip)).ToArray();
        var available = configured.Except(unavailable, StringComparer.Ordinal).ToArray();
        return available.Length == 0 ? ["127.0.0.1"] : available;
    }

    internal static string LocalUrl(string[] listen, int port) => listen.Any(ip => ip is "127.0.0.1" or "0.0.0.0")
        ? $"http://localhost:{port}" : $"http://{listen[0]}:{port}";

    internal static string[] AccessAddresses(string[] listen, int port, NetworkInterfaceDto[] adapters)
    {
        var result = new List<string>();
        if (listen.Any(ip => ip is "127.0.0.1" or "0.0.0.0")) result.Add($"http://localhost:{port}");
        var addresses = listen.Contains("0.0.0.0", StringComparer.Ordinal) ? adapters.Select(n => n.Address) : listen.Where(ip => ip != "127.0.0.1");
        result.AddRange(addresses.Distinct(StringComparer.Ordinal).Select(ip => $"http://{ip}:{port}"));
        return [.. result];
    }

    internal static NetworkInterfaceDto[] GetInterfaces()
    {
        string[] secondary = ["Docker", "VMware", "VirtualBox", "Hyper-V", "vEthernet", "WSL"];
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => new NetworkInterfaceDto(n.Name, a.Address.ToString(), secondary.Any(s => n.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))))
                .DistinctBy(n => n.Address).OrderBy(n => n.Other).ThenBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Address, StringComparer.Ordinal).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    internal static void SaveConfiguration(string baseDirectory, int port, string[] listen)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        var temp = Path.Combine(baseDirectory, "temp");
        Directory.CreateDirectory(temp);
        var temporary = Path.Combine(temp, $"appsettings-{Guid.NewGuid():N}.json");
        using var existing = File.Exists(path) ? JsonDocument.Parse(File.ReadAllBytes(path)) : null;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    if (existing is not null)
                        foreach (var property in existing.RootElement.EnumerateObject())
                            if (!property.Name.Equals("port", StringComparison.OrdinalIgnoreCase) && !property.Name.Equals("listen", StringComparison.OrdinalIgnoreCase)) property.WriteTo(writer);
                    writer.WriteNumber("port", port);
                    writer.WriteStartArray("listen");
                    foreach (var address in listen) writer.WriteStringValue(address);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
