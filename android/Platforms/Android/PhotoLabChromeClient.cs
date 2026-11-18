using Android.App;
using Android.Content;
using Android.Provider;
using Android.Webkit;
using Microsoft.Maui;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;
using Java.Lang;
using AWebView = Android.Webkit.WebView;

namespace StreetEcoTool.Mobile;

/// <summary>
/// Explicit test implementation. MAUI's default MauiWebChromeClient already supports
/// FileChooserParams.CreateIntent (gallery). This extension additionally provides
/// a camera capture source backed by an AndroidX FileProvider content URI.
/// </summary>
internal sealed class PhotoLabChromeClient(IWebViewHandler handler, Page page) : MauiWebChromeClient(handler)
{
    private Android.Net.Uri? _cameraUri;

    public override bool OnShowFileChooser(AWebView webView, IValueCallback callback, FileChooserParams options)
    {
        Android.Util.Log.Info("PhotoLab", "OnShowFileChooser accept=" + string.Join(",", options.AcceptTypes ?? []));
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                var choice = options.IsCaptureEnabled
                    ? "拍照"
                    : await page.DisplayActionSheetAsync("选择照片来源", "取消", null, "从相册选择", "拍照");
                if (choice == "拍照")
                {
                    var root = System.IO.Path.Combine(FileSystem.CacheDirectory, "photo-lab-camera");
                    Directory.CreateDirectory(root);
                    var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg");
                    var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                    if (activity is null) throw new InvalidOperationException("Android Activity 不可用");
                    _cameraUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                        activity, activity.PackageName + ".files", new Java.IO.File(path));
                    var capture = new Intent(MediaStore.ActionImageCapture);
                    capture.PutExtra(MediaStore.ExtraOutput, _cameraUri);
                    capture.SetClipData(ClipData.NewRawUri("photo", _cameraUri));
                    capture.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                    if (!ChooseFile(callback, capture, "拍照"))
                        throw new InvalidOperationException("系统相机无法启动");
                    Android.Util.Log.Info("PhotoLab", "camera intent launched");
                }
                else if (choice == "从相册选择")
                {
                    _cameraUri = null;
                    if (!ChooseFile(callback, options.CreateIntent(), "从相册选择"))
                        throw new InvalidOperationException("系统相册选择器无法启动");
                    Android.Util.Log.Info("PhotoLab", "gallery chooser launched");
                }
                else
                {
                    _cameraUri = null;
                    callback.OnReceiveValue(null);
                    Android.Util.Log.Info("PhotoLab", "chooser cancelled");
                }
            }
            catch (Exception ex)
            {
                _cameraUri = null;
                Android.Util.Log.Error("PhotoLab", "chooser error: " + ex);
                callback.OnReceiveValue(null);
                await page.DisplayAlertAsync("无法选择照片", ex.Message, "确定");
            }
        });
        return true;
    }

    protected override Java.Lang.Object ParseResult(Result resultCode, Intent data)
    {
        var pendingCamera = _cameraUri;
        _cameraUri = null;
        if (resultCode == Result.Ok && pendingCamera is not null)
        {
            Android.Util.Log.Info("PhotoLab", "camera returned content Uri");
            return Java.Lang.Object.FromArray(new[] { pendingCamera });
        }
        Android.Util.Log.Info("PhotoLab", "picker result=" + resultCode);
        return base.ParseResult(resultCode, data);
    }
}
