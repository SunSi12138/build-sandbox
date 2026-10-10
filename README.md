# 街道生态环保「排查—整改」留痕工具

开发分支：`hbtool`。在办公室电脑或小主机运行，手机现场录入，电脑跟踪整改，导出 Word 留痕。数据保存在运行程序的电脑上，可整体备份。本工具面向一人使用、偶尔共用同一网址的可信局域网或蒲公英组网环境，不设登录、访问口令、权限体系，不做定位、地图、小程序、统计图表或自动编号，不使用云服务。**构建需要下载依赖；部署后的页面、图标、字体和图片压缩均不依赖外网。** 手机仍须能够连接运行工具的电脑。

## 1. 下载与快速开始

打开 [hbtool 分支构建列表](https://github.com/SunSi12138/build-sandbox/actions/workflows/build.yml?query=branch%3Ahbtool)，选择最新成功运行，在 **Artifacts** 下载对应附件。GitHub 下载的附件 ZIP 内还有实际发布包和同名 `.sha256` 文件，请再解压实际发布包。

| 附件 / 发布包 | 用途与实际验证环境 |
| --- | --- |
| `hbtool-win-x86` / `hbtool-win-x86.zip` | Windows x86 程序；在 Windows Server 2022 x64 的 WOW64 环境构建并实测 |
| `hbtool-linux-arm64` / `hbtool-linux-arm64.tar.gz` | ARM64 Linux；在 Ubuntu 24.04 arm64 原生构建并实测 |
| `hbtool-linux-x64` / `hbtool-linux-x64.tar.gz` | 额外的 Linux x64 验证包；CI 在 Ubuntu 22.04 上运行完整浏览器验收 |

解压到可写目录，例如 Windows 的 `D:\hbtool` 或 Linux 的 `~/hbtool`。不要在压缩包内部直接运行，也不要只复制可执行文件而漏掉 `wwwroot`。

| 解压后路径 | 内容 |
| --- | --- |
| `hbtool/StreetEcoTool.exe` 或 `hbtool/StreetEcoTool` | 唯一的程序可执行文件，无需安装 .NET Runtime、Node.js 或 SQLite |
| `hbtool/wwwroot/` | 完整的离线前端，包含本地图标及 WASM |
| `hbtool/data/` | 首次启动自动创建，包含数据库、照片与备份 |

Windows 双击 `StreetEcoTool.exe`；Linux 在解压目录执行 `./StreetEcoTool`（如执行权限丢失，先运行 `chmod +x StreetEcoTool`）。程序自动打开 `http://localhost:5180`，并在控制台打印手机可用的 IPv4 地址。没有桌面环境的 Linux 不会尝试打开浏览器。

首次使用先进入「设置」填写街道全称、落款单位、默认排查人，添加社区及通知单抬头，再进入「点位」建立点位。手机连接同一局域网，输入控制台或「设置 → 手机访问地址」显示的地址，例如 `http://192.168.1.100:5180`。手机上的 `localhost` 指手机自身，不能用它访问办公室电脑。

### 运行与浏览器兼容性

- Windows 应使用仍在 [.NET 10 官方支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)中的版本。客户端优先使用 Windows 11；Windows 10 仅限官方仍支持的 LTSC / Enterprise 分支。**不支持 Windows 7 / 8.1，也没有验证老旧 32 位 Windows 系统。** `win-x86` 表示程序架构，不代表兼容所有旧系统。
- 本项目 Windows 发布包只依赖系统 DLL / UCRT，没有 `vcruntime*`、`msvcp*` 或独立 SQLite DLL 依赖。支持范围内的现代 Windows 自带 [Universal CRT](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment)，常规部署无需额外安装 VC++ Redistributable；C++ 工具链只在构建时需要。
- ARM64 发布包以 **Ubuntu 24.04 arm64** 为验收基线。NativeAOT 的 Linux 系统依赖受构建宿主影响；当前产物需要的符号包括 `GLIBC_2.38`，不能据此承诺兼容 Ubuntu 22.04、Debian 12、任意树莓派系统或 Alpine / musl。其他环境应在受支持的目标系统上原生重建并验证，见 [NativeAOT 部署约束](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。
- 浏览器最低基线：**Chrome / Edge 111+、Safari / iOS 16.4+、Firefox 128+**，依据 [Tailwind CSS v4 官方要求](https://tailwindcss.com/docs/compatibility)，构建目标与此一致。建议用系统浏览器打开；旧浏览器缺少所需特性时会显示中文升级提示。

## 2. 启动、端口与数据目录

程序始终监听 `http://0.0.0.0:{port}`，默认端口 `5180`。在程序同目录创建 `appsettings.json` 即可修改，例如：

```json
{ "port": 5181 }
```

也可用命令行参数，优先级为 **命令行 > appsettings.json > 5180**：

```powershell
.\StreetEcoTool.exe --port 5181
.\StreetEcoTool.exe --no-browser
.\StreetEcoTool.exe --seed-demo
.\StreetEcoTool.exe --help
```

Linux 将上面的程序名换成 `./StreetEcoTool`。参数可以组合；`--port=5181` 也可用。`--seed-demo` 仅在社区、点位、排查均为空时灌入试用数据，不向已有业务数据重复追加；正式使用时直接不加此参数。

程序用 `Global\StreetEcoTool` Mutex 保证单实例。再次启动会打开已运行实例的真实端口后退出；加 `--no-browser` 则不打开浏览器。**更换端口前先关闭原实例。** 端口被其他程序占用时会用中文提示并退出。按 `Ctrl+C` 正常结束；SQLite 连接随服务关闭。

`wwwroot` 与 `data` 均相对于可执行文件目录解析，与启动时的工作目录无关。`data/app.db` 是数据库，`data/photos/yyyyMM/` 存 JPEG，`data/backup/` 存备份；运行时可能出现 SQLite 的 `-wal` / `-shm` 配套文件。不要将 `data` 放到只读目录，也不要同时用多个程序直接改数据库。业务日期均按**服务器本地日期**处理，请将电脑时区与时钟设为业务所在地时间。

## 3. 日常工作与提醒规则

1. **排查**：选点位（支持搜索、最近使用）→ 核对日期与排查人 → 选择正常 / 发现问题并登记问题、照片 → 保存。正常排查也生成记录。
2. **整改**：在「整改跟踪」筛选问题，登记社区反馈的日期、说明与照片，再记录复查结论。复查照片可选，不会因无照片而无法闭环。
3. **退回**：可退回待整改，原来的反馈、复查、退回说明与照片继续保留在办理历史中；当前轮状态、反馈及闭环字段重置。
4. **留痕**：在排查详情导出记录单 / 通知单，在「台账导出」按排查日期和社区导出整改台账。列表筛选保存在网址 query 中，刷新、返回可保留。

首页提醒按自然日计算：「已逾期」为期限早于今天且未闭环；「3 天内到期」包含今天至今天后第 3 天；「待复查」为社区已反馈。周期提醒按**周一开始的本周、自然月、自然季度**判断本期是否有排查，未来日期记录不抵扣。停用、不定期点位不参与周期提醒；停用点位已有的未闭环问题仍须处理。

合理默认值：新问题期限预填排查日期后 **7 天**，可以修改；责任单位默认所属社区；通知单编号始终手填、可空。单次排查最多 50 条问题，每次关联最多 100 张照片。已有历史记录的点位 / 字典不能直接删除，点位可停用；所有删除均需再次确认。排查保存时快照点位与社区信息，后续改名不会改变历史排查基本信息。

## 4. 照片与手机草稿

选图允许拍照或相册、多选，不强制打开相机。前端先按 EXIF 方向解码，长边缩至不超过 **1600 px**，以默认质量 **75** 重新编码 JPEG，再上传；原照片不上传，EXIF（包括位置）不会写入新 JPEG。质量可在设置中调整为 1–100。全局最多并行压缩 2 张，有处理及上传进度；WASM / Worker 失败会自动回退到 canvas JPEG。

单张**压缩后**照片上限为 10 MiB；复杂场景体积会更大，不保证每张都固定在几百 KB。失败的照片可逐张重试或确认移除；全部上传完成后再保存。浏览器不能解码某些 HEIC / 特殊格式时，请先转为 JPEG / PNG，或使用能解码该图片的浏览器。

新建排查草稿自动写入当前浏览器 `localStorage`，包括文字和**已经上传成功的照片 ID**；尚未上传完的原图在刷新后须重新选择。草稿只属于当前浏览器及网址的协议、主机、端口：从内网 IP 换成蒲公英 IP、改端口、换浏览器 / 手机、清理站点数据，都不会自动带走原草稿。已经保存到服务器的排查仍可从这些网址查看。

超过 **7 天仍未关联**的照片会在启动及每日维护时清理，旧草稿中的此类照片需重新选择。草稿不是正式留痕，也不包含在服务器备份内。部署不使用 Service Worker；断开外网可正常工作，断开与办公室电脑的连接则不能上传、保存或重新拉取业务数据。

## 5. Word 导出

- **排查记录单**：单次基本信息、问题及期限、现场照片、备注。正常记录也可导出。
- **整改通知单**：该次全部问题、社区抬头、可选手填编号、正文、落款、回执与附后照片；正常排查不生成整改通知单。
- **整改台账**：横向 A4，选择本月 / 本季 / 自定义排查日期及社区，可附整改前后照片；包含闭环 / 未闭环 / 逾期汇总。反馈、复查、退回的历史文字与照片保留，状态栏反映当前状态。

「设置 → 正文模板」支持 `{日期}`、`{点位}`、`{问题}`、`{期限}`，也支持 `{街道全称}`、`{社区}`。`{日期}` 使用排查日期；`{问题}` 插入该次问题表，省略此占位符也会在正文后补上问题清单。**`{期限}` 为本次全部问题中最晚的期限**，各问题的实际期限仍逐条列明；期限不同时另有说明。街道、落款与正文使用导出时的当前设置，社区抬头与点位基本信息使用排查快照。

文档为标准 `.docx`，全部 JPEG 嵌入文件，不依赖外部图片。标题指定方正小标宋简体二号，正文仿宋三号，表格宋体；字体表为标题字体和仿宋声明宋体替代。程序不下载或打包这些字体，查看电脑未安装时由 Word / WPS 使用可用替代字体，分页可能有差异。文件名如 `整改通知单_建设路改造工地_20261010.docx`；台账文件名使用社区及结束日期。

## 6. 备份、恢复与升级

**日常请用「设置 → 备份与恢复 → 下载备份 ZIP」保存完整备份**，另存到 U 盘或其他电脑。ZIP 包含通过 `VACUUM INTO` 生成的一致性 `app.db` 快照、登记的照片与备份清单；运行中可以下载。

| 方式 | 包含内容与保留规则 |
| --- | --- |
| 完整 ZIP | 数据库 + 照片；手动下载，保存多久由你决定 |
| 每日自动备份 | 仅数据库，`data/backup/auto-yyyy-MM-dd.db`，保留最近 **14 份**，不等于保留全部照片 |
| 恢复前旧数据 | `data/backup/before-restore-日期时间-标识/` 下的数据库与照片；不会按 14 份规则自动清理 |

每天首次启动自动备份数据库；持续运行时每分钟检查日期，跨日后执行当天维护。维护失败会记录中文日志并重试。数据库快照不能替代完整 ZIP，尤其不能单靠它找回已经删除的照片。

恢复步骤：选择完整 ZIP → 等待校验并核对时间、点位 / 排查 / 问题 / 照片数量 → 勾选覆盖确认 → 点击「确认覆盖并恢复」。确认有效期 30 分钟；上传包上限 4 GiB、解压总量上限 16 GiB、最多 50,000 个条目。无效数据库、危险路径或缺失照片会被拒绝。覆盖前自动保留原数据，恢复中断后下次启动按恢复日志回滚或完成收尾。

手工搬迁或升级时：先下载完整备份，正常停程序，再复制**整个 `data` 文件夹**；更新可执行文件和 `wwwroot` 时保留 `data`。**不要在运行中只热拷 `app.db`，也不要自行删掉 `-wal` 文件**，未检查点的数据可能仍在 WAL 中。恢复前保留的整套旧数据库与照片如需手工回退，也应停机后成套恢复，避免混用不同时间的数据库与照片。

## 7. 手机连通、防火墙与固定地址

手机与电脑须在能互通的局域网，或已加入能互通的蒲公英网络；使用相应网卡的 IPv4 地址和实际端口。工具不负责建立蒲公英连接，也不会调用蒲公英云接口。办公室电脑须保持开机，睡眠会使手机访问中断；访客 Wi-Fi 的设备隔离也可能阻止互访。

Windows 在管理员 PowerShell 放行实际端口，以下示例覆盖专用 / 域网络，依据 [New-NetFirewallRule 官方参数](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule)：

```powershell
New-NetFirewallRule -DisplayName "街道环保工具 TCP 5180" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5180 -Profile Private,Domain
Get-NetConnectionProfile
```

也可在「Windows Defender 防火墙 → 高级设置 → 入站规则 → 新建规则 → 端口」添加 TCP 5180。更换端口后同步更新规则。蒲公英虚拟网卡可能被识别为 Public：先查看它的实际网络配置文件，再只为需要的虚拟接口 / 地址范围配置相应规则；仅添加 Private 规则不会自动放行 Public 接口，不必关闭整个防火墙。

固定内网地址优先在办公室路由器上为电脑网卡 MAC 设置 **DHCP 地址保留 / 静态租约**。随后确认控制台显示的地址并给手机加入书签；蒲公英访问则保存其虚拟地址。不能配置路由器时，可由网络维护人员分配不冲突的固定 IPv4、掩码、网关和 DNS，避免随意占用 DHCP 地址池中的地址。

## 8. 开机自启

### Windows

简便方式：为 `StreetEcoTool.exe` 创建快捷方式，`Win+R` 输入 `shell:startup`，将快捷方式放入启动文件夹。快捷方式目标可写为 `"D:\hbtool\StreetEcoTool.exe" --no-browser`；这会在**当前用户登录后**启动，移除快捷方式即可取消。

需要无人登录也运行时，在「任务计划程序」创建任务，触发器选「启动时」，操作指向完整可执行路径，参数填 `--no-browser`，起始于填程序目录。选择对该目录有写权限的运行账户，并按需要选「不管用户是否登录都运行」；多实例策略选「不启动新实例」。不要设置运行若干小时后自动停止。创建后手动运行任务，确认手机能打开网址；「起始于」只是便于维护，程序不依赖它定位数据。

### Linux systemd（Ubuntu 示例）

先停止手工启动的实例。将发布包解压为 `/opt/hbtool/`，创建专用系统账户并赋予该目录写权限（账户已存在时跳过创建）：

```sh
sudo useradd --system --user-group --home-dir /opt/hbtool --shell /usr/sbin/nologin hbtool
sudo chown -R hbtool:hbtool /opt/hbtool
sudo chmod +x /opt/hbtool/StreetEcoTool
```

把以下内容保存为 `/etc/systemd/system/street-eco-tool.service`：

```ini
[Unit]
Description=Street Eco Inspection Tool
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=hbtool
Group=hbtool
WorkingDirectory=/opt/hbtool
ExecStart=/opt/hbtool/StreetEcoTool --no-browser
Restart=on-failure
RestartSec=5
TimeoutStopSec=45

[Install]
WantedBy=multi-user.target
```

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now street-eco-tool
sudo systemctl status street-eco-tool
journalctl -u street-eco-tool -n 80 --no-pager
# 正常停止：sudo systemctl stop street-eco-tool
```

默认 `SIGTERM` 会交给 ASP.NET Core 正常关闭。更改路径时同步修改 `User` / `Group` 的文件权限、`WorkingDirectory` 与 `ExecStart`；运行前检查服务器本地时区。需要自定义端口可改同目录 `appsettings.json`，然后重启服务。

## 9. 项目与本地开发

`server/` 为 Minimal API、手写参数化 SQLite SQL、迁移和 OOXML 导出，`server/StreetEcoTool.Tests/` 为单元测试；`web/` 为 Nuxt SPA；`tests/` 为实际进程 API / Word 测试；`scripts/` 为打包脚本；`.github/workflows/` 为构建验收；`spikes/` 保留三个先行技术验证。

固定版本：**.NET SDK 10.0.401**（`global.json` 允许同特性带最新补丁）、Microsoft.Data.Sqlite 10.0.12、SQLitePCLRaw.bundle_e_sqlite3 2.1.12、StaticLink.e_sqlite3 2.1.10.1；AOT 静态 SQLite 实测 **3.46.1**。前端 Nuxt **4.6.1**、Nuxt UI **4.11.3**、Tailwind **4.3.3**、TypeScript **6.0.3**，jSquash JPEG 1.6.0 / Resize 2.1.1。TS 6 与当前 `vue-tsc` 验证配套，依赖以锁文件为准。

安装 .NET SDK、Node.js **24**（CI 使用此版本）；Python 3 仅供测试和打包。两个终端分别运行：

```sh
# 终端 1：仓库根目录；dotnet run 使用 JIT
dotnet run --project server/StreetEcoTool.csproj -p:PublishAot=false -- --no-browser
# 终端 2：仓库根目录
cd web
npm ci
npm run dev
```

开发访问 `http://localhost:3000`；`nuxt dev` 将 `/api` 与 `/photos` 代理到 `127.0.0.1:5180`。后端改端口时也需修改 `web/nuxt.config.ts` 的开发代理。JIT 的数据位于实际输出目录（通常 `server/bin/Debug/net10.0/data`），与发布包数据隔离；JIT 用 bundle 动态 SQLite，版本可能与 AOT 静态引擎不同。

生产配置为 `ssr:false`、hash 文件路由、中文浅色 Nuxt UI、系统字体、本地图标包与本地 WASM；`nuxt generate` 的 `.output/public` 可直接静态托管，见 [Nuxt 官方部署说明](https://nuxt.com/docs/4.x/getting-started/deployment#client-side-only-rendering)。生产不运行 Nitro 服务、不引入 Pinia，也不需要 HTTPS 专属浏览器能力。

## 10. 原生构建、打包与验收

```sh
# 仓库根目录：先生成前端
npm --prefix web ci
npm --prefix web run typecheck
npm --prefix web run generate
```

**Windows x86 必须在 Windows 构建**：安装 .NET SDK 与 Visual Studio 2022 或更新版的「使用 C++ 的桌面开发」工具（含 x86 MSVC、Windows SDK），从 x86 Native Tools 命令环境运行以下 PowerShell 命令。使用的是 `dotnet` CLI，无需通过旧版 Visual Studio IDE 打开 .NET 10 项目。

```powershell
dotnet publish server/StreetEcoTool.csproj -c Release -r win-x86 -o publish/win-x86 -p:PublishAot=true -p:SelfContained=true -warnaserror
New-Item -ItemType Directory -Force publish/win-x86/wwwroot | Out-Null
Copy-Item web/.output/public/* publish/win-x86/wwwroot -Recurse -Force
python scripts/package.py --rid win-x86 --publish publish/win-x86 --output dist
```

**Linux ARM64 在 Ubuntu 24.04 arm64 原生构建**（`uname -m` 应为 `aarch64`）：

```sh
sudo apt-get update
sudo apt-get install -y clang zlib1g-dev
dotnet publish server/StreetEcoTool.csproj -c Release -r linux-arm64 -o publish/linux-arm64 -p:PublishAot=true -p:SelfContained=true -warnaserror
mkdir -p publish/linux-arm64/wwwroot
cp -a web/.output/public/. publish/linux-arm64/wwwroot/
python3 scripts/package.py --rid linux-arm64 --publish publish/linux-arm64 --output dist
```

NativeAOT 不支持跨操作系统编译；本项目 CI 使用目标架构原生宿主，不依赖 ARM 交叉工具链。AOT / 裁剪及其他构建警告视为错误，没有以关闭警告完成发布。打包只选取可执行文件和 `wwwroot`，不会打入开发数据库、独立 SQLite 动态库或调试符号。

`dist/` 输出 ZIP / tar.gz 及 SHA-256 文件。Linux 用 `sha256sum -c hbtool-linux-arm64.tar.gz.sha256`；Windows 用 `Get-FileHash .\hbtool-win-x86.zip -Algorithm SHA256` 与同名 `.sha256` 内容对照。Actions 发布包保留 30 天，测试证据保留 14 天，可重新运行工作流生成；需要长期保存的包请自行下载留存。

复现验收前关闭本机正在运行的工具；Python 脚本会复制程序到临时目录，自行启动并关闭，不使用正式 `data`。以下以 Linux ARM64 发布目录为例；Windows 替换可执行文件路径和 `python3` 命令：

```sh
dotnet test server/StreetEcoTool.Tests/StreetEcoTool.Tests.csproj -c Release -p:PublishAot=false -warnaserror
python3 tests/api-smoke.py --executable publish/linux-arm64/StreetEcoTool --report test-results/api-smoke-report.json
python3 tests/word-smoke.py --executable publish/linux-arm64/StreetEcoTool --report test-results/word-smoke-report.json
cd web
npx playwright install --with-deps chromium
node browser-test.mjs --executable ../publish/linux-arm64/StreetEcoTool
```

三个先行 spike 均已通过：[AOT + SQLite 三平台](spikes/aot-sqlite/RESULTS.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360)）、[Nuxt 离线 / hash](spikes/nuxt-offline/README.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38050309537)）、[jSquash 方向 / 压缩 / 回退](spikes/photo-compression/RESULTS.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38050177110)）。约 1200 万像素合成样本由 7,946,458 字节压到 301,622 字节，八种 EXIF 方向与 WASM 失败回退均有实际浏览器验证。

Word 阶段已通过 **103 个单元测试、3 个 RID 的零警告 AOT 发布、各平台 31 次 Word API 请求、9 组完整浏览器流程**，包括三种真实 Word 下载；最终结果以[最新成功工作流](https://github.com/SunSi12138/build-sandbox/actions/workflows/build.yml?query=branch%3Ahbtool)及其中的 `test-results-*`、`browser-evidence` 为准。测试覆盖提醒边界、JPEG 解析、照片关联、整改流转、备份恢复 / 中断回滚、OOXML 结构与资源、普通 HTTP 压缩、外域请求阻断和手机布局。

**实际验证边界**：Windows 程序在 Server 2022 WOW64 执行；浏览器自动化使用 CI 的 Chromium，未逐一实测最低浏览器版本、真机 iOS 或真实手机相册。LibreOffice 已对六份含长文本、照片的 DOCX 转 PDF 并目视检查，QA 使用 Noto CJK 替代字体；尚未在 Microsoft Word / WPS 图形界面实际打开验收，不能把 LibreOffice 检查等同于这两项人工验收。可用 `libreoffice --headless --convert-to pdf --outdir test-results/word-pdf test-results/word-samples/*.docx` 在装有中文字体的电脑复查。

## 11. 常见问题

| 现象 | 排查办法 |
| --- | --- |
| 电脑能打开，手机连不上 | 核对地址不是 localhost、端口正确；确认电脑未休眠、手机与相应网卡互通；检查 Wi-Fi 隔离及该物理 / 蒲公英网卡实际防火墙配置文件 |
| 提示端口占用 / 改端口无效 | 正常关闭已有实例后重启；其他程序占用时改 `--port` 或配置文件，并同步放行新端口 |
| 照片上传失败 | 等待压缩完成，检查与服务器连接、磁盘空间、目录权限；重试失败照片；压缩后超过 10 MiB 时调低质量，不能解码的格式先转 JPEG / PNG |
| 换网址后草稿不见了 | 回原协议、IP / 主机名、端口和浏览器查看；正式保存的记录在服务器，草稿不会跨浏览器或网址同步 |
| 页面空白 / 更新后样式异常 | 使用满足基线的浏览器；确认完整复制 `wwwroot`，刷新页面。入口 HTML 禁缓存，带 hash 的资源长期缓存；不要混用新旧版本的静态文件 |
| 通知单不能下载 / 图片缺失 | 先填写街道设置并确认该次有问题；检查照片文件及完整备份，保留现有数据后处理；正常记录使用「导出排查记录单」 |
| 无法写入 / Linux 提示 GLIBC 版本不足 | 将安装目录设为运行账户可写并检查磁盘；GLIBC 问题使用符合构建基线的系统或在目标受支持系统重新原生构建，不能靠安装 .NET Runtime 修复 |
