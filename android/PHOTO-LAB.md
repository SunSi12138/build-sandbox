# MAUI WebView 照片上传实验

分支：`maui-webview-photo-lab`。本分支从 `maui-android-net11-coreclr` 最新提交单独创建，不修改原 Android 或上游 `hbtool`。

**用途只有一件事：排查 MAUI Android WebView 上传照片的问题。**

- 独立应用包名：`com.streetecotool.photolab`，可与原「环保排查」并排安装。
- .NET 11、MAUI 11、ARM64 CoreCLR；不需要联网或运行电脑后端。
- **不包含业务数据库和 Nuxt 页面，不会读写原应用的照片、地图或 SQLite。**
- 独立测试页面通过 Android WebView 访问 `http://127.0.0.1:随机端口/`。
- 两种选择器可切换：默认 MAUI `MauiWebChromeClient`、增强 `PhotoLabChromeClient`（提供系统相册/文件选择与系统相机拍照）。
- 原 HTML 直接使用 `<input type="file" accept="image/*" multiple>` 和 `<input type="file" accept="image/*" capture="environment">`。
- 可测试直接上传原图片，以及使用 `createImageBitmap` / Canvas 转 JPEG 后再上传。
- 本地 HTTP 上传终点：`PUT /api/upload`，返回文件大小、MIME 和 SHA-256；测试文件保存在本应用**临时缓存目录**，不会进入原应用。
- 用于定位真实失败阶段：Android 文件选择回调 → HTML `change` → 图片解码 → HTTP PUT → C# 保存响应。

## 安装

进入 [Photo Lab 构建工作流](https://github.com/SunSi12138/build-sandbox/actions/workflows/android-photo-lab.yml)，打开该分支最新成功的运行，从 Artifacts 下载 `maui-webview-photo-lab-arm64`，解压出 `MAUI-WebView-Photo-Lab-arm64.apk`。

在澎湃 OS 4 的文件管理中打开 APK，允许该文件管理器安装未知应用后完成安装。因为应用包名与正式版不同，可以并排安装。

这是每次 CI 临时签名的实验包；不同构建之间可能需要先卸载旧 Photo Lab 应用。

## 测试顺序

1. 打开 APP，确认顶部出现 `本机上传服务已就绪`，网页出现 `本地HTTP服务` 日志。
2. 选择 **增强选择器**，点击「从相册选择」，选取 JPEG 或 PNG。应依次出现「文件选择回调」「预览成功」。
3. 点击「直接上传原图片」，应显示 `HTTP响应 200` 和 SHA-256。
4. 点击「Canvas 转 JPEG 再上传」，应显示「解码成功」「JPEG 转换成功」「HTTP响应 200」。
5. 点击「拍照（capture）」，调用系统相机，拍照确认后应进入同样的上传流程。
6. 切到 **MAUI 默认选择器** 重复相册选择与上传，比较两者的差异。
7. 如果失败，点击「复制日志」粘贴给开发者，说明是哪一步失败，以及是否出现了原生选择器。

**诊断意义：**

- 原生选取器不出现：WebChromeClient/Activity 对接问题。
- 原生选取器出现、回到 WebView 没有「文件选择回调」：返回 URI、FileProvider、结果回调问题。
- 「预览失败」/「解码失败」：照片编码或 WebView Canvas 解码问题。
- HTTP 不是 200：本机 HTTP 请求、文件 MIME、超时或服务器处理问题。

## 实现边界

Android 项目从原实验版分支复制后，在本分支中禁用了所有业务模块编译，包括离线 SQLite Store 和 Nuxt Web 资源。测试照片只存到 Photo Lab 缓存目录，可能随 Android 清理缓存或卸载而消失。

调用系统相机使用 Android ACTION_IMAGE_CAPTURE、FileProvider `content://`，不需要声明任意存储权限或访问手机完整媒体库；相册通过 Android 系统文件选择器选取，用户自行授权选中的文件。拍照行为仍需 REDMI K90 Pro Max / 澎湃 OS 4 真机验证，模拟器验证不能替代真实系统相机。
