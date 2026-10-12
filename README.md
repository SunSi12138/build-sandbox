# 街道生态环保工具

开发分支：`hbtool`，本轮版本 **v2**。在办公室电脑或小主机运行，手机现场录入，电脑处理问题和整治记录，按需导出 Word。导航为 **首页、点位、排查、整治、设置**；问题反馈、复查与退回都在排查详情内完成。

本工具面向一人使用、偶尔共用同一网址的可信局域网或蒲公英组网环境，不设登录、访问口令或权限体系，数据保存在运行程序的电脑上。**构建需要下载依赖；部署后的业务页面、图标、字体与图片压缩均不依赖外网。** 手工打开高德地图或导航链接时需要相应服务，工具不自动定位、不加载地图 SDK、不转换坐标，也不调用云端业务接口。手机须能连接运行工具的电脑。

## 1. 下载与快速开始

打开 [hbtool 分支构建列表](https://github.com/SunSi12138/build-sandbox/actions/workflows/build.yml?query=branch%3Ahbtool)，选择通过全部验收的运行，在 **Artifacts** 下载附件 **`hbtool`**。GitHub 附件内包含实际发布包 **`hbtool.zip`** 和 **`hbtool.zip.sha256`**；再解压实际发布包。

v2 只分发一个包，同时包含两个平台的可执行文件和一份前端：

```text
hbtool/
  StreetEcoTool-win-x86.exe
  StreetEcoTool-linux-arm64
  wwwroot/
```

解压到可写目录，例如 Windows 的 `D:\hbtool` 或 Linux 的 `~/hbtool`。不要在压缩包内部运行，也不要漏掉 `wwwroot`。按当前系统选择程序，另一个可执行文件可以保留：

| 系统 | 启动方式 | 构建与验收宿主 |
| --- | --- | --- |
| Windows | 双击 `StreetEcoTool-win-x86.exe` | Windows Server 2022 x64 的 WOW64 环境 |
| ARM64 Linux | `./StreetEcoTool-linux-arm64` | Ubuntu 24.04 arm64 原生环境 |

Linux 解压工具如未保留执行权限，先运行 `chmod +x StreetEcoTool-linux-arm64`。部署无需安装 .NET Runtime、Node.js 或 SQLite；Linux 的系统库仍须符合产物构建基线。`linux-x64` 仅用于 CI 单元测试与浏览器验收，不放入发布包。

默认端口为 **5180**，默认**仅本机访问**。有桌面环境时程序自动打开 `http://localhost:5180`。首次使用先进入「设置」填写街道全称、落款单位、默认排查人，添加社区及通知单抬头，然后建立常态点位或直接新增临时点位排查。

手机使用前，在运行程序的电脑上通过 `localhost` 打开首页，点击右上角「仅本机」，选择所需网卡或「所有网卡」并应用。把浮层中可访问的 IPv4 网址发给手机，例如 `http://192.168.1.100:5180`；手机与该网卡须能互通。手机上的 `localhost` 指手机自身。

没有桌面的 ARM64 Linux 应明确开放监听，例如：

```sh
./StreetEcoTool-linux-arm64 --no-browser --listen 0.0.0.0
```

### 系统与浏览器兼容性

- Windows 系统支持范围以 [.NET 10 官方支持列表](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)为准。`win-x86` 是程序架构，不代表支持所有旧版系统；不支持 Windows 7 / 8.1，未验收老旧 32 位 Windows。
- v1 Windows 产物核验为系统 DLL / UCRT 依赖，没有独立 SQLite、`vcruntime*` 或 `msvcp*` DLL；v2 的最终依赖核验结果见本文末尾。现代 Windows 的 UCRT 说明见 [Universal CRT 部署文档](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment)。C++ 工具链用于构建，用户部署不需要编译工具链。
- ARM64 构建基线为 **Ubuntu 24.04 arm64**。NativeAOT 的 Linux 系统依赖受构建宿主影响；本轮 ARM64 产物最高 GLIBC 符号版本为 `GLIBC_2.38`。不能据构建架构承诺兼容 Ubuntu 22.04、Debian 12、任意树莓派系统或 Alpine / musl；其他环境应按 [NativeAOT 部署约束](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)在目标系统重建并验收。
- 前端兼容基线为 **Chrome / Edge 111+、Safari / iOS 16.4+、Firefox 128+**，依据 [Tailwind CSS v4 的浏览器要求](https://tailwindcss.com/docs/compatibility)。缺少必要特性的浏览器会显示中文升级提示。自动化使用 CI 的 Chromium，最低版本及真实 iOS 设备没有逐一实测。

## 2. 监听设置、端口与单实例

启动配置的优先级是 **命令行 > 程序同目录 `appsettings.json` > 默认值**。发布包不包含 `appsettings.json`；在界面应用监听设置时会创建或更新它，也可以手工创建：

```json
{
  "port": 5180,
  "listen": ["127.0.0.1", "192.168.1.100"]
}
```

`listen` 可包含 `127.0.0.1` 和一个或多个本机 IPv4；`0.0.0.0` 表示所有网卡，必须单独选择。程序枚举当前可用网卡，Docker、VMware、Hyper-V 等接口归在「其他网卡」中。

首页监听浮层显示当前生效的监听、网卡与可访问地址。**只有请求来源为回环地址时可修改监听**：须在运行程序的电脑上使用 `http://localhost:端口` 或 `http://127.0.0.1:端口` 打开；即使在这台电脑上，使用它的局域网 IP 访问也不能修改，其他访问者只能查看地址，接口返回 403。保留「仅本机」选项可继续通过回环地址修改配置。

监听应用后会短暂中断 HTTP 连接，页面检查新监听是否生效；如移除了当前地址，应打开应用结果中的新网址。只选择一个指定网卡前会提示 `localhost` 将不可用。若已经移除回环监听，需在本机编辑配置或使用命令行后重启，恢复本机配置入口。

已保存的 IP 因 DHCP 或网卡变化而失效时，启动会跳过该地址并打印中文警告，浮层提示其当前不可用；没有可用地址时回退到 `127.0.0.1`。端口确实被其他程序占用时仍会提示并退出。

```powershell
.\StreetEcoTool-win-x86.exe --port 5181
.\StreetEcoTool-win-x86.exe --listen 127.0.0.1 --listen 192.168.1.100
.\StreetEcoTool-win-x86.exe --listen 0.0.0.0 --no-browser
.\StreetEcoTool-win-x86.exe --seed-demo
.\StreetEcoTool-win-x86.exe --help
```

Linux 使用 `./StreetEcoTool-linux-arm64`。`--port=5181`、`--listen=0.0.0.0` 也可用；`--listen` 可重复，但「所有网卡」不能与其他地址混用。命令行覆盖会在控制台提示；使用带 `--listen` 的快捷方式或服务启动时，下次启动仍以该参数为准，界面保存的配置不会覆盖启动命令。

**修改端口须正常关闭程序再启动**，并同步修改手机书签与防火墙规则；监听地址可以在界面应用。`--seed-demo` 仅在社区、点位和排查均为空时写入示例，包括临时点位排查、反馈、已闭环问题和整治记录，不重复追加到已有业务数据。

程序用 `Global\StreetEcoTool` Mutex 保证单实例。再次启动会打开已有实例的真实访问地址后退出；`--no-browser` 可禁止打开浏览器。按 `Ctrl+C`、正常关闭控制台或停止系统服务结束程序。所有运行路径都相对于可执行文件目录，与启动时的工作目录无关。

## 3. 点位、排查与整改

### 常态点位与临时点位

「点位」默认显示常态点位，支持社区、类型、停用状态和本期未排查筛选。常态点位可设置每周、每月、每季或不定期，登记地址、联系人、电话、备注和最多 **9 张**点位照片；最早的一张作为封面。

新增排查可以选择已有常态点位，也可选择「临时点位」，填写名称、社区、类型、地址等后与排查一起保存。临时点位固定为不定期，不参与周期提醒，默认不混入常态点位列表；排查列表可筛选临时记录。排查、问题或照片校验失败时，临时点位与本次记录一起回滚。

临时点位可以从详情「转为常态」，选择排查频次后进入常态管理，原排查记录与照片仍关联同一地点。删除临时点位的最后一次排查时，会同时清理这个临时点位及其点位照片；常态点位不会因删除最后一条排查而消失。

### 经纬度与外部地图

点位可选填**经度、纬度**，必须同时填写或同时留空。输入顺序为经度在前、纬度在后，支持英文逗号、中文逗号、空格或制表符分隔；校验经度 `-180～180`、纬度 `-90～90`，疑似经纬顺序颠倒时给出提示，保存到小数点后 6 位。

坐标按高德使用的 **GCJ-02** 保存；工具不自动获取 GPS，不在不同坐标系之间转换。已有坐标时可复制坐标，或手工打开地图 / 导航链接；没有坐标时不显示这些入口。照片 EXIF 中的位置不用于填充点位坐标。

### 排查与问题办理

1. **新增排查**：选择点位方式，核对日期与排查人，选择正常或发现问题，填写问题、期限和照片后保存。正常排查同样生成记录。
2. **反馈**：排查详情按问题显示当前状态；可一次勾选多项待整改问题，统一反馈日期，分别填写说明与整改照片。
3. **复查**：可勾选多项已反馈问题，统一复查日期，分别填写结论与可选复查照片，保存后闭环。
4. **退回**：逐项退回待整改，填写退回说明；以往反馈、退回、复查文字与照片继续保留，当前轮反馈及闭环字段重置。
5. **编辑与删除**：可修改排查日期、排查人、通知单编号和备注。日期不能晚于今天或已有办理日期。删除排查须确认，并级联删除本次问题、办理记录及对应照片和缩略图。

一次批量反馈或复查中的所有条目在同一事务内保存。重复问题、错误归属、不符状态、无效日期或任意一张照片关联失败，会使整批撤销；不会只保存前半批。

排查列表以**每次排查**为一行，显示问题数、闭环数与最近期限。整改状态按以下规则汇总：正常为「无问题」；有待整改问题为「待整改」；没有待整改但有已反馈问题为「待复查」；全部问题复查完成为「已闭环」。列表支持状态、社区、类型、日期、逾期、临近到期和临时点位筛选，筛选写入网址 query。状态数量依据其他筛选条件计算，切换状态不会把其他状态的计数压成零；「次排查」与「项问题」分开统计。

新问题期限默认排查日期后 **7 天**，可修改；责任单位默认所属社区；通知单编号手填、可空。单次排查最多 50 项问题，关联照片总数最多 100 张。已有引用的点位或字典不能直接删除，可停用点位。排查保存时快照点位名称、类型、社区、抬头、地址和联系人，后续改名不改写历史基本信息。

### 首页与提醒

首页显示逾期、3 天内到期、待复查和本期未排查数量，待办合并为排查记录，最多显示 5 条，优先展示逾期，其次临近到期和待复查；另有最近 5 次排查。

提醒按**服务器本地自然日**计算：期限早于今天且未闭环为逾期；3 天内到期包含今天至今天后第 3 天。周期按周一开始的本周、自然月、自然季度检查是否已有本期排查，未来日期记录不抵扣。停用、不定期及临时点位不参与周期提醒；它们已有的未闭环问题仍参与问题提醒。

## 4. 整治记录

「整治」用于登记卫生清扫、垃圾清运、河道清理等工作，可新建、编辑、删除、查看详情和导出台账。记录包含名称、类型、社区、地点、开始日期与可选结束日期、组织单位、参加单位及人员、整治内容、成效、备注，以及以下可选数量：

| 字段 | 保存规则 |
| --- | --- |
| 参加人数、出动车辆 | 非负整数，可空 |
| 清运垃圾（吨）、清理面积（㎡） | 非负数，可空，四舍五入保留 2 位小数 |
| 整治结束日期 | 不得早于开始日期 |
| 整治前、整治中、整治后照片 | 三组分别最多 20 张 |

组织单位默认当前落款单位。整治类型可在设置中管理，预置卫生大扫除、垃圾清运、河道清理、乱堆乱放清理、小广告清除、绿化补种、其他。被引用的类型和社区不能删除；记录保存类型与社区名称快照，字典改名后原记录仍显示保存时的名称。删除整治须确认，并清理其三阶段照片与缩略图。

## 5. 照片与浏览器草稿

选图允许拍照或相册、多选，不强制打开相机。前端按 EXIF 方向解码，把主图长边缩至不超过 **1600 px**，默认质量 **75** 重新编码为 JPEG 后上传；原文件不上传，新 JPEG 不写入 EXIF。主图质量可在设置中调整为 1～100。压缩全局最多并行 2 张，有处理和传输进度；WASM / Worker 失败时回退到 canvas JPEG。

同一张图另生成长边不超过 **360 px**、质量 **70** 的 JPEG 缩略图，保存为同目录的 `{id}.thumb.jpg`。列表及画廊预览使用缩略图，查看大图和 Word 使用主图；旧照片没有缩略图时自动回退主图，缩略图失败不阻断主图保存。

主图压缩后上限为 **10 MiB**，缩略图上限为 **200 KiB**。图片体积随场景变化，不保证固定大小。主图失败可逐张重试或确认移除，主图全部完成后才可保存业务记录。部分浏览器无法解码 HEIC 或其他特殊格式时，请先转换为 JPEG / PNG。

新增排查和新增整治会把文字及已上传成功的照片 ID 写入当前浏览器 `localStorage`；尚未成功的原图，刷新后需要重新选择。草稿只属于当前浏览器及当前网址的协议、主机和端口，换 IP、端口、浏览器或手机不会同步。v2 使用新的草稿格式；旧 v1 排查草稿会清除，服务器中已经保存的业务记录不受影响。

上传后尚未关联业务的照片可暂时没有归属，业务保存时只允许关联一个排查、问题、点位或整治。超过 **7 天仍未关联**的照片在启动和每日维护时清理，旧草稿引用的此类照片需重选。浏览器草稿不包含在服务器备份中。部署不使用 Service Worker；断开外网仍可使用业务页面，断开与办公室电脑的连接则不能上传、保存或重新读取业务数据。

## 6. 七种 Word 导出

详情页导出单份记录，排查列表与整治列表的「导出台账」打开弹窗，移动端使用底部抽屉。台账支持本月、本季、本年、自定义日期和社区筛选；统计覆盖整个所选范围，不受列表分页限制。

| 导出 | 入口与内容 |
| --- | --- |
| 排查记录单 | 排查详情；基本情况、问题与期限、现场照片、非空备注，正常排查也可导出 |
| 整改通知单 | 有问题的排查详情；可选编号、社区抬头、正文、问题表、落款、回执、附后现场照片 |
| 整改记录单 | 有问题且全部复查闭环的排查详情；逐项反馈、复查、前后及复查照片，有退回时列办理过程，完成日期为最后闭环日期 |
| 整治记录单 | 整治详情；基本情况、内容、数量、成效、前后与过程照片、非空备注 |
| 排查台账 | 排查列表选择台账类型；每次排查一行，汇总正常 / 发现问题次数和问题 / 闭环项数 |
| 整改台账 | 排查列表选择台账类型；每个问题一行，汇总闭环、待复查、待整改和逾期数，可附照片 |
| 整治台账 | 整治列表；每次整治一行，汇总人数、车辆、垃圾吨数和面积，可附照片 |

仍有问题未闭环时，整改记录单接口返回 400 并说明「尚有 N 项问题未闭环」；正常排查使用排查记录单。台账汇总是主表的最后一行，附照在新页输出。整治台账长内容及成效会截断，全文见整治记录单。

「设置 → 通知单模板」支持 `{日期}`、`{点位}`、`{问题}`、`{期限}`、`{街道全称}`、`{社区}`。`{日期}` 是排查日期；`{问题}` 插入问题表，省略时在正文后补问题表；`{期限}` 使用该次最晚的整改期限，每项实际期限仍逐条列明。街道、落款和正文采用导出时当前设置，点位与社区抬头采用排查快照。正文与落款日期写为「2026年10月12日」，表格日期为 `2026-10-12`。

### 统一样式

单据为纵向 A4、四边 2.5 cm；台账为横向 A4、四边 2.0 cm。页脚居中 `— 1 —`，宋体小五。样式约定如下：

| 内容 | 字体与字号 | 排版 |
| --- | --- | --- |
| 主标题 | 方正小标宋简体，二号 22 pt | 居中、不加粗，段后 12 pt |
| 一级标题 | 黑体，三号 16 pt | 左对齐、不加粗，段前 12 pt、段后 6 pt |
| 二级标题 | 仿宋，三号 16 pt | 左对齐、加粗，段前 6 pt、段后 3 pt |
| 正文 | 仿宋，三号 16 pt | 首行缩进 2 字符，段间距为 0；抬头及落款按用途对齐 |
| 表头 | 黑体，五号 10.5 pt | 居中，浅灰 `F2F2F2` 底色，跨页重复 |
| 表格内容 | 仿宋，五号 10.5 pt | 文本左对齐，日期、状态和数量等按列居中或右对齐 |

所有段落，包括图片、表格、页脚及间隔段落，都使用 `w:line="240"`、`w:lineRule="auto"` 的单倍行距，避免图片被固定行高裁切。表格为 0.5 pt 黑色边框，单元格上下内边距 50、左右 100 twips，行不拆分，每张表声明列网格。表格后间隔段落使用 1 pt 字号。

字体声明的 `ascii`、`hAnsi`、`eastAsia`、`cs` 一致，并设置 `hint="eastAsia"`；字体表声明方正小标宋简体、仿宋回退宋体，黑体回退微软雅黑。程序不下载、不打包字体，实际替代字体仍由打开文档的软件决定，分页可能随安装字体变化。

DOCX 内嵌全部照片，照片下方不显示上传时间；空章节不输出。文档作者取街道全称，`settings.xml` 不设置 `updateFields`，页码保留正常 PAGE 域。标准 OOXML 校验与实际 Word / WPS 打开体验属于不同验收项，当前人工验收边界见第 13 节。

## 7. 数据目录、备份与升级

首次启动后的目录如下，所有路径相对于可执行文件：

```text
hbtool/
  StreetEcoTool-win-x86.exe
  StreetEcoTool-linux-arm64
  wwwroot/
  appsettings.json                    用户保存监听或手工配置后出现
  data/
    app.db                            数据库
    app.db-wal / app.db-shm            SQLite 运行时可能存在
  photos/
    yyyyMM/{id}.jpg
    yyyyMM/{id}.thumb.jpg
  backup/
    auto-yyyy-MM-dd.db
    before-migrate-v1-日期时间-标识.db
    before-restore-日期时间-标识/
  temp/                               运行临时文件与恢复暂存
```

**`data` 只放数据库，`photos`、`backup`、`temp` 与它同级。** 安装目录须对运行账户可写。不要同时用多个程序修改数据库；不要在运行中只复制 `app.db`，也不要自行删除 `-wal`，尚未检查点的数据可能仍在 WAL 中。

### 完整备份与恢复

在「设置 → 备份与恢复」下载完整 ZIP，另存到 U 盘或其他电脑。ZIP 内保持 `app.db`、`manifest.json`、`photos/` 的结构，数据库通过 `VACUUM INTO` 生成一致性快照，包含已登记的主图与缩略图；可以在程序运行中下载。照片数量只统计主图。

| 备份方式 | 内容与保留规则 |
| --- | --- |
| 下载的完整 ZIP | 数据库、主图、缩略图及清单；由用户保管，不包含程序配置或浏览器草稿 |
| `backup/auto-yyyy-MM-dd.db` | 每日数据库快照，不含照片，保留最近 14 份 |
| `backup/before-migrate-v1-*.db` | v1 迁移前的原数据库；不按每日快照规则删除 |
| `backup/before-restore-*/` | 覆盖恢复前的数据库与照片；不按每日快照规则删除 |

恢复步骤：选完整 ZIP → 校验并核对摘要数量 → 勾选覆盖确认 → 确认恢复。确认标识有效 30 分钟；包上限 4 GiB、解压总量上限 16 GiB、最多 50,000 个条目。危险路径、非预期数据库结构、外键问题、缺失或不符尺寸的照片等会被拒绝。

恢复前保留当前数据库与照片，保存位置显示为程序目录下的 `backup/before-restore-…`。目录切换和中断日志均在同一程序根目录内；恢复中断后下次启动根据日志回滚或完成收尾。v1 ZIP 仍可恢复，恢复后自动迁移数据库为 v2；备份来自更高版本时会拒绝。

### 每日 00:00 保存前一天快照

**每天 00:00 保存前一天的数据库快照，保留最近 14 份，不含照片。** 例如 2026-10-13 00:00 生成 `backup/auto-2026-10-12.db`。文件名日期是快照所属日期，同日已有快照不覆盖。

程序按本地时间计算下一次 00:00，使用可测试时钟调度，每次唤醒重新检查日期。启动时在接受 HTTP 请求和写入示例数据前，如果缺前一天快照就补做；连续多天未运行也只补前一天这一份。业务写入在存储锁内再次检查前一天快照，避免跨午夜请求或延迟定时回调把当天新写入混入前一天快照。

孤立照片和超过 1 天的临时文件在启动与每日维护时清理，不再每分钟轮询。只按日期清理命名符合规则的 `auto-????-??-??.db`，保留最新 14 份，不清理 `before-*`。数据库快照无法找回已删除的照片，请同时保存完整 ZIP。

日期、提醒和午夜调度都采用**服务器本地时区**。启动会打印时区；Linux 缺少时区数据可能按 UTC 运行，控制台在检测到与 `TZ` 不一致时提示安装 `tzdata` 或设置 `TZ=Asia/Shanghai`。请核对系统时钟和业务所在地时区。

### v1 → v2 自动迁移

旧安装直接使用新程序启动时，先恢复可能中断的旧恢复操作，再迁移旧目录：`data/photos` → `photos`、`data/backup` → `backup`，旧临时与恢复暂存移入 `temp`。旧、新布局均可重复启动；目标已有内容时逐项合并，同名文件不覆盖，控制台记录冲突，源目录清空后才删除。**有同名冲突时，为保留原文件，旧非空目录可能暂时仍在 `data` 下**，应在保留副本后人工核对。移动失败会中文报错并停止启动，不继续使用不完整目录。

数据库识别为 v1 时，先完成 WAL 检查点并保留原 v1 数据库，然后在单个事务内增加点位经纬度和临时标记、建立整治类型及整治表、扩充照片归属和缩略图字段。照片表重建保留原 `action_id`、路径、时间及已有归属，已上传但未关联的照片也保留。外键检查通过后才提交版本号 2；迁移失败回滚结构及版本，并保留迁移前副本供排查。迁移本身不要求为旧照片补缩略图。

仓库提供固定的真实 v1 二进制夹具 [schema-v1](server/StreetEcoTool.Tests/Fixtures/schema-v1/README.txt)，包含数据库、排查、问题、办理历史、不同归属和未关联照片。新增测试比较迁移前后记录及文件、检查外键、反复启动、故障回滚，并覆盖新旧目录同时存在和 v1 ZIP 恢复。本轮实际执行结果见第 13 节，不能只凭代码覆盖声明验收完成。

### 搬迁与版本升级

1. 先下载完整 ZIP，正常停止程序或系统服务。
2. 复制**整个程序根目录 `hbtool`**作为迁移或升级前副本，保留数据库、照片、备份、配置及其相对路径；v2 不能只复制 `data`。
3. 从新包只替换两个可执行文件及整个 `wwwroot`，保留 `data`、`photos`、`backup`、`temp` 和 `appsettings.json`；旧 v1 未带 RID 后缀的程序不要继续使用，更新快捷方式与服务路径。
4. 启动新程序，核对控制台迁移结果、监听地址、点位 / 排查 / 整治数量和照片，再恢复日常使用。

如需回退到 v1，应停机恢复升级前的整套程序根目录副本，或成套恢复原数据库和对应照片；不要用 v1 程序打开已经升级到 v2 的数据库，也不要混用不同时间的数据库和照片。

## 8. 手机连通、防火墙与固定地址

手机与电脑应在能互通的局域网或蒲公英网络。先确认首页监听包含所需网卡，再使用对应 IPv4 和实际端口；仅开放防火墙不会改变「仅本机」监听。电脑睡眠、访客 Wi-Fi 设备隔离或网卡地址变化都会中断访问。工具不负责建立蒲公英连接，也不调用其云接口。

Windows 可在管理员 PowerShell 放行实际端口，以下规则覆盖专用 / 域网络，参数见 [New-NetFirewallRule 文档](https://learn.microsoft.com/en-us/powershell/module/netsecurity/new-netfirewallrule)：

```powershell
New-NetFirewallRule -DisplayName "街道环保工具 TCP 5180" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5180 -Profile Private,Domain
Get-NetConnectionProfile
```

也可在「Windows Defender 防火墙 → 高级设置 → 入站规则 → 新建规则 → 端口」添加 TCP 5180。改端口后同步修改规则。蒲公英虚拟网卡可能属于 Public 配置文件；先确认实际配置，再只为所需虚拟接口或地址范围添加相应规则，不必关闭整个防火墙。

固定地址优先在路由器中为电脑网卡设置 DHCP 地址保留 / 静态租约。无法配置路由器时，由网络维护人员分配不冲突的 IPv4、掩码、网关和 DNS。变更地址后从本机监听浮层重新选择有效网卡，并更新手机书签。

## 9. 开机自启

### Windows

为 `StreetEcoTool-win-x86.exe` 创建快捷方式，`Win+R` 输入 `shell:startup`，把快捷方式放入启动文件夹。例如目标为 `"D:\hbtool\StreetEcoTool-win-x86.exe" --no-browser`，会在当前用户登录后启动，移除快捷方式即可取消。局域网监听可事先在界面保存；需要命令行固定监听时在快捷方式后加 `--listen 0.0.0.0` 或所需网卡。

无人登录时运行可用「任务计划程序」：触发器选启动时，操作指向完整程序路径，参数填 `--no-browser`，选择对目录有写权限的账户，按需要设为不管用户是否登录都运行；多实例策略选不启动新实例，不要设为运行若干小时后停止。建立后手动运行并确认手机可访问。

### Linux systemd（Ubuntu ARM64 示例）

先停止手工实例，把发布包放在 `/opt/hbtool`；以下命令在账户不存在时创建专用运行账户，并配置目录权限和时区数据：

```sh
sudo useradd --system --user-group --home-dir /opt/hbtool --shell /usr/sbin/nologin hbtool
sudo chown -R hbtool:hbtool /opt/hbtool
sudo chmod +x /opt/hbtool/StreetEcoTool-linux-arm64
sudo apt-get install -y tzdata
```

把以下内容保存为 `/etc/systemd/system/street-eco-tool.service`。示例明确监听所有网卡，时区采用中国标准时间：

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
Environment=TZ=Asia/Shanghai
ExecStart=/opt/hbtool/StreetEcoTool-linux-arm64 --no-browser --listen 0.0.0.0
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
```

正常停止用 `sudo systemctl stop street-eco-tool`，SIGTERM 会触发正常关闭。修改程序路径、端口或服务文件后重启；修改服务文件还需 `daemon-reload`。如要由 `appsettings.json` 管理监听，删除 ExecStart 的 `--listen` 覆盖参数，并先在配置中明确要使用的监听地址。

## 10. 接口与项目结构

接口使用 JSON，删除业务对象须带 `?confirm=true`。主要新增与调整如下，精确 DTO 见 [Contracts.cs](server/Contracts.cs)，监听 DTO 见 [Listening.cs](server/Listening.cs)：

| 方法与路径 | v2 行为 |
| --- | --- |
| `GET /api/bootstrap` | 增加整治类型字典 |
| `GET /api/dashboard` | 保留问题提醒数量，增加排查待办与最近记录 |
| `GET /api/points` | `temporary=false/true/all`，默认常态；返回坐标、性质、封面和照片数 |
| `POST /api/points`、`PUT /api/points/{id}` | 坐标及点位照片；临时点位只能随新增排查创建 |
| `POST /api/points/{id}/promote` | 临时点位转常态并设置频次 |
| `GET /api/inspections` | 整改汇总、闭环 / 逾期数、最近期限，支持待复查问题 `awaitingReview`、未闭环问题 `openOnly` 等筛选，返回匹配问题数 |
| `GET /api/inspections/counts` | 按其他筛选条件统计各整改状态与问题数，不受当前 `rectify` 限制 |
| `POST /api/inspections` | `pointId` 与 `newPoint` 二选一，临时点位与排查原子创建 |
| `GET /api/inspections/{id}` | 逐项问题详情、照片和办理过程 |
| `PUT /api/inspections/{id}`、`DELETE /api/inspections/{id}` | 编辑基本信息；删除关联问题、办理、主图、缩略图及无剩余排查的临时点位 |
| `POST /api/inspections/{id}/feedback`、`POST /api/inspections/{id}/review` | 批量反馈 / 复查，整批校验和事务回滚 |
| `GET /api/campaign-types`、`POST /api/campaign-types`、`PUT/DELETE /api/campaign-types/{id}` | 整治类型字典与引用保护 |
| `GET/POST /api/campaigns`、`GET/PUT/DELETE /api/campaigns/{id}` | 整治查询及增删改，类型 / 社区快照，三阶段照片 |
| `PUT /api/photos/{id}/thumb` | JPEG 原始请求体，最多 200 KiB、长边 360 px |
| `GET /photos/{id}?size=thumb` | 有缩略图返回缩略图，否则返回主图 |
| `GET /api/system` | 当前端口、监听、网卡、可访问地址、失效已保存地址及 `canConfigure` |
| `PUT /api/system/listen` | `{ "listen": [...] }`，只允许回环请求，保存配置并应用监听 |

点位和整治编辑提交的照片 ID 可追加或保留已经属于该对象的照片，省略某张照片不会隐式删除它；移除已有照片使用独立删除接口。原单问题查询、编辑、反馈、复查、退回及备份接口保留兼容。

Word 接口均为 GET：

| 路径 | 文档 |
| --- | --- |
| `/api/inspections/{id}/export/record` | 排查记录单 |
| `/api/inspections/{id}/export/notice` | 整改通知单 |
| `/api/inspections/{id}/export/rectification` | 整改记录单，`rectification-record` 为别名 |
| `/api/campaigns/{id}/export/record` | 整治记录单 |
| `/api/exports/inspection-ledger` | 排查台账 |
| `/api/exports/rectification-ledger` | 整改台账，原 `/api/exports/ledger` 为别名 |
| `/api/exports/campaign-ledger` | 整治台账 |

台账参数 `from`、`to` 为日期范围，`communityId` 可空；整改 / 整治支持 `photos=true`，整治另支持 `typeId`。不带分页限制。

`server` 为 Minimal API、参数化 SQLite SQL、迁移和 OOXML 生成；`server/StreetEcoTool.Tests` 为单元测试与夹具；`web` 为 Nuxt SPA；`tests` 为实际进程 API / Word 验收；`scripts` 为打包；`.github/workflows` 为 CI；`spikes` 保存先行技术验证。

## 11. 本地开发与原生构建

依赖以 [global.json](global.json)、[后端项目](server/StreetEcoTool.csproj)、[前端 package.json](web/package.json) 和锁文件为准：.NET SDK **10.0.401**（同特性带 latestPatch）、Microsoft.Data.Sqlite **10.0.12**、SQLitePCLRaw.bundle_e_sqlite3 **2.1.12**、StaticLink.e_sqlite3 **2.1.10.1**；Nuxt **4.6.1**、Nuxt UI **4.11.3**、Tailwind **4.3.3**、TypeScript **6.0.3**、jSquash JPEG **1.6.0** / Resize **2.1.1**。CI 使用 Node.js **24**；测试和打包使用 Python **3.10+**。

两个终端分别运行：

```sh
# 终端 1，仓库根目录，使用 JIT
dotnet run --project server/StreetEcoTool.csproj -p:PublishAot=false -- --no-browser
```

```sh
# 终端 2，仓库根目录
cd web
npm ci
npm run dev
```

开发页面为 `http://localhost:3000`，`nuxt dev` 将 `/api` 和 `/photos` 代理到 `127.0.0.1:5180`；后端改端口时同步调整 `web/nuxt.config.ts`。JIT 数据位于实际输出目录（通常 `server/bin/Debug/net10.0/data`），照片 / 备份 / 临时目录与它同级，和发布包隔离。JIT 使用 bundle 动态 SQLite，AOT 使用静态 SQLite；v1 AOT 已实测 SQLite **3.46.1**，本轮最终产物仍需核验。

生产采用 `ssr:false`、hash 路由、中文浅色 Nuxt UI、系统字体、本地图标与本地 WASM；`nuxt generate` 输出 `.output/public`，由后端静态服务承载。无需部署 Nitro、Pinia 或 HTTPS 专属浏览器能力，背景见 [Nuxt 客户端渲染部署文档](https://nuxt.com/docs/4.x/getting-started/deployment#client-side-only-rendering)。

先生成一份前端：

```sh
npm --prefix web ci
npm --prefix web run typecheck
npm --prefix web run generate
```

Windows x86 在 Windows 构建。安装 .NET SDK 和 Visual Studio 2022 或更新版的 C++ 桌面工具链（x86 MSVC、Windows SDK），从 x86 Native Tools 命令环境执行：

```powershell
dotnet publish server/StreetEcoTool.csproj -c Release -r win-x86 -o publish/win-x86 -p:PublishAot=true -p:SelfContained=true -warnaserror
```

Linux ARM64 在 Ubuntu 24.04 arm64 原生构建（`uname -m` 为 `aarch64`）：

```sh
sudo apt-get update
sudo apt-get install -y clang zlib1g-dev
dotnet publish server/StreetEcoTool.csproj -c Release -r linux-arm64 -o publish/linux-arm64 -p:PublishAot=true -p:SelfContained=true -warnaserror
```

NativeAOT 不跨操作系统编译，本项目 CI 使用目标架构原生宿主。把两个宿主生成的可执行文件和同一份前端收集到打包主机，再执行：

```sh
python3 scripts/package.py --win-x86 publish/win-x86/StreetEcoTool.exe --linux-arm64 publish/linux-arm64/StreetEcoTool --web-static web/.output/public --output dist
```

输出只有 `dist/hbtool.zip` 和 `dist/hbtool.zip.sha256`。打包校验 Windows PE 的 x86 架构、Linux ELF 的 ARM64 架构、ZIP 完整性以及 Linux 权限 755；包中只选取两份程序与一份 `wwwroot`，不带 `appsettings.json`、数据、调试符号、独立 SQLite 库或 Linux x64 验证程序。

Linux 在 `dist` 中运行 `sha256sum -c hbtool.zip.sha256`；Windows 用 `Get-FileHash .\dist\hbtool.zip -Algorithm SHA256` 对照校验文件。CI 先构建 `web-static` 与三个 `bin-*`，`package` 汇总 Windows x86 / Linux ARM64，`browser` 单独使用 Linux x64 加同一份静态资源。发布附件保留 30 天，测试证据保留 14 天，需要长期保存的产物请下载留存。

## 12. 复现验收与既有证据

复现前关闭本机正在运行的工具。测试脚本会复制程序到隔离目录自行启动 / 停止，不使用正式数据；浏览器验收的程序旁需要完整 `wwwroot`。以下以 Linux ARM64 构建目录为例，Windows 替换程序路径；CI 浏览器环境使用 Linux x64：

```sh
dotnet test server/StreetEcoTool.Tests/StreetEcoTool.Tests.csproj -c Release -p:PublishAot=false -warnaserror
python3 tests/api-smoke.py --executable publish/linux-arm64/StreetEcoTool --report test-results/api-smoke-report.json
python3 tests/word-smoke.py --executable publish/linux-arm64/StreetEcoTool --report test-results/word-smoke-report.json
mkdir -p publish/linux-arm64/wwwroot
cp -a web/.output/public/. publish/linux-arm64/wwwroot/
cd web
npx playwright install --with-deps chromium
node browser-test.mjs --executable ../publish/linux-arm64/StreetEcoTool
```

v2 新增或调整的验收包括：真实 v1 数据库迁移与回滚、新旧目录合并、旧 ZIP 恢复、午夜快照与跨日写入顺序、临时点位及转常态、级联删除、经纬度、主图 / 缩略图、批量反馈 / 复查原子性、整治、统计口径、监听重绑与非回环 403、七种 Word、全站禁用文案扫描、移动布局和断外网运行。AOT / 裁剪及其他构建警告均视为错误，保留 `-warnaserror`。

截图目录为 `web/test-results/screens/`，九个页面各生成 **375×812、768×1024、1440×900** 三种尺寸，预期 27 张。文件名为下表前缀加 `-宽x高.png`：

| 页面 | 截图前缀 |
| --- | --- |
| 首页 | `home` |
| 点位列表 | `points` |
| 点位详情 | `point-detail` |
| 排查列表 | `inspections` |
| 新增排查 | `inspection-new` |
| 排查详情（多个问题） | `inspection-detail` |
| 整治列表 | `campaigns` |
| 新增整治 | `campaign-new` |
| 设置 | `settings` |

测试另在 360 px 宽度检查无横向滚动，检查移动点击区域至少 40×40、正文文字至少 12 px（底部 tab 例外）、移动首页首屏统计与首条待办、375 px 排查首屏至少 5 条、桌面页头不超过 64 px、移动顶栏 48 px、移动筛选两列。截图须逐张目视复核，自动断言不能替代看图。原始结果写入 `web/test-results/browser-report.json` 并由 `browser-evidence` 附件保存。

### v1 已完成的基线，不能当作 v2 测试结果

三个先行验证已有独立记录：[AOT + SQLite 三平台](spikes/aot-sqlite/RESULTS.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360)）、[Nuxt 离线 / hash 路由](spikes/nuxt-offline/README.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38050309537)）、[jSquash 方向 / 压缩 / 回退](spikes/photo-compression/RESULTS.md)（[CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38050177110)）。图像验证中约 1200 万像素合成样本由 7,946,458 字节压到 301,622 字节，并覆盖八种 EXIF 方向和 WASM 失败回退；这只是该固定样本的数据。

本轮基于 `eec6117` 源码继续改造。[v1 最终工作流](https://github.com/SunSi12138/build-sandbox/actions/runs/38054839889)记录 **103 个单元测试、3 个 RID 的零警告 AOT 发布、各平台 31 次 Word API 请求、9 组完整浏览器流程**。这些是 v1 数字，包含当时三种 Word 下载，不是 v2 的测试总数或七种导出通过证明。

v1 曾使用 LibreOffice 对六份含长文本、照片的 DOCX 转 PDF 并目视检查，使用 Noto CJK 替代字体。v2 的新文档样本须单独复验。有 LibreOffice 和中文字体时可运行：

```sh
libreoffice --headless --convert-to pdf --outdir test-results/word-pdf test-results/word-samples/*.docx
```

## 13. 本轮 v2 验收结果

本轮验收代码为 [83769259](https://github.com/SunSi12138/build-sandbox/commit/83769259e434a5dbff9d4f0b36d2fa36cde3c345)，对应 [完整 CI](https://github.com/SunSi12138/build-sandbox/actions/runs/38238235193)。以下数字来自该次实际运行；测试使用隔离目录及示例数据。

| 项目 | 本轮结果与证据 |
| --- | --- |
| 单元测试 | 151 / 151 通过，失败 0、跳过 0；Linux x64 附件的 `unit-tests.trx` |
| NativeAOT | Windows x86、Linux ARM64、Linux x64 均通过 `-warnaserror` 发布，AOT 警告 0 |
| 三平台 API | 每个平台 11 组、283 次请求全部通过；覆盖 v1 迁移/恢复、旧目录、中断恢复、批量回滚及级联删除 |
| 监听实际连接 | 三平台均验证 IPv6 回环、同进程重绑、非回环 403、CLI 覆盖及失效 IP 回退；每个平台另有 3 次已移除监听地址拒绝 TCP 连接的记录 |
| 七种 Word | 每个平台 5 组、54 次请求通过；覆盖闭环导出门禁、七种单据/台账和 OOXML 字体、行距、表格、照片及汇总断言 |
| Nuxt | 类型检查与静态构建通过；保持原锁定依赖 |
| 真实浏览器 | Chromium 151.0.7922.34，14 组流程全部通过；包含草稿、压缩回退、临时点位、按问题反馈、整治、7 个 Word 下载入口、监听及恢复 |
| 27 张截图 | 第 12 节九个页面 × 三个指定视口，全部逐张按原尺寸审阅；文件均在 `web/test-results/screens/` |
| 36 组布局测量 | 九个页面 × 360、375、768、1440 宽度；无页面横向滚动；手机目标 ≥40×40、正文 ≥12px（底部 tab 11px）；首页首屏完整显示四项统计与首条待办；375px 排查首屏实际 6 条；顶栏 48px、桌面页头 ≤64px、手机筛选两列 |
| 看图后补充回归 | 平板/桌面两个日期筛选可见，手机默认折叠；经纬度六位精度且完整单行；三个桌面列表的普通样例行高 44px；设置页同排输入框对齐 |
| 离线与错误 | 外部请求 0，未处理页面错误 0；本地图标、系统字体栈及 WASM/回退压缩通过；测试程序正常退出 |
| 统一发布包 | `hbtool.zip` + SHA-256；一个 `hbtool/` 根目录、两个指定平台程序、一份 `wwwroot`，75 个文件；PE x86、ELF64 ARM64、ZIP CRC、Linux 755 均通过，不带用户配置或 Linux x64 |
| 原生依赖检查 | Windows 仅导入系统 DLL/UCRT；ARM64 动态依赖为 libc、libm 和加载器，最高 GLIBC 符号版本 2.38；没有独立 SQLite 库 |
| Word 渲染 | 相同 Word 源码生成 12 份样例，LibreOfficeDev 26.8 + Noto CJK 回退渲染，覆盖 52 页；普通 20 页与已审图逐字节一致，其余新页面逐张审阅或比对到同一已审图；两份长台账均完整提取 3,640/3,640 字 |

完整交付证据包包含三平台 JSON/TRX、浏览器报告、27 张页面图、Word 样例/PDF/页面图、逐图审阅及包结构/SHA 校验结果。GitHub 附件内的原始结果分别位于 `test-results-{rid}`、`browser-evidence` 与 `hbtool`。

### 本轮处理决定

手机统计数字采用 20px、桌面 28px，按第 4.8 节的上限处理与 4.2/4.7 节的冲突。先上传后关联的照片允许暂时零归属，业务保存后唯一归属；目录同名冲突保留源文件，因此冲突未处理前旧目录可能暂留。首页补充 `awaitingReview` / `openOnly` 问题级筛选，以匹配混合问题状态的数量。超长 Word 表格行按相同序号续行，保留全文，并让每个物理行保持不跨页拆分。

**实际验收边界：**未在 Microsoft Word 或 WPS 图形界面打开七种文档，不能宣称这两种软件无修复或更新域提示；OOXML 检查及 LibreOffice 转换不替代该项人工验收。最低浏览器版本、真实 iOS/Android 设备、真实手机拍照/相册、蒲公英实网及每一种目标 Linux 发行版没有逐一实测。可选二维码按钮未实现。

## 14. 常见问题

| 现象 | 处理办法 |
| --- | --- |
| 电脑可打开，手机连不上 | 先确认监听已包含手机可达的网卡，地址不是 localhost，再检查端口、Wi-Fi 隔离、电脑睡眠和网卡防火墙配置 |
| 在办公室电脑上也不能改监听 | 使用回环网址 localhost / 127.0.0.1 打开；以本机局域网 IP 访问仍属于非回环请求 |
| 保存过的 IP 不见了 | DHCP 或网卡发生变化，程序可能已回退仅本机；从本机选择当前有效地址 |
| 改端口无效 / 提示端口占用 | 关闭旧实例，检查启动命令是否覆盖配置，修改端口后重启并更新防火墙 |
| 换网址后草稿不见了 | 回到原协议、主机、端口和浏览器查看；已正式保存的记录在服务器，草稿不跨网址同步 |
| 照片失败或旧缩略图不存在 | 检查磁盘、目录权限和网络；主图超 10 MiB 时调低质量；特殊格式先转换，旧图缺缩略图会回退主图 |
| 整改记录单不能下载 | 查看该次是否仍有未闭环问题，正常排查应下载排查记录单 |
| 升级后照片缺失 | v2 照片在根目录 `photos`，不能只迁移 `data`；保留现有数据后核对旧目录冲突日志和完整备份 |
| 更新后空白或样式异常 | 替换完整 `wwwroot`，使用兼容浏览器并刷新；入口 HTML 禁缓存，带 hash 的资源长期缓存，不混用两版静态文件 |
| 无法写入 / Linux GLIBC 不匹配 | 检查运行账户目录权限和磁盘；系统库不足时使用符合构建基线的系统或在目标受支持系统重建，安装 .NET Runtime 不能替代系统库 |
