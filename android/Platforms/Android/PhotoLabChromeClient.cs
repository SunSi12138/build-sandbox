using Android.App;
using Android.Content;
using Android.Provider;
using Android.Webkit;
using Microsoft.Maui;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Storage;
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
    private const int MaximumPhotos = 10;
    private bool _pendingMultiple;

    public override bool OnShowFileChooser(AWebView webView, IValueCallback callback, FileChooserParams options)
    {
        // The HTML page already exposes separate "Gallery" and "Camera" buttons.
        // Do not show a custom MAUI action sheet between the user and the native picker.
        // FileChooserParams.MODE_OPEN_MULTIPLE has the stable Android framework value 1.
        var multiple = Convert.ToInt32(options.Mode) == 1 && !options.IsCaptureEnabled;
        Android.Util.Log.Info("PhotoLab", "OnShowFileChooser capture=" + options.IsCaptureEnabled + " multi=" + multiple);
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try
            {
                if (options.IsCaptureEnabled)
                {
                    _pendingMultiple = false;
                    var root = System.IO.Path.Combine(FileSystem.CacheDirectory, "photo-lab-camera");
                    Directory.CreateDirectory(root);
                    var path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N") + ".jpg");
                    var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                        ?? throw new InvalidOperationException("Android Activity 不可用");
                    _cameraUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                        activity, activity.PackageName + ".files", new Java.IO.File(path));
                    var capture = new Intent(MediaStore.ActionImageCapture);
                    capture.PutExtra(MediaStore.ExtraOutput, _cameraUri);
                    capture.ClipData = ClipData.NewRawUri("photo", _cameraUri);
                    capture.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                    if (!ChooseFile(callback, capture, "系统相机"))
                        throw new InvalidOperationException("系统相机无法启动");
                    Android.Util.Log.Info("PhotoLab", "camera intent launched");
                }
                else
                {
                    _cameraUri = null;
                    _pendingMultiple = multiple;
                    Intent picker;
                    if (OperatingSystem.IsAndroidVersionAtLeast(33))
                    {
                        // Android 13+ framework Photo Picker: genuine system photos UI.
                        picker = new Intent(MediaStore.ActionPickImages);
                        picker.SetType("image/*");
                        if (multiple) picker.PutExtra(MediaStore.ExtraPickImagesMax, MaximumPhotos);
                    }
                    else
                    {
                        // Older Android: native DocumentsUI rather than custom gallery.
                        picker = new Intent(Intent.ActionOpenDocument);
                        picker.SetType("image/*");
                        picker.AddCategory(Intent.CategoryOpenable);
                        if (multiple) picker.PutExtra(Intent.ExtraAllowMultiple, true);
                    }
                    picker.AddFlags(ActivityFlags.GrantReadUriPermission);
                    if (!ChooseFile(callback, picker, "系统相册"))
                        throw new InvalidOperationException("系统照片选择器无法启动");
                    Android.Util.Log.Info("PhotoLab", "native picker launched multi=" + multiple);
                }
            }
            catch (System.Exception ex)
            {
                _cameraUri = null;
                _pendingMultiple = false;
                Android.Util.Log.Error("PhotoLab", "chooser error: " + ex);
                callback.OnReceiveValue(null);
                await page.DisplayAlertAsync("无法选择照片", ex.Message, "确定");
            }
        });
        return true;
    }

    protected override Java.Lang.Object ParseResult(Result resultCode, Intent data)
    {
        var camera = _cameraUri;
        _cameraUri = null;
        var multiple = _pendingMultiple;
        _pendingMultiple = false;
        if (resultCode != Result.Ok)
        {
            Android.Util.Log.Info("PhotoLab", "picker cancelled");
            return base.ParseResult(resultCode, data);
        }
        if (camera is not null)
        {
            Android.Util.Log.Info("PhotoLab", "camera returned one content URI");
            return Java.Lang.Object.FromArray(new[] { camera });
        }

        // Default WebView parseResult is not reliable for multiple selections.
        // ACTION_PICK_IMAGES and ACTION_OPEN_DOCUMENT return selected URIs in ClipData.
        var uris = new List<Android.Net.Uri>();
        if (data?.ClipData is ClipData selected)
        {
            for (var i = 0; i < selected.ItemCount; i++)
            {
                var uri = selected.GetItemAt(i)?.Uri;
                if (uri is not null && uri.Scheme == "content" && !uris.Any(x => x.Equals(uri)))
                    uris.Add(uri);
            }
        }
        if (uris.Count == 0 && data?.Data is Android.Net.Uri single &&
            single.Scheme == "content")
            uris.Add(single);
        if (!multiple && uris.Count > 1) uris.RemoveRange(1, uris.Count - 1);
        if (uris.Count > MaximumPhotos) uris.RemoveRange(MaximumPhotos, uris.Count - MaximumPhotos);
        Android.Util.Log.Info("PhotoLab", "native picker returned " + uris.Count + " content URI(s)");
        if (uris.Count == 0) return base.ParseResult(resultCode, data);
        return Java.Lang.Object.FromArray(uris.ToArray());
    }
}
