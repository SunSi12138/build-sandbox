using System.Net;
using System.Net.Sockets;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

namespace StreetEcoTool.Mobile;

/// <summary>
/// A deliberately thin Android client for the existing desktop HTTP API.
/// No SQLite/PMTiles files are copied into the APK, and there is no embedded
/// ASP.NET server: the Windows/Linux host remains the data owner.
/// </summary>
public sealed class ServerPage : ContentPage
{
    private const string SavedAddressKey = "server_address_v1";
    private static readonly Color Accent = Color.FromArgb("#075E54");
    private readonly Entry _address;
    private readonly Label _message;
    private readonly ActivityIndicator _busy;
    private readonly Grid _configuration;
    private readonly Grid _browserArea;
    private readonly WebView _browser;
    private readonly Label _connected;
    private Uri? _approvedServer;
    private bool _connecting;

    public ServerPage()
    {
        Title = "环保排查";
        BackgroundColor = Color.FromArgb("#F4F8F6");
        _address = new Entry
        {
            Keyboard = Keyboard.Url,
            Placeholder = "例如：192.168.1.20:5180",
            Text = Preferences.Default.Get(SavedAddressKey, ""),
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            HorizontalOptions = LayoutOptions.Fill,
            AutomationId = "ServerAddress",
        };
        _message = new Label
        {
            Text = "手机与电脑连接同一 Wi-Fi。电脑先运行 hbtool，并启用局域网监听。",
            TextColor = Colors.DarkSlateGray,
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _busy = new ActivityIndicator { Color = Accent, IsRunning = false, IsVisible = false };
        var connect = new Button
        {
            Text = "连接现有应用",
            BackgroundColor = Accent,
            TextColor = Colors.White,
            CornerRadius = 10,
            AutomationId = "ConnectServer",
        };
        connect.Clicked += async (_, _) => await ConnectAsync();
        _address.Completed += async (_, _) => await ConnectAsync();

        var form = new VerticalStackLayout
        {
            Padding = new Thickness(22, 34),
            Spacing = 18,
            Children =
            {
                new Label { Text = "环保排查 · Android", FontSize = 26, FontAttributes = FontAttributes.Bold, TextColor = Accent },
                new Label { Text = "连接同一局域网中的电脑端程序", FontSize = 15, TextColor = Colors.DarkSlateGray },
                new Label { Text = "电脑地址", FontAttributes = FontAttributes.Bold, TextColor = Colors.Black },
                _address,
                connect,
                _busy,
                _message,
                new Label { Text = "此 APK 是 WebView 客户端。数据库、图片、PMTiles、字体及样式均保存在电脑端，不会复制到手机。", FontSize = 12, TextColor = Colors.Gray },
            }
        };
        _configuration = new Grid
        {
            Children = { new ScrollView { Content = form } }
        };

        _browser = new WebView
        {
            AutomationId = "HostedWebPage",
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = Colors.White,
        };
        _browser.Navigating += OnNavigating;
        _browser.Navigated += (_, args) =>
        {
            if (args.Result != WebNavigationResult.Success)
                _connected.Text = "页面未能加载，请检查电脑服务和网络";
        };

        var settings = new Button { Text = "设置地址", FontSize = 12, BackgroundColor = Colors.Transparent, TextColor = Accent, Padding = new Thickness(7, 0) };
        settings.Clicked += (_, _) => ShowSettings();
        var refresh = new Button { Text = "刷新", FontSize = 12, BackgroundColor = Colors.Transparent, TextColor = Accent, Padding = new Thickness(7, 0) };
        refresh.Clicked += (_, _) => _browser.Reload();
        _connected = new Label { TextColor = Colors.DarkSlateGray, FontSize = 12, VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var toolbar = new Grid
        {
            Padding = new Thickness(8, 0),
            HeightRequest = 42,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            BackgroundColor = Color.FromArgb("#EDF5F0"),
        };
        toolbar.Add(_connected, 0, 0);
        toolbar.Add(refresh, 1, 0);
        toolbar.Add(settings, 2, 0);

        _browserArea = new Grid
        {
            IsVisible = false,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        _browserArea.Add(toolbar, 0, 0);
        _browserArea.Add(_browser, 0, 1);

        var root = new Grid();
        root.Add(_configuration);
        root.Add(_browserArea);
        Content = root;
    }

    private void ShowSettings()
    {
        _browserArea.IsVisible = false;
        _configuration.IsVisible = true;
        _approvedServer = null;
    }

    private async Task ConnectAsync()
    {
        if (_connecting) return;
        if (!TryParseServer(_address.Text, out var url, out var reason))
        {
            _message.Text = reason;
            return;
        }
        _connecting = true;
        _busy.IsVisible = true;
        _busy.IsRunning = true;
        _message.Text = "正在检查电脑服务……";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync(new Uri(url!, "/api/system"));
            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                _message.Text = "已连接到该地址，但没有找到环保排查服务（/api/system）。";
                return;
            }
            _approvedServer = url;
            Preferences.Default.Set(SavedAddressKey, url!.GetLeftPart(UriPartial.Authority));
            _connected.Text = url.Authority;
            _browser.Source = new UrlWebViewSource { Url = url.ToString() };
            _configuration.IsVisible = false;
            _browserArea.IsVisible = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _message.Text = "连接失败。检查电脑已启动、监听 0.0.0.0、Wi-Fi 相同且 Windows 防火墙允许端口 5180。";
        }
        finally
        {
            _connecting = false;
            _busy.IsRunning = false;
            _busy.IsVisible = false;
        }
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs args)
    {
        if (_approvedServer is null) { args.Cancel = true; return; }
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var next)) { args.Cancel = true; return; }
        if (next.Scheme is "about" && next.AbsoluteUri == "about:blank") return;
        if (next.Scheme == _approvedServer.Scheme &&
            next.Host.Equals(_approvedServer.Host, StringComparison.OrdinalIgnoreCase) &&
            next.Port == _approvedServer.Port) return;

        // Don't let links escape the approved local backend origin.
        args.Cancel = true;
        _connected.Text = "已阻止跳转到其他服务器";
    }

    private static bool TryParseServer(string? input, out Uri? result, out string message)
    {
        result = null;
        message = "";
        var text = input?.Trim() ?? "";
        if (text.Length == 0) { message = "请输入电脑的局域网 IP 地址。"; return false; }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("/" or "") || uri.Host.Length == 0)
        {
            message = "请输入正确地址，如 http://192.168.1.20:5180";
            return false;
        }
        // Plain HTTP is appropriate only over a trusted private LAN.
        if (uri.Scheme == "http" && !IsPrivateAddress(uri.Host))
        {
            message = "HTTP 只允许私有局域网 IPv4 地址（如 192.168.x.x）；公网地址必须使用 HTTPS。";
            return false;
        }
        result = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        return true;
    }

    private static bool IsPrivateAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    protected override bool OnBackButtonPressed()
    {
        if (_browserArea.IsVisible)
        {
            if (_browser.CanGoBack) _browser.GoBack();
            else ShowSettings();
            return true;
        }
        return base.OnBackButtonPressed();
    }
}
