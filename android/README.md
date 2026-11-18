# 环保排查 Android 独立离线实验版（.NET 11 CoreCLR）

此实验仅在分支 `maui-android-net11-coreclr` 上开发，原 `hbtool` 不受影响。该版本不再需要 Windows/Linux 服务：应用内启动**仅监听 127.0.0.1 的轻量 HTTP 服务**，系统 Android WebView 展示打包在 APK 中的 Nuxt 前端。手机内部存储保存与电脑版**相同架构与 SchemaVersion 6**的 SQLite 数据库。

## 重要状态

- 实现：.NET SDK 11.0.100、MAUI 11、ARM64/CoreCLR（禁止 Mono）；共享服务器 SQLite Store 域逻辑、离线 API、打包前端、Android 文件选择器。
- 支持：手机本地数据的点位、排查、活动、事项等主要 REST API；本地 PMTiles v3、XYZ/TMS 瓦片、PBF 的字体和 Sprite；数据库与照片附件的完整备份恢复；备份导出。
- **限制：** Android 全部功能（尤其相机上传、图片缩略图、附件与 Word 文件下载）仍需真机验证和补齐；当前以**实验版**发布，不表示已经完成 REDMI 真机验收。
- 不访问电脑，不需要网络服务或 Wi-Fi。任何外部站点跳转在 WebView 内被阻止。应用的本机 HTTP 服务不对局域网开放。
- 手机数据存于 Android 应用沙箱，卸载应用会清除本地数据库、导入的 PMTiles 和样式文件。**卸载前务必导出备份；PMTiles 和样式 ZIP 也应另外留存**。

## APK 安装（REDMI K90 Pro Max / 澎湃 OS 4）

1. 打开 [GitHub Actions 的 Android .NET 11 CoreCLR MAUI APK 工作流](https://github.com/SunSi12138/build-sandbox/actions/workflows/android-coreclr.yml)，选择该实验分支的**最新成功**运行。
2. 下载 `android-coreclr-arm64` Artifact ZIP，解压并得到 `StreetEcoTool-Mobile-net11-arm64.apk`。
3. 在红米手机文件管理器中打开 APK；按系统提示允许文件管理器安装未知应用，并完成安装。
4. 首次启动，应用会在私有存储中创建数据库，然后自动展示打包的页面。**不填写电脑 IP、不启动桌面端、不连接 Wi-Fi**。
5. 不同 CI 运行使用临时测试签名。更新版本时可能需要卸载旧测试 APK；这样会清除手机私有数据，必须先备份。

## 导入既有电脑数据

1. 在电脑端 `hbtool` 的「备份」页**导出完整备份 ZIP**（不是直接复制 SQLite WAL 文件）。
2. 将备份 ZIP 通过 USB、手机文件管理器等复制到手机。
3. 在 Android 顶部工具条点「导入备份」，选中 ZIP，核对数量、时间后确认。
4. 应用使用原数据库的备份恢复验证逻辑，将数据库、照片、附件恢复到 Android 沙箱；原来的电脑数据库完全不变。
5. 地图文件通常未包含在完整数据备份中；**PMTiles 和样式需要单独导入**。

## 在手机上离线使用 PMTiles / PBF

1. 把 `vector_label.pmtiles` 或影像 `.pmtiles` 文件复制到手机（U 盘 OTG、USB、文件管理器均可）。
2. 在 APP 顶部点 **「导入 PMTiles」**，选择 `.pmtiles`；应用会校验 PMTiles v3 文件头，将整个文件复制到应用私有 `tiles` 目录，并自动创建一个本地图层。
3. 矢量 PMTiles 需要对应样式：把电脑版样式目录（包含 `style.json`、`font/`、`sprite/`）压缩成 ZIP，允许 ZIP 顶部带一层目录 `A/`。
4. 在 APP 点 **「导入样式 ZIP」**，选 ZIP，并选择刚导入的矢量图层。应用会安全解压到自己的 `tiles/styles/...`，将图层关联到这个样式。
5. 回到「地图图层设置」，按需启用图层、调整上下顺序。原来卫星图与透明地名标注可叠加；如果矢量样式含有背景、面填充，仍会遮住卫星底图。
6. 所有瓦片、字体、精灵文件均由 APP 本机私有存储提供，不需要联网，WebView 请求地址为 `127.0.0.1` 的随机端口。

其他本地 XYZ/TMS 瓦片目录可通过后续 Android 目录选择器或备份导入增强。**这份实验 APK 目前主要提供 PMTiles 文件和样式 ZIP 的导入入口。**

## 本地数据目录（不是手机外部可见目录）

- SQLite：`FileSystem.AppDataDirectory/data/app.db`
- 图片：`FileSystem.AppDataDirectory/photos`
- 附件：`FileSystem.AppDataDirectory/attachments`
- 地图：`FileSystem.AppDataDirectory/tiles`
- 备份/暂存：`FileSystem.AppDataDirectory/backup`、`temp`

不要尝试用 Windows 路径 `F:\\环保排查\\style\\A` 访问手机内容。手机路径在私有沙箱中，应用通过「导入样式 ZIP」自动设置相对目录。

## 构建 APK

项目：`android/StreetEcoTool.Mobile.csproj`，目标 `net11.0-android` / `android-arm64`，明确 `UseMonoRuntime=false` 和 `PublishAot=false`。

GitHub Actions 会首先用 Node 24 从 `web` 生成 Nuxt 静态页面，然后用 .NET 11、JDK 21、MAUI Android 工作负载编译、签名 ARM64 APK，检查 `libcoreclr.so` / `libclrjit.so` 是否存在，同时排除 Mono。只有工作流成功才代表此构建已完成**编译和 APK 包装检查**；不能替代手机真机启动、相机、地图大文件导入及完整业务流程测试。

## 技术细节

旧 ASP.NET Core 的 `Microsoft.AspNetCore.App` Android Runtime Pack 并不是默认提供的组件，因此实验版使用由 `TcpListener` 实现的 loopback-only HTTP 传输，而不是将 Kestrel 的 Linux 二进制打包进 Android。业务模型、SQLite 表结构与多数 Store 方法直接引用原项目源代码；不引入独立的 Android 数据库版本。若设备上运行失败，应保留异常信息并在实验分支修复，而不是在主分支更改数据格式。
