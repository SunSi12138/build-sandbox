using System.IO.Compression;
using System.Text;
using StreetEcoTool;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace StreetEcoTool.Mobile;

/// <summary>The entire app runs on this phone: local SQLite, web assets and PMTiles.</summary>
public sealed class OfflineHomePage : ContentPage
{
    private readonly Label _status;
    private readonly WebView _web;
    private readonly Grid _root;
    private OfflineHttpServer? _server;
    private bool _initializing;

    public OfflineHomePage()
    {
        Title = "环保排查 · 本机离线";
        BackgroundColor = Colors.White;

        _status = new Label
        {
            Text = "正在准备手机本地数据库……",
            TextColor = Colors.DarkSlateGray, FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation
        };
        _web = new WebView
        {
            AutomationId = "OfflineWebView",
            VerticalOptions = LayoutOptions.Fill, HorizontalOptions = LayoutOptions.Fill
        };
        _web.Navigated += (_, e) =>
        {
            if (e.Result != WebNavigationResult.Success)
                _status.Text = "本地页面加载失败，请检查应用存储空间。";
        };
        _web.Navigating += (_, e) =>
        {
            if (_server?.Address is not Uri allowed ||
                !Uri.TryCreate(e.Url, UriKind.Absolute, out var next) ||
                next.Host != allowed.Host || next.Port != allowed.Port ||
                next.Scheme != allowed.Scheme)
                e.Cancel = true;
        };

        var refresh = ToolButton("刷新", () => _web.Reload());
        var importTiles = ToolButton("导入 PMTiles", () => _ = ImportPmtilesAsync());
        var importStyle = ToolButton("导入样式 ZIP", () => _ = ImportStyleAsync());
        var restoreBackup = ToolButton("导入备份", () => _ = ImportBackupAsync());
        var shareBackup = ToolButton("导出备份", () => _ = ShareBackupAsync());

        var tools = new HorizontalStackLayout
        {
            Padding = new Thickness(5, 1),
            Spacing = 3,
            Children = { importTiles, importStyle, restoreBackup, shareBackup, refresh }
        };
        var toolbar = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)
            },
            BackgroundColor = Color.FromArgb("#EDF6F0")
        };
        toolbar.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Never }, 0, 0);
        toolbar.Add(_status, 0, 1);
        _status.Margin = new Thickness(10, 0, 10, 6);

        _root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }
        };
        _root.Add(toolbar, 0, 0);
        _root.Add(_web, 0, 1);
        Content = _root;
    }

    private static Button ToolButton(string label, Action action)
    {
        var button = new Button
        {
            Text = label, FontSize = 11,
            Padding = new Thickness(7, 3),
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#075E54"),
            MinimumHeightRequest = 36
        };
        button.Clicked += (_, _) => action();
        return button;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_server is not null || _initializing) return;
        _initializing = true;
        try
        {
            var storage = FileSystem.AppDataDirectory;
            var server = await Task.Run(() =>
            {
                var running = new OfflineHttpServer(storage);
                running.Start();
                return running;
            });
            _server = server;
            // A successful APK build does not prove that SQLite and the bundled
            // Nuxt assets can actually be served on a particular Android device.
            // Probe them before opening WebView so startup failures are visible.
            using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
            {
                using var health = await probe.GetAsync(new Uri(server.Address!, "api/health"));
                health.EnsureSuccessStatusCode();
                using var bootstrap = await probe.GetAsync(new Uri(server.Address!, "api/bootstrap"));
                bootstrap.EnsureSuccessStatusCode();
                var json = await bootstrap.Content.ReadAsStringAsync();
                if (!json.Contains("\"communities\"", StringComparison.Ordinal))
                    throw new InvalidDataException("本机数据库接口未返回有效数据。");
                using var home = await probe.GetAsync(server.Address);
                home.EnsureSuccessStatusCode();
                if (home.Content.Headers.ContentType?.MediaType?.Equals("text/html", StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidDataException("APK 内前端主页不存在。");
            }
#if ANDROID
            Android.Util.Log.Info("StreetEcoOffline", "READY " + server.Address);
#endif
            _status.Text = "本机数据库已就绪 · 完全离线";
            _web.Source = new UrlWebViewSource { Url = server.Address!.ToString() };
        }
        catch (Exception e)
        {
            _status.Text = "无法创建本机数据库：" + e.Message;
        }
        finally { _initializing = false; }
    }

    private async Task ImportPmtilesAsync()
    {
        if (_server is null) return;
        try
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择手机中的 .pmtiles 文件"
            });
            if (picked is null) return;
            if (!picked.FileName.EndsWith(".pmtiles", StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlertAsync("文件格式", "请选择 .pmtiles 文件。", "确定");
                return;
            }
            var name = Path.GetFileNameWithoutExtension(picked.FileName);
            var dest = "import-" + Guid.NewGuid().ToString("N")[..12] + ".pmtiles";
            var root = _server.Data.OfflineTilesRoot;
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, dest);
            var staging = path + ".part";
            try
            {
                _status.Text = "正在复制 PMTiles 到应用内部存储……";
                await using (var input = await picked.OpenReadAsync())
                await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                    await input.CopyToAsync(output);
                await using (var check = File.OpenRead(staging))
                {
                    var header = new byte[8];
                    await check.ReadExactlyAsync(header);
                    if (Encoding.ASCII.GetString(header, 0, 7) != "PMTiles" || header[7] != 3)
                        throw new InvalidDataException("需要 PMTiles v3 文件。");
                }
                File.Move(staging, path);
                _server.Data.CreateMapProvider(new TileProvider("",
                    dest, "xyz", Name: name, Format: "pmtiles"));
                _status.Text = "已导入 " + picked.FileName + "（" + new FileInfo(path).Length / 1048576 + " MB）";
                _web.Reload();
                await DisplayAlertAsync("PMTiles 已导入",
                    "文件已存入手机内部存储并创建地图图层。矢量 PMTiles 如未显示文字，请继续导入对应样式 ZIP。", "确定");
            }
            catch
            {
                if (File.Exists(staging)) File.Delete(staging);
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }
        catch (Exception e) { await DisplayAlertAsync("导入失败", e.Message, "确定"); }
    }

    private async Task ImportStyleAsync()
    {
        if (_server is null) return;
        try
        {
            var picked = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "选择包含 style.json、font 和 sprite 的 ZIP" });
            if (picked is null) return;
            if (!picked.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择 .zip 文件。");

            var targets = _server.Data.GetMapSettings().Providers.Where(p => p.Format == "pmtiles").ToArray();
            if (targets.Length == 0) throw new InvalidDataException("请先导入一个 PMTiles 图层。");
            var labels = targets.Select(p => p.Name + " [" + p.Id[..Math.Min(6, p.Id.Length)] + "]").ToArray();
            var selected = targets.Length == 1 ? labels[0] :
                await DisplayActionSheetAsync("将样式应用到哪个图层？", "取消", null, labels);
            var index = Array.IndexOf(labels, selected);
            if (index < 0) return;
            var target = targets[index];

            var styleId = "styles/" + Guid.NewGuid().ToString("N")[..12];
            var path = Path.Combine(_server.Data.OfflineTilesRoot, styleId.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            try
            {
                await using var input = await picked.OpenReadAsync();
                using var archive = new ZipArchive(input, ZipArchiveMode.Read);
                var styles = archive.Entries.Where(e => e.Name.Equals("style.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (styles.Length != 1) throw new InvalidDataException("ZIP 必须包含且只能包含一个 style.json。");
                var prefix = styles[0].FullName[..^"style.json".Length];
                long total = 0;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0 || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var relative = entry.FullName[prefix.Length..].Replace('\\', '/');
                    var parts = relative.Split('/');
                    if (parts.Any(part => part is "" or "." or ".." || part.Contains(':')) ||
                        (parts[0] is not ("style.json" or "font" or "sprite")))
                        throw new InvalidDataException("样式 ZIP 包含非法路径。");
                    total += entry.Length;
                    if (total > 512L * 1024 * 1024) throw new InvalidDataException("样式资源不能超过 512 MB。");
                    var local = Path.GetFullPath(Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!local.StartsWith(Path.GetFullPath(path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("样式 ZIP 路径越界。");
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    await using var from = entry.Open();
                    await using var output = File.Create(local);
                    await from.CopyToAsync(output);
                }
                if (!File.Exists(Path.Combine(path, "style.json")))
                    throw new InvalidDataException("样式目录缺少 style.json。");
                _server.Data.UpdateOfflineMapStyle(target.Id, styleId);
                _status.Text = "已导入样式 " + picked.FileName;
                _web.Reload();
                await DisplayAlertAsync("样式已导入", "已经为图层「" + target.Name + "」设置本地样式。", "确定");
            }
            catch
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                throw;
            }
        }
        catch (Exception e) { await DisplayAlertAsync("导入失败", e.Message, "确定"); }
    }

    private async Task ImportBackupAsync()
    {
        if (_server is null) return;
        try
        {
            var selected = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择从电脑 hbtool 导出的完整备份 ZIP"
            });
            if (selected is null) return;
            if (!selected.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择完整备份 ZIP。");
            await using var source = await selected.OpenReadAsync();
            _status.Text = "正在校验备份 ZIP……";
            var preview = await _server.Data.PrepareRestoreAsync(source, CancellationToken.None);
            var confirmed = await DisplayAlertAsync("确认恢复",
                $"备份时间：{preview.CreatedAt}\n点位：{preview.PointCount}，排查：{preview.InspectionCount}，问题：{preview.IssueCount}\n\n此操作将替换手机当前数据库、照片和附件。恢复前请先导出手机备份。是否继续？",
                "确认覆盖手机数据", "取消");
            if (!confirmed)
            {
                _status.Text = "已取消备份恢复";
                return;
            }
            _server.Data.Restore(new RestoreRequest(preview.Token, true));
            _status.Text = "电脑备份已恢复到手机本地数据库";
            _web.Reload();
            await DisplayAlertAsync("恢复成功", "SQLite、照片及附件已恢复。手机现在可以离线使用这些数据。地图 PMTiles 和样式文件请另外导入。", "确定");
        }
        catch (Exception e)
        {
            _status.Text = "备份导入失败";
            await DisplayAlertAsync("恢复失败", e.Message, "确定");
        }
    }

    private async Task ShareBackupAsync()
    {
        if (_server is null) return;
        try
        {
            var backup = await Task.Run(() => _server.Data.CreateBackupZip());
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "导出手机完整数据库与文件备份",
                File = new ShareFile(backup, "application/zip")
            });
        }
        catch (Exception e) { await DisplayAlertAsync("备份失败", e.Message, "确定"); }
    }
}
