using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace StreetEcoTool.Mobile;

public sealed class PhotoLabPage : ContentPage
{
    private readonly WebView _web;
    private readonly Label _status;
    private readonly Button _mode;
    private PhotoLabServer? _server;
    private bool _starting;
    private bool _enhanced = true;

    public PhotoLabPage()
    {
        Title = "WebView 照片验证";
        BackgroundColor = Colors.White;
        _web = new WebView
        {
            AutomationId = "PhotoLabWebView",
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            BackgroundColor = Colors.White
        };
        _web.HandlerChanged += (_, _) => ApplyChooser();
        _web.Navigated += (_, args) =>
        {
            if (args.Result != WebNavigationResult.Success)
                _status.Text = "网页载入失败：" + args.Result;
        };
        _web.Navigating += (_, args) =>
        {
            if (_server?.Origin is not Uri origin ||
                !Uri.TryCreate(args.Url, UriKind.Absolute, out var destination) ||
                destination.Host != origin.Host || destination.Port != origin.Port ||
                destination.Scheme != origin.Scheme)
                args.Cancel = true;
        };

        _mode = new Button
        {
            Text = "系统照片选择器（可多选）",
            FontSize = 12,
            Padding = new Thickness(10, 4),
            BackgroundColor = Color.FromArgb("#0b6c56"),
            TextColor = Colors.White,
            MinimumHeightRequest = 38,
        };
        _mode.Clicked += (_, _) =>
        {
            _enhanced = !_enhanced;
            ApplyChooser();
        };
        var reload = new Button
        {
            Text = "刷新",
            FontSize = 12,
            MinimumHeightRequest = 38,
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#0b6c56")
        };
        reload.Clicked += (_, _) => _web.Reload();
        _status = new Label
        {
            FontSize = 12,
            TextColor = Colors.DarkSlateGray,
            Text = "正在启动独立的本机上传测试服务……",
            LineBreakMode = LineBreakMode.WordWrap
        };
        var top = new VerticalStackLayout
        {
            Padding = new Thickness(12, 8),
            Spacing = 6,
            BackgroundColor = Color.FromArgb("#eaf5ee"),
            Children =
            {
                new Label { Text = "MAUI / Android · WebView 文件上传实验", FontSize = 15,
                            FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#0b6c56") },
                new HorizontalStackLayout { Spacing = 8, Children = { _mode, reload } },
                _status
            }
        };
        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            }
        };
        layout.Add(top, 0, 0);
        layout.Add(_web, 0, 1);
        Content = layout;
    }

    private void ApplyChooser()
    {
#if ANDROID
        if (_web.Handler is WebViewHandler handler)
        {
            // Compare MAUI's built-in FileChooserParams-based picker against an
            // enhanced ChromeClient with explicit gallery/camera choices.
            if (handler.PlatformView.WebChromeClient is MauiWebChromeClient previous)
                previous.UnregisterCallbacks();
            handler.PlatformView.SetWebChromeClient(_enhanced
                ? new PhotoLabChromeClient(handler, this)
                : new MauiWebChromeClient(handler));
        }
#endif
        _mode.Text = _enhanced ? "系统照片选择器（可多选）" : "MAUI 默认文件选择器";
        _status.Text = (_enhanced ? "Android 系统照片选择器（支持多选）" : "MAUI 默认文件选择器") + "已启用；从下方选择照片后可批量上传。";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_server is not null || _starting) return;
        _starting = true;
        try
        {
            var server = new PhotoLabServer();
            server.Start();
            _server = server;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await http.GetAsync(new Uri(server.Origin!, "/api/status"));
            response.EnsureSuccessStatusCode();
            _web.Source = new UrlWebViewSource { Url = server.Origin!.ToString() };
            _status.Text = "本机上传服务已就绪 · " + server.Origin + " · " +
                           (_enhanced ? "系统照片" : "MAUI 默认") + "选择器";
#if ANDROID
            Android.Util.Log.Info("PhotoLab", "READY " + server.Origin);
#endif
        }
        catch (Exception error)
        {
            _status.Text = "无法启动测试服务：" + error.Message;
#if ANDROID
            Android.Util.Log.Error("PhotoLab", error.ToString());
#endif
        }
        finally { _starting = false; }
    }
}
