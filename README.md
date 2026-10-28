# 街道生态环保工具

本机运行的街道生态环保工作台，包含点位、排查、事项、活动、地图和设置。手机可通过可信局域网访问，无需安装 Node.js、.NET 或 Office。

## 下载与启动

打开本仓库 Actions 中 **Build and verify hbtool** 的成功运行，下载 `hbtool` 附件，再解压其中的 `hbtool.zip`。校验文件为 `hbtool.zip.sha256`。统一包只包含：

```text
hbtool/
  StreetEcoTool-win-x64.exe
  StreetEcoTool-linux-arm64
  wwwroot/
  README.md
  使用说明.md
  地图使用说明.txt
  map-providers.example.json
```

Windows 完整解压到可写目录，双击 `StreetEcoTool-win-x64.exe`。Linux ARM64：

```sh
chmod +x StreetEcoTool-linux-arm64
./StreetEcoTool-linux-arm64 --no-browser --listen 0.0.0.0
```

默认端口 `5180`，默认仅回环监听。有桌面环境时打开 `http://localhost:5180`。手机不能使用 `localhost`：先在运行程序的电脑上打开首页右上角监听设置，选择需要的网卡或所有网卡并应用；再在手机访问列出的 IPv4 地址，如 `http://192.168.1.100:5180`。

先在设置维护街道全称、落款单位、默认排查人、社区抬头及分类；随后建立点位或直接创建临时点位排查。试用示例可在全新目录加 `--seed-demo`；它只在业务空库中灌入示例，不要用正式数据试验恢复。

## 启动选项与监听

命令行优先于程序同目录 `appsettings.json`；配置优先于默认值：

```powershell
.\StreetEcoTool-win-x64.exe --port 5181
.\StreetEcoTool-win-x64.exe --listen 127.0.0.1 --listen 192.168.1.100
.\StreetEcoTool-win-x64.exe --listen 0.0.0.0 --no-browser
.\StreetEcoTool-win-x64.exe --seed-demo
```

可选配置（发布包不包含此文件，避免覆盖用户设置）：

```json
{"port":5180,"listen":["127.0.0.1","192.168.1.100"]}
```

`0.0.0.0` 与其他选项互斥。保存监听后在同一进程重新绑定，数据库和 Mutex 不变；远程请求不能修改监听，必须从运行电脑的回环地址操作。失效网卡跳过并提示，全部不可用时回退到本机。端口冲突会中文报错退出。

按 Ctrl+C 正常停机。

## 数据、备份及不支持的旧版本

当前数据库结构为 **schema 5**，同时包含地图位置快照、事项、标签、附件和统一活动。按新的整合版本部署，使用全新数据目录。程序目录布局：

```text
hbtool/
  data/app.db                # 运行时可能有 -wal / -shm
  photos/yyyyMM/             # 原图与 .thumb.jpg
  attachments/yyyyMM/        # 点位附件原文件
  backup/                   # auto-日期.db、before-restore-* 等
  temp/                     # 当前恢复日志、恢复暂存及临时导出
  appsettings.json          # 用户配置
```

所有路径相对可执行文件，不依赖工作目录。不支持的数据库或旧布局将保留文件并拒绝启动／恢复。本版 ZIP 清单为 `FormatVersion=1`，恢复时校验完整结构。

“设置 → 备份与恢复”下载完整 ZIP，内容含一致性数据库快照、所有登记的原图、缩略图和点位附件。恢复先预检并显示点位、排查、问题、活动、整治子集、事项、附件、标签和照片数量，确认覆盖后才执行；覆盖前保留旧数据，当前恢复日志支持中断回滚。恢复包必须来自当前支持的结构。

每日 00:00 保存前一天的数据库快照；缺失时在启动及跨日写入前补做；保留最近 14 份 `auto-日期.db`，**不含照片和附件**，不删除 `before-*`。超过七天仍未关联的照片会清理。浏览器草稿不包含在服务端备份中。

首次部署请解压到可写目录。**更新前下载完整备份并正常停机，再复制整个程序目录留作回退。** 更新时替换两平台程序和整份 `wwwroot`，保留 data、photos、attachments、backup、temp、tiles 和用户配置。回退使用更新前的整目录副本；当前完整备份恢复要求结构版本为5。

## 日常流程与导出

完整业务规则见[使用说明](docs/new-application.md)。

- 事项独立登记和办理，可关联排查、整改问题及活动；办理记录、回复、移交、办结和重新办理保留过程。
- 点位管理基本资料、照片、附件及相关工作记录。附件保存原文件，支持预览、下载、改名、备注和删除。
- 排查支持事项来源、问题分类和逐项事项关联，整改保留反馈、复查、退回及闭环。
- 活动统一承载普通活动和整治。整治是内置特殊模板，保留点位、整治类别、数量和前中后照片。
- 标签在设置统一管理，跨模块筛选和查看；停用标签保留历史，多个标签按同时满足筛选。
- 列表导出默认沿用当前筛选，覆盖全部匹配记录，也可自定义范围。九种DOCX：排查记录单、整改通知单、整改记录单、整治记录单、排查台账、整改台账、整治台账、活动记录单、活动台账。

照片在浏览器转JPEG、去除EXIF并压缩到长边不超过1600，原图上限10MiB；点位附件单文件上限25MiB，保持原始字节。静态样式、图标、图片处理WASM随页面本地供应。

通知单支持 `{日期}`、`{点位}`、`{问题}`、`{期限}`、`{街道全称}`、`{社区}`。`{问题}` 为问题表格，最多一次；未填写时在正文后补表。

## 地图

点位和排查的列表与地图共用筛选条件。点位支持选点、画区域；排查保存当时的位置，不随点位后续移动。操作见[地图使用说明](docs/地图使用说明.txt)。

底图使用吉林一号卫星图和天地图地名标注。复制 `map-providers.example.json` 为 `map-providers.local.json`，填写密钥后放在程序旁并重启；天地图使用服务端类型 Key。

浏览新范围时联网下载，已缓存范围从 `tiles/` 加载。完整业务备份不包含地图缓存和服务配置，搬迁时另行复制 `tiles/` 和 `map-providers.local.json`。

Windows 本机构建可用 `scripts/build-map.ps1`，会携带本地地图配置和缓存。该脚本生成的配置使用端口5181；统一ZIP包默认端口5180。

## 手机访问、网络与开机自启

本工具无登录保护，仅用于可信网络。

Windows 防火墙由管理员放行实际端口，仅限所需可信网络。例如管理员 PowerShell：

```powershell
New-NetFirewallRule -DisplayName "街道环保 TCP 5180" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5180 -Profile Private,Domain
```

优先在路由器为电脑网卡设置 DHCP 地址保留；否则请网络管理员分配固定 IP，避免冲突。访客 Wi-Fi 隔离、电脑休眠、未选监听网卡和虚拟网卡防火墙均可能导致手机无法访问。不要以关闭整台防火墙解决。

Windows：为程序创建快捷方式放进 `Win+R → shell:startup`（登录后启动），或用任务计划程序设开机启动。无桌面启动加 `--no-browser`；工作目录可以填写程序目录，数据路径仍由程序自身定位。

Linux systemd（先创建专用 `hbtool` 用户，目录归该用户；按实际目录调整）：

```ini
[Unit]
Description=Street Eco Tool
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
User=hbtool
Group=hbtool
WorkingDirectory=/opt/hbtool
Environment=TZ=Asia/Shanghai
ExecStart=/opt/hbtool/StreetEcoTool-linux-arm64 --no-browser --listen 0.0.0.0
Restart=on-failure
RestartSec=5
TimeoutStopSec=45
[Install]
WantedBy=multi-user.target
```

保存到 `/etc/systemd/system/street-eco-tool.service` 后：

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now street-eco-tool
journalctl -u street-eco-tool -n 80 --no-pager
```

安装 tzdata 并核对本地时区；所有业务日期和快照以服务器本地时间计算。修改服务或更新程序前正常停机。

## 平台和浏览器

Windows x64 在 Windows Server 2022 x64 构建与运行验证；不支持 Windows7/8.1。Linux ARM64 在 Ubuntu20.04容器中原生构建，打包检查要求最高 GLIBC 符号不超过2.31；不支持 Alpine/musl。linux-x64只用于测试，不进入统一发布包。

浏览器：Chrome/Edge111+、Safari/iOS16.4+、Firefox128+。

Word导出采用A4页面和固定字体，设备缺少字体时会自动替换。

## 开发、构建与回归

使用 global.json 指定的.NET SDK及Node.js 24.15+。开发：

```sh
dotnet run --project server/StreetEcoTool.csproj -p:PublishAot=false -- --no-browser
# 另一个终端
npm --prefix web ci
npm --prefix web run dev
```

Nuxt开发代理默认后端5180；生产仅 `nuxt generate` 的 `.output/public`。常规回归（Node.js 24.15+）：

```sh
dotnet test server/StreetEcoTool.Tests/StreetEcoTool.Tests.csproj -c Release -p:PublishAot=false -warnaserror
npm --prefix web ci
npm --prefix web run typecheck
npm --prefix web run generate
# 对应原生宿主，Windows需x64 MSVC，Linux需clang、zlib开发包
dotnet publish server/StreetEcoTool.csproj -c Release -r <RID> -o publish/<RID> -p:PublishAot=true -p:SelfContained=true -warnaserror
python3 tests/api-smoke.py --executable publish/<RID>/StreetEcoTool --report test-results/api.json
python3 tests/word-smoke.py --executable publish/<RID>/StreetEcoTool --report test-results/word.json
# 把静态产物复制到可执行文件旁wwwroot；Windows替换exe名
cd web
npx playwright install --with-deps chromium
node browser-test.mjs --executable ../publish/<RID>/StreetEcoTool
```

`tests/word-layout-samples.py` 用真实API生成当前模板和边界样本；`tests/render-word.py` 固定本地字体配置，生成PDF、逐页PNG、哈希和人工审阅清单。CI针对当前新结构生成样本并使用固定字体渲染，浏览器／文档步骤失败时不组装最终包。Pillow、LibreOffice及Poppler只用于测试，不放入生产程序。

统一打包：

```sh
python3 scripts/package.py --win-x64 publish/win-x64/StreetEcoTool.exe --linux-arm64 publish/linux-arm64/StreetEcoTool --web-static web/.output/public --output dist
```

输出 `dist/hbtool.zip` 与校验文件。

## 常见问题

手机连不上：核对实际监听网卡、IP、端口、防火墙、Wi-Fi隔离与电脑休眠。端口占用：正常关闭原实例或改端口并更新防火墙／书签。照片失败：核对可解码格式、10MiB上限、网络、磁盘和写权限，重试或确认移除失败任务。更新后白屏：替换完整wwwroot并刷新，不能混用两版hash资源。旧库拒绝：本版不兼容旧结构，请使用独立新目录，原目录保留供旧程序使用。
