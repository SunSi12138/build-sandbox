# Spike 1：NativeAOT + SQLite

## 结论：通过

2026-10-10，提交 `8c853cd6a35a32af6b552e4329a6406cc659426b` 的[实际 CI 运行](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360)全部通过。没有修改既定的 .NET / SQLite 选型。

| 平台 | 原生构建环境 | 零警告 AOT 发布 | 存储自测与跨进程持久化 | Kestrel GET / POST |
| --- | --- | --- | --- | --- |
| win-x86 | Windows Server 2022 + VS C++ x86 | 通过 | 通过 | 通过 |
| linux-arm64 | Ubuntu 24.04 arm64 | 通过 | 通过 | 通过 |
| linux-x64（额外演示基线） | Ubuntu 22.04 x64 | 通过 | 通过 | 通过 |

日志证据：[Windows x86](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360/job/114206884089)、[Linux arm64](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360/job/114206884145)、[Linux x64](https://github.com/SunSi12138/build-sandbox/actions/runs/38049947360/job/114206883972)。三个平台实际输出的 SQLite 版本均为 **3.46.1**。

Linux x64 AOT 产物下载到本会话环境后亦通过了 SQLite 自测、HTTP GET / POST、独立 Python SQLite 连接读取 POST 新增记录和 SIGINT 正常关闭。再把**唯一一个可执行文件**复制到空目录，在没有任何配套 DLL、SO、Runtime 或静态资源清单文件的情况下，同样成功执行自测并启动 Kestrel。`ldd` 仅列出系统 `libc`、`libm` 和动态加载器，没有外部 SQLite 依赖。

## 验证范围

最小 ASP.NET Core Minimal API，使用 `WebApplication.CreateSlimBuilder`，所有 JSON 请求和响应显式传入 System.Text.Json 源生成的 `JsonTypeInfo`。不使用 EF Core、运行时请求绑定或动态代码生成。`InvariantGlobalization` 开启；应用日期按本地时间写入 `yyyy-MM-dd` 文本。

`TreatWarningsAsErrors`、`ILLinkTreatWarningsAsErrors`、`IlcTreatWarningsAsErrors` 均开启。未通过静默抑制 IL2xxx / IL3xxx 警告达成验证。

## 固定依赖

| 依赖 | 版本 |
| --- | --- |
| .NET SDK（本地下载） | 10.0.401 |
| .NET Runtime | 10.0.12 |
| Microsoft.Data.Sqlite | 10.0.12 |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 |
| StaticLink.e_sqlite3 | 2.1.10.1 |

`StaticLink.e_sqlite3` 自带 SQLite **3.46.1**。本项目选择该包满足指定静态链接方案；发布后自测已确认三个平台均为此版本。JIT 模式仍使用 bundle 所提供的动态 SQLite，因此开发和发布的引擎版本可能不同；业务 SQL 只使用两个版本均具备的基础功能。

## 已核实的包实体

官方 NuGet 下载的 `StaticLink.e_sqlite3 2.1.10.1`：

- 下载大小：25,933,782 字节。
- SHA-256：`fbf7a44a3a04204c174afdbfd3489c7b40b50a360ed3a08f754278c4ebdcedf3`。
- `static/win-x86/native/e_sqlite3.lib`：7,760,396 字节；`objdump` 确认内部 `sqlite3.obj` 为 `pe-i386`。
- `static/linux-arm64/native/libe_sqlite3.a`：11,085,968 字节；`readelf` 确认内部 `sqlite3.o` 为 `ELF64 / AArch64`。
- 包内 `buildTransitive/StaticLink.e_sqlite3.targets` 在 `PublishAot=true` 时按 RID 加入 `NativeLibrary` 和 `DirectPInvoke`，并从发布目录清理 `e_sqlite3.dll` / `libe_sqlite3.so`。

这些检查确认包确实携带正确架构的静态库；不代替目标平台运行验证。

## 自测内容

`--self-test --expected-count N` 在程序目录创建 SQLite 数据库并执行：

1. 使用参数化 SQL 写入含中文和单引号的点位名称，事务内新增排查记录。
2. 读取并比较名称和日期。
3. 检查所有既有测试记录的内容和日期格式，并比较总数。CI 连续运行两次，分别要求总数为 1 和 2，以验证跨进程持久化。
4. 插入不存在点位的排查，要求出现外键约束错误。
5. 要求 `PRAGMA journal_mode` 为 `wal`。
6. 用参数化 `VACUUM INTO` 创建一致性快照，再用独立连接执行 `PRAGMA integrity_check`。
7. 用源生成 JSON 上下文进行序列化与反序列化往返验证。
8. CI 启动发布后的 Kestrel，实际请求 GET / POST JSON 接口。
9. CI 要求发布目录不含独立的 SQLite 动态库。

## 构建与验证

Windows 必须在 Windows 上安装 VS C++ x86 工具链后运行：

```powershell
dotnet publish spikes/aot-sqlite/AotSqliteSpike.csproj -c Release -r win-x86 -o publish -p:PublishAot=true -p:SelfContained=true -warnaserror
./publish/AotSqliteSpike.exe --self-test --expected-count 1
./publish/AotSqliteSpike.exe --self-test --expected-count 2
```

Linux arm64 在 arm64 Linux 上运行：

```sh
sudo apt-get install clang zlib1g-dev
dotnet publish spikes/aot-sqlite/AotSqliteSpike.csproj -c Release -r linux-arm64 -o publish -p:PublishAot=true -p:SelfContained=true -warnaserror
./publish/AotSqliteSpike --self-test --expected-count 1
./publish/AotSqliteSpike --self-test --expected-count 2
```

`aot-spike.yml` 是可放入 `.github/workflows/` 的工作流。矩阵覆盖 Windows x86、Linux arm64，并包含 Linux x64 的本地演示基线。

## 编译中验证出的实现约束

实际使用 .NET SDK 10.0.401 编译时，以下两点需要显式处理：

- `HttpResponse.WriteAsJsonAsync` 的源生成 `JsonTypeInfo` 重载需要显式指定 `contentType: null, cancellationToken: ...`，避免选择接受 `JsonSerializerOptions` 的重载。
- 把 `async (HttpContext context) => ...` 直接传给 `MapPost` 时，请求委托生成器在本次编译产生 CS9144（生成的拦截器签名与 `RequestDelegate` 重载不匹配）。先赋值给显式 `RequestDelegate` 变量，再调用 `MapPost(path, variable)`，让路由直接使用该请求委托。请求体依然手工调用源生成 JSON 读取重载；没有改用反射绑定，没有屏蔽警告。

## 当前执行环境记录

本会话环境是 x86_64 Linux。SDK 下载完成；`dotnet` 原生宿主可以列出 SDK / Runtime，但启动 CoreCLR 返回 `HRESULT 0x8007000E`，因此本机 JIT / AOT 编译均未开始。已尝试仅针对当前进程设置本地临时目录、关闭诊断、限制 GC 堆，以及运行时兼容开关，均未解决。没有修改机器安全规则。使用已有 GCC 构建并执行最小原生程序成功，随后下载 CI 生成的 Linux x64 AOT 产物，已成功在本机运行，证明当前限制只阻止本地 JIT / SDK 使用，没有阻止部署后的 NativeAOT 程序。

本地验证使用 artifact `11669076384`（`aot-sqlite-spike-linux-x64`），下载 ZIP 的 SHA-256 为 `7b22a976d272fa3828695577ff6ec3d2ca3de4ecb9388ead1081401ed860f53c`，与 GitHub 返回的摘要一致。

原始 `dotnet publish` 会额外生成调试符号和 `*.staticwebassets.endpoints.json`；上面的“唯一可执行文件”实验确认本应用并不依赖这些文件。正式打包应只选取程序文件与前端 `wwwroot/`，不要把测试数据库打入用户安装包。

## 上游来源

- [StaticLink.e_sqlite3 2.1.10.1 官方 NuGet 页面](https://www.nuget.org/packages/StaticLink.e_sqlite3/2.1.10.1)
- [Microsoft.Data.Sqlite 官方 NuGet 页面](https://www.nuget.org/packages/Microsoft.Data.Sqlite)
- [SQLitePCLRaw 官方仓库](https://github.com/ericsink/SQLitePCL.raw)
- [.NET NativeAOT 官方说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [.NET NativeAOT 跨平台与跨架构约束](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile)
