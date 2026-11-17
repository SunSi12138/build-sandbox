# 环保排查 Android / .NET 11 CoreCLR 实验版

此目录位于独立分支 `maui-android-net11-coreclr`；未修改 `hbtool` 生产分支。

## 技术范围

- .NET SDK **11.0.100**、`net11.0-android`、.NET MAUI 11.0.10。
- Android **ARM64**（REDMI K90 Pro Max / 澎湃 OS 4 等）。
- **CoreCLR**，明确 `UseMonoRuntime=false`、`PublishAot=false`。
- Android 原生 WebView 显示现有 Nuxt SPA；先连接电脑/服务器运行的 `hbtool`。
- 不在手机内启动 Kestrel/ASP.NET Core；**不是独立离线后端**，不会在手机中创建 SQLite 数据库或直接读取手机内部 PMTiles。
- 该实验 APK 并不替代 Windows/Linux 端程序；保留原有数据库和地图文件不变。

## 构建 APK

GitHub Actions：`.github/workflows/android-coreclr.yml`。运行并在 Artifacts 下载 `android-coreclr-arm64`，其中是 `StreetEcoTool-Mobile-net11-arm64.apk`。
SDK、Android 工作负载、JDK21 和 Android SDK 均在 CI 中安装；APK 使用每次运行临时生成的测试签名，**不同 CI 构建不能直接覆盖安装**，更新实验版前先卸载旧版。卸载会删除手机内存储的“服务器地址”，但**不会删除电脑端数据库、图片和 PMTiles**。

本地编译（已安装 .NET 11、JDK21、MAUI Android 工作负载及 Android SDK）：

```bash
cd android
dotnet workload install maui-android
dotnet publish StreetEcoTool.Mobile.csproj -c Release -f net11.0-android -r android-arm64 -p:AndroidPackageFormat=apk
```

## 在澎湃 OS 4 上安装

1. 手机下载 `StreetEcoTool-Mobile-net11-arm64.apk`。
2. 用「文件管理」打开安装包，按系统提示允许该文件管理器「安装未知应用」（设置位置因系统版本略有不同），确认安装。
3. 让手机和 Windows/Linux 后端电脑位于同一个可信 Wi-Fi；避免公共 Wi-Fi 与访客隔离网络。
4. 电脑启动原版 `StreetEcoTool-win-x64.exe`，在首页监听地址设置中启用 **所有网卡（0.0.0.0）**，确认 Windows 防火墙仅在可信**专用网络**允许程序端口（默认 5180）。
5. 在电脑命令行运行 `ipconfig` 找到无线/有线网卡 IPv4 地址，例如 `192.168.1.20`。注意 **127.0.0.1 是设备自己，手机不能用这个地址访问电脑**。
6. 启动 Android APK，输入 `192.168.1.20:5180`，点击「连接现有应用」。手机保存地址，以后可在顶部「设置地址」修改。
7. 成功后在 WebView 看到与电脑端相同的页面、点位、地图、文档和其他数据。首次加载需要局域网连通；离开网络将无法使用本实验 APK。

如果连接不上：用手机浏览器先访问 `http://192.168.1.20:5180/api/system`。无法访问一般是电脑没开启局域网监听、防火墙阻挡、手机和电脑不同子网，或 Wi-Fi 客户端隔离。

**安全提醒：** 原后端为局域网服务，不提供互联网级的鉴权/HTTPS。仅在可信局域网内使用，不做端口映射到公网。

## 现有 PMTiles/PBF 怎么使用

**使用电脑端已有文件**（保持零数据库迁移）：

1. 在运行后端的电脑，把文件放在地图缓存根目录下，例如 `F:\\环保排查\\tiles\\vector_label.pmtiles`。
2. 把矢量样式放在电脑的 `F:\\环保排查\\style\\A\\`，其中有 `style.json`、`font/`、`sprite/`。如果仅希望覆盖卫星影像的文字，不要在样式中启用不透明背景/填充图层。
3. 在**电脑端或 Android WebView 里的地图图层设置**中选择 PMTiles，文件路径填写 `vector_label.pmtiles`（相对于服务端 `tiles` 根目录），样式目录填写 `F:\\环保排查\\style\\A`。
4. 启用底层卫星影像和上层透明标注图层，在 Android WebView 中打开地图。瓦片及字体/精灵由**电脑端的本地服务**提供，手机通过 Wi-Fi 读取。
5. 普通栅格加载深度可选 0–3；矢量 PMTiles 使用保持比例的渲染及更高层级预取。
6. **不支持手机直接选取一个 PMTiles 文件用于完全离线查看。** 如果把 `vector_label.pmtiles` 复制到手机「下载」目录，本实验版不会自动识别；这是后续 Android 本地后端/导入器的工作。

## 为什么没有把 Windows EXE 直接嵌入 APK？

原工程的后端是 `net10.0` 的 ASP.NET Core WebApplication + Kestrel + SQLite（Windows/Linux NativeAOT），使用 `AppContext.BaseDirectory/data` 保存数据库，并依赖目录、文件权限、图片与备份。仅将目标框架切换为 `net11.0-android` 不会使上述服务和存储路径直接可用：Android 应用只能在沙箱内持久化文件，服务端运行环境、Web 服务依赖及文件导入需要单独移植和验证。本实验版以无数据库迁移的局域网 WebView 客户端先验证 CoreCLR/MAUI/系统 WebView 的兼容性。

## 测试清单

- 编译出**签名可安装**的 arm64 APK。
- 检查 APK 包内有 `lib/arm64-v8a/libcoreclr.so`，而不是 Mono runtime。
- APK 在手机上手工验证：连接电脑 IPv4、WebView 地图/PMTiles、筛选与地图缩放、相机拍照与附件的 Android WebView 适配。CI 不等于已在 REDMI K90 Pro Max 真机实测。
