using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreetEcoTool;

internal sealed record McpConfiguration(string Mode = "off", string? TokenHash = null);
internal sealed record McpSettingsDto(string Mode, bool HasToken, bool CanConfigure, bool Available, string Url);
internal sealed record McpSettingsRequest(string Mode, string TokenAction = "keep");
internal sealed record McpSettingsSaved(McpSettingsDto Settings, string? Token = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(McpConfiguration))]
[JsonSerializable(typeof(McpSettingsDto))]
[JsonSerializable(typeof(McpSettingsRequest))]
[JsonSerializable(typeof(McpSettingsSaved))]
internal partial class McpSettingsJsonContext : JsonSerializerContext;

internal sealed class McpSettingsService
{
    private readonly object _gate = new();
    private readonly string _baseDirectory;
    private readonly ListeningService _listening;
    private McpConfiguration _current;

    internal McpSettingsService(string baseDirectory, ListeningService listening)
    {
        _baseDirectory = baseDirectory;
        _listening = listening;
        _current = Load(baseDirectory);
    }

    internal McpConfiguration Current { get { lock (_gate) return _current; } }

    private static McpConfiguration Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("mcp", StringComparison.OrdinalIgnoreCase)) continue;
                var config = property.Value.Deserialize(McpSettingsJsonContext.Default.McpConfiguration);
                if (config is null || config.Mode is not ("off" or "read" or "write") ||
                    config.TokenHash is not null && (config.TokenHash.Length != 64 || !config.TokenHash.All(Uri.IsHexDigit)))
                    throw new JsonException("MCP 配置格式不正确。");
                return config;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed MCP section must not stop the existing browser application.
            Console.Error.WriteLine("MCP 配置不可用，已关闭 MCP；可在工具设置中重新保存。");
        }
        return new();
    }

    private bool CanConfigure(HttpContext context)
    {
        if (!_listening.CanConfigure(context.Connection.RemoteIpAddress)) return false;
        var host = context.Request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var ip) && _listening.CanConfigure(ip))) return false;
        if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        if (context.Request.Headers.TryGetValue("Origin", out var origin))
        {
            if (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var actual) ||
                !Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out var expected) ||
                !actual.GetLeftPart(UriPartial.Authority).Equals(expected.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private McpSettingsDto Describe(McpConfiguration config, bool canConfigure) =>
        new(config.Mode, config.TokenHash is not null, canConfigure,
            _listening.Current.Any(address => address is "127.0.0.1" or "0.0.0.0"),
            $"http://127.0.0.1:{_listening.Port}/mcp");

    private McpSettingsSaved Save(McpSettingsRequest request)
    {
        if (request.Mode is not ("off" or "read" or "write"))
            throw new ApiException(400, "请选择有效的 MCP 权限。");
        if (request.TokenAction is not ("keep" or "generate" or "clear"))
            throw new ApiException(400, "令牌操作无效。");
        lock (_gate)
        {
            var token = request.TokenAction == "generate" ? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() : null;
            var hash = request.TokenAction switch
            {
                "generate" => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token!))),
                "clear" => null,
                _ => _current.TokenHash
            };
            var next = new McpConfiguration(request.Mode, hash);
            ListeningService.UpdateConfiguration(_baseDirectory, ["mcp"], writer =>
            {
                writer.WritePropertyName("mcp");
                JsonSerializer.Serialize(writer, next, McpSettingsJsonContext.Default.McpConfiguration);
            });
            // Only publish a new policy after its atomic configuration write succeeds.
            _current = next;
            return new(Describe(next, true), token);
        }
    }

    internal void Map(WebApplication app)
    {
        RequestDelegate get = context => Api.Write(context, Describe(Current, CanConfigure(context)), McpSettingsJsonContext.Default.McpSettingsDto);
        RequestDelegate put = async context =>
        {
            if (!CanConfigure(context)) throw new ApiException(403, "请在运行程序的电脑上修改 MCP 设置。");
            var result = Save(await Api.Read(context, McpSettingsJsonContext.Default.McpSettingsRequest));
            await Api.Write(context, result, McpSettingsJsonContext.Default.McpSettingsSaved);
        };
        app.MapGet("/api/system/mcp", get);
        app.MapPut("/api/system/mcp", put);
    }
}
