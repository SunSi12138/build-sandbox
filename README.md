# 街道生态环保工具

分支：`hbtool`。本机部署，手机通过可信局域网或蒲公英组网访问；五个入口为首页、点位、排查、整治、设置。运行时无需外网、Node.js、.NET Runtime 或 Office。没有登录、口令及权限体系，不要暴露到公网。

## 下载与启动

打开本仓库 Actions 中 **Build and verify hbtool** 的成功运行，下载 `hbtool` 附件，再解压其中的 `hbtool.zip`。校验文件为 `hbtool.zip.sha256`。统一包只包含：

```text
hbtool/
  StreetEcoTool-win-x86.exe
  StreetEcoTool-linux-arm64
  wwwroot/
```

Windows 完整解压到可写目录，双击 `StreetEcoTool-win-x86.exe`。Linux ARM64：

```sh
chmod +x StreetEcoTool-linux-arm64
./StreetEcoTool-linux-arm64 --no-browser --listen 0.0.0.0
```

默认端口 `5180`，默认仅回环监听。有桌面环境时打开 `http://localhost:5180`。手机不能使用 `localhost`：先在运行程序的电脑上打开首页右上角监听设置，选择需要的网卡或所有网卡并应用；再在手机访问列出的 IPv4 地址，如 `http://192.168.1.100:5180`。

先在设置维护街道全称、落款单位、默认排查人、社区抬头及分类；随后建立点位或直接创建临时点位排查。试用示例可在全新目录加 `--seed-demo`；它只在业务空库中灌入示例，不要用正式数据试验恢复。

## 启动选项与监听

命令行优先于程序同目录 `appsettings.json`；配置优先于默认值：

```powershell
.\StreetEcoTool-win-x86.exe --port 5181
.\StreetEcoTool-win-x86.exe --listen 127.0.0.1 --listen 192.168.1.100
.\StreetEcoTool-win-x86.exe --listen 0.0.0.0 --no-browser
.\StreetEcoTool-win-x86.exe --seed-demo
```

可选配置（发布包不包含此文件，避免覆盖用户设置）：

```json
{"port":5180,"listen":["127.0.0.1","192.168.1.100"]}
```

`0.0.0.0` 与其他选项互斥。保存监听后在同一进程重新绑定，数据库和 Mutex 不变；远程请求不能修改监听，必须从运行电脑的回环地址操作。失效网卡跳过并提示，全部不可用时回退到本机。端口冲突会中文报错退出。

默认框架日志最低级别为 **Warning**，保留 Error/Critical。启动地址、目录、时区和关键配置提醒仍直接输出到控制台。命令行 `--no-browser` 禁止自动开浏览器；第二实例不另起服务。Ctrl+C、SIGTERM 或正常关闭会释放连接。

## 数据、备份及不支持的旧版本

当前数据库结构仍为 **schema 2**，没有为了清理代码升级或重置当前库。新库直接创建当前结构；已有当前库原样打开。程序目录布局：

```text
hbtool/
  data/app.db                # 运行时可能有 -wal / -shm
  photos/yyyyMM/             # 原图与 .thumb.jpg
  backup/                   # auto-日期.db、before-restore-* 等
  temp/                     # 当前恢复日志、恢复暂存及临时导出
  appsettings.json          # 用户配置
```

所有路径相对可执行文件，不依赖工作目录。**本次不再迁移 schema 1、旧 data/photos 布局或旧恢复日志**。发现不支持的旧库、未来库或旧布局将保留文件并拒绝启动／恢复，不自动清空或新建空库。当前 ZIP 清单的 `FormatVersion=1` 是现行包格式，与 schema 1 不同，继续支持。

“设置 → 备份与恢复”下载完整 ZIP，内容含一致性数据库快照、所有登记的原图和缩略图。恢复先预检并显示点位、排查、问题、整治和照片数量，确认覆盖后才执行；覆盖前保留旧数据，当前恢复日志支持中断回滚。恢复包必须来自当前支持的结构。

每日 00:00 保存前一天的数据库快照；缺失时在启动及跨日写入前补做；保留最近 14 份 `auto-日期.db`，**不含照片**，不删除 `before-*`。超过七天仍未关联的照片会清理。浏览器草稿不包含在服务端备份中。

**升级／搬迁前先下载完整备份并正常停机，再复制整个程序目录留作回退。** 更新时只替换两平台程序和整份 `wwwroot`，保留 data、photos、backup、temp 和用户配置；运行时不要只复制 app.db、不要删除 WAL。回退使用更新前的整目录副本或与其匹配的完整备份，不混用不同时点的数据库与照片。旧目录中真实数据的处理需要人工确认，程序不会代为删除。

## 日常流程与导出

正常排查同样保存；临时点位和排查共同成功或回滚。问题只有待整改、已反馈、复查闭环三档；排查详情按问题反馈、复查、退回，也支持单事务批量办理。删除临时点位最后一条排查会级联清理临时点位和点位照片，确认框明确说明。点位只加照片或切换停用不会再回写其他资料。

草稿在当前浏览器自动保存，提交期间字段冻结，失败后保留输入。上传可取消、失败可重试；缩略图失败回退原图，不阻断正常业务。浏览器支持的图片解码后按 EXIF 方向转 JPEG、去除 EXIF，长边不超过 1600，默认质量75；原图上限10MiB。HEIC 无法解码时先转 JPEG。样式、图标、字体栈、WASM 随静态页面本地供应；主动点高德链接才访问外部地图，工具不自动定位。

七种 DOCX：排查记录单、整改通知单、整改记录单、整治记录单、排查台账、整改台账、整治台账。整改记录单仅对有问题且全部闭环的排查开放。台账日期、社区和实际有效类型在导出弹窗全部显示；日期独立选择，未支持的列表搜索／状态条件不继承。

通知单支持 `{日期}`、`{点位}`、`{问题}`、`{期限}`、`{街道全称}`、`{社区}`。`{问题}` 为块级变量，最多一次；没有时正文后补问题表。重复时明确报错、不重置模板。编号最多100字。用户业务原文不会因包含“让、妥善、上传”等词而过滤。

保持尚未确认的规则：整治台账仍为摘要（全文在记录单）；通知单正文期限仍为该次最晚期限，各问题保留各自期限；退回后仍允许符合原有规则的业务日期补录；台账仍独立范围。详见 [实施决定](docs/optimization/DECISIONS.md)。

## 手机访问、网络与开机自启

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

## 平台和浏览器边界

Windows x86 在 Windows Server 2022 x64/WOW64 构建与运行验证；不支持 Windows7/8.1。Linux ARM64 在 Ubuntu24.04原生构建，已核验产物最高 GLIBC 符号2.38，不能承诺旧发行版或 Alpine/musl 兼容。linux-x64只用于测试，不进入统一发布包。

浏览器基线：Chrome/Edge111+、Safari/iOS16.4+、Firefox128+。自动化主要是 Chromium；最低版本和真机相机不能视为已逐一验收。没有 HTTPS 专属功能、Service Worker 或公网资源依赖。

Word 页面使用项目固定A4/字体声明和页码，不打包字体。缺少字体时由设备替代；**LibreOffice 渲染、OOXML结构和 Word/WPS GUI 是不同验收层**，不能互相替代。本轮结果与未验项见 [变更和验证说明](docs/optimization/CHANGES.md)。

## 开发、构建与回归

沿用 global.json、server项目与web锁文件，不擅自更新版本。开发：

```sh
dotnet run --project server/StreetEcoTool.csproj -p:PublishAot=false -- --no-browser
# 另一个终端
npm --prefix web ci
npm --prefix web run dev
```

Nuxt开发代理默认后端5180；生产仅 `nuxt generate` 的 `.output/public`。常规回归：

```sh
dotnet test server/StreetEcoTool.Tests/StreetEcoTool.Tests.csproj -c Release -p:PublishAot=false -warnaserror
npm --prefix web ci
npm --prefix web run typecheck
npm --prefix web run generate
# 对应原生宿主，Windows需x86 MSVC，Linux需clang、zlib开发包
dotnet publish server/StreetEcoTool.csproj -c Release -r <RID> -o publish/<RID> -p:PublishAot=true -p:SelfContained=true -warnaserror
python3 tests/api-smoke.py --executable publish/<RID>/StreetEcoTool --report test-results/api.json
python3 tests/word-smoke.py --executable publish/<RID>/StreetEcoTool --report test-results/word.json
# 把静态产物复制到可执行文件旁wwwroot；Windows替换exe名
cd web
npx playwright install --with-deps chromium
node browser-test.mjs --executable ../publish/<RID>/StreetEcoTool
```

`tests/word-layout-samples.py` 用真实API生成七类文档和边界样本；`tests/render-word.py` 固定本地字体配置，生成PDF、逐页PNG、哈希和人工审阅清单。CI同一宿主分别构建原始审计基线与新程序，使用相同测试脚本和字体渲染，浏览器／文档步骤失败时不组装最终包。Pillow、LibreOffice及Poppler只用于测试，不放入生产程序。

统一打包：

```sh
python3 scripts/package.py --win-x86 publish/win-x86/StreetEcoTool.exe --linux-arm64 publish/linux-arm64/StreetEcoTool --web-static web/.output/public --output dist
```

输出 `dist/hbtool.zip` 与校验文件。主程序架构、ZIP CRC、Linux执行位及共享wwwroot都会核对。历史完整说明仅保留在 [历史README](docs/history/v2-README.md)，其中旧迁移能力和旧测试数字不代表当前版本。

## 常见问题

手机连不上：核对实际监听网卡、IP、端口、防火墙、Wi-Fi隔离与电脑休眠。端口占用：正常关闭原实例或改端口并更新防火墙／书签。照片失败：核对可解码格式、10MiB上限、网络、磁盘和写权限，重试或确认移除失败任务。更新后白屏：替换完整wwwroot并刷新，不能混用两版hash资源。旧库拒绝：先保留完整目录，用与原数据匹配的旧程序导出／核对，不要删除文件绕过检查。
