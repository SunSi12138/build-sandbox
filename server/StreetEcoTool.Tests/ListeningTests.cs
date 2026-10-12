using System.Net;
using System.Text.Json;
using Xunit;

namespace StreetEcoTool.Tests;

public sealed class ListeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hbtool-listening-" + Guid.NewGuid().ToString("N"));
    private static NetworkInterfaceDto[] Adapters() => [new("以太网", "192.168.1.23", false), new("蒲公英", "172.16.12.8", false), new("vEthernet (WSL)", "172.20.0.1", true)];

    [Fact]
    public void CommandLineUsesLocalhostByDefaultAndOverridesSavedAddresses()
    {
        Directory.CreateDirectory(_root);
        var defaults = Startup.ParseArguments([], _root);
        Assert.Equal(["127.0.0.1"], defaults.Listen);
        Assert.Equal(5180, defaults.Port);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{\"port\":5190,\"listen\":[\"192.168.1.23\"]}");
        var configured = Startup.ParseArguments([], _root);
        Assert.Equal(["192.168.1.23"], configured.Listen);
        Assert.Equal(5190, configured.Port);
        var explicitOptions = Startup.ParseArguments(["--port", "5191", "--listen", "127.0.0.1", "--listen=172.16.12.8", "--no-browser"], _root);
        Assert.Equal(["127.0.0.1", "172.16.12.8"], explicitOptions.Listen);
        Assert.Equal(5191, explicitOptions.Port);
        Assert.True(explicitOptions.ListenOverride);
        Assert.True(explicitOptions.NoBrowser);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("127.1")]
    [InlineData("https://127.0.0.1")]
    [InlineData("300.1.1.1")]
    [InlineData("")]
    public void RejectsNoncanonicalOrNonIpv4ListeningValues(string address) => Assert.Throws<ApiException>(() => ListeningService.Normalize([address]));

    [Fact]
    public void RequiresAtLeastOneAndKeepsWildcardExclusive()
    {
        Assert.Throws<ApiException>(() => ListeningService.Normalize([]));
        Assert.Throws<ApiException>(() => ListeningService.Normalize(["0.0.0.0", "127.0.0.1"]));
        Assert.Equal(["127.0.0.1"], ListeningService.Normalize(["127.0.0.1", "127.0.0.1"]));
    }

    [Fact]
    public void MissingSavedInterfacesAreReportedAndFallBackToLoopback()
    {
        var service = new ListeningService(_root, 5180, ["192.168.1.99"], Adapters);
        var info = service.GetInfo(IPAddress.Loopback);
        Assert.Equal(["127.0.0.1"], info.Listen);
        Assert.Equal(["192.168.1.99"], info.UnavailableSaved);
        Assert.Equal(["http://localhost:5180"], info.Addresses);
        Assert.Equal(3, info.Interfaces.Length);
        Assert.Contains(info.Interfaces, n => n.Name == "蒲公英" && !n.Other);
    }

    [Fact]
    public void OnlyLoopbackCanSaveAndOnlyActualInterfaceAddressesAreAccepted()
    {
        var service = new ListeningService(_root, 5180, ["127.0.0.1"], Adapters);
        Assert.Equal(403, Assert.Throws<ApiException>(() => service.Prepare(new(["0.0.0.0"]), IPAddress.Parse("192.168.1.23"))).StatusCode);
        Assert.Equal(403, Assert.Throws<ApiException>(() => service.Prepare(new(["0.0.0.0"]), null)).StatusCode);
        Assert.Equal(400, Assert.Throws<ApiException>(() => service.Prepare(new(["192.168.1.99"]), IPAddress.IPv6Loopback)).StatusCode);
        Assert.False(File.Exists(Path.Combine(_root, "appsettings.json")));
        Assert.True(ListeningService.IsLoopback(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(ListeningService.IsLoopback(IPAddress.Parse("::ffff:192.168.1.23")));
    }

    [Fact]
    public void ConfigurationIsSavedBeforeRebindingWithoutChangingCurrentAddresses()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{\"port\":5180,\"futureOption\":{\"enabled\":true}}");
        var service = new ListeningService(_root, 5190, ["127.0.0.1"], Adapters);
        var response = service.Prepare(new(["172.16.12.8"]), IPAddress.Loopback);
        Assert.True(response.Rebinding);
        Assert.Equal("http://172.16.12.8:5190", response.LocalUrl);
        Assert.Equal(["127.0.0.1"], service.Current);
        Assert.True(service.GetInfo(IPAddress.Loopback).Switching);
        Assert.Equal(409, Assert.Throws<ApiException>(() => service.Prepare(new(["0.0.0.0"]), IPAddress.Loopback)).StatusCode);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "appsettings.json")));
        Assert.Equal(5190, saved.RootElement.GetProperty("port").GetInt32());
        Assert.True(saved.RootElement.GetProperty("futureOption").GetProperty("enabled").GetBoolean());
        Assert.Equal("172.16.12.8", saved.RootElement.GetProperty("listen")[0].GetString());
        service.SignalChange();
        Assert.True(service.Changes.TryRead(out _));
        service.Applied(service.TakePending());
        var active = service.GetInfo(IPAddress.Loopback);
        Assert.False(active.Switching);
        Assert.Equal(["http://172.16.12.8:5190"], active.Addresses);
        Assert.False(service.GetInfo(IPAddress.Parse("172.16.12.8")).CanConfigure);
    }

    [Fact]
    public void AllInterfacesIncludeLoopbackAndEverySelectableIpv4Interface()
    {
        var addresses = ListeningService.AccessAddresses(["0.0.0.0"], 5180, Adapters());
        Assert.Equal(4, addresses.Length);
        Assert.Contains("http://localhost:5180", addresses);
        Assert.Contains("http://172.16.12.8:5180", addresses);
        Assert.DoesNotContain(addresses, value => value.Contains("0.0.0.0", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        GC.SuppressFinalize(this);
    }
}
