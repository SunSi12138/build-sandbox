# 活动与问题分类升级验证（2026-10-23）

> 历史实施记录。当前功能与数据结构以 [新版使用说明](new-application.md) 为准。

## 核对基线

主分支 `hbtool`：`8552190`。已有升级分支 `hbtool-activities-schema1`：`f520225`，对应 PR #2。本轮在已有功能基础上完成实际运行验证和遗漏修复。

## 本轮修复

- 活动表单跨社区选项改为非空选择值，修复刷新草稿后页面报错；提交仍保存空社区。
- 恢复预览显示活动数量；活动列表日期筛选补充可访问名称及统一测试标识。
- 独立转换工具在私有副本处理数据库/WAL，显式关闭连接，避免修改源目录及 Windows 文件占用；补齐活动索引和旧点位新增列约束，保留用户配置。
- 发布包附带转换工具和中文使用说明。
- 回归测试同步新版问题类型必填字段、v3 排查草稿、十列整改台账及 33 张截图；九种 Word 导出均通过浏览器实际下载。
- 增补旧版 1/2/3 的真实结构夹具及转换回归，并接入 CI；保留旧基线文档测试的数据契约。

## 本机验证结果

环境：macOS ARM64，局部安装的 .NET SDK 10.0.401，Node.js 24.20.0，Chromium 151。

| 验证 | 结果 |
| --- | --- |
| .NET 测试 | Passed: 186, Failed: 0, Skipped: 0 |
| 独立转换测试 | 6 个测试通过，覆盖旧结构 1/2/3、数据/照片保留、索引/字段一致、WAL、重复转换、未来版本、缺失照片与拒绝覆盖 |
| 转换后启动 | 三个旧版夹具分别转换，新版 NativeAOT 程序成功打开，点位及新增字典核对通过 |
| 前端类型检查及静态生成 | 通过 |
| macOS ARM64 NativeAOT 构建 | 成功；本地链接器提示静态 SQLite 的最低 macOS 版本高于链接目标，此构建仅用于本机测试 |
| API 业务回归 | 11 组场景、271 次请求通过；问题分类、活动 CRUD、照片导出、备份恢复、重启恢复均覆盖 |
| Word API 回归 | 6 组场景、62 次请求通过 |
| 浏览器回归 | 27 组场景、33 张截图通过，外部资源请求 0；覆盖九种下载、草稿恢复、照片、监听切换和完整备份恢复 |
| 打包逻辑 | 使用独立二进制头夹具验证 ZIP CRC、工具/说明包含且字节一致；不是 Windows/Linux 运行验证 |

完整 API 脚本在 `test_listening` 的本机非回环网卡 HTTP 请求处超时，保留失败报告。其余 11 组业务用原有 `ApiSmoke.run` 执行，仅在测试调度层排除该网络场景；仓库脚本和 CI 没有默认跳过网络测试。浏览器中的本机监听切换测试通过。不能把业务子集结果描述为完整 API 全绿。

Windows x86、Linux ARM64 原生构建/运行及 LibreOffice 排版对比需以 PR CI 为准；本机结果不代表这些平台已经验收，也没有修改用户真实数据库。

## 验收对应

| 用户要求 | 证据 |
| --- | --- |
| 排查问题类型与设置管理 | `ApiSmoke.test_activities_and_types`：必填、保存、筛选、改名快照、引用删除保护 |
| 新增活动记录 Tab/CRUD/导出 | `ApiSmoke.test_activities_and_types`；浏览器 `Activity creation: type management, local photo compression, draft and Word record`、`Nine Word exports download through the current UI` |
| 独立旧数据库一次性转换工具 | `ConversionTests.test_legacy_versions_preserve_data_and_match_current_schema`、`test_missing_photo_removes_output_and_preserves_source`、`test_existing_output_is_not_overwritten`、`test_repeated_conversion_is_refused`、`test_future_version_is_refused`、`test_wal_commits_are_copied_without_touching_source` |
| 将新数据库版本统一设为 1 | `ConversionTests.test_legacy_versions_preserve_data_and_match_current_schema` 的显式版本断言，以及 `CurrentSchemaTests.EmptyDatabaseInitializesCurrentSchemaOnceAndReopensWithoutChangingData` |

成功执行的常规命令（本机使用局部 SDK 路径及 Node 24.20.0）：

```sh
dotnet test server/StreetEcoTool.Tests/StreetEcoTool.Tests.csproj -c Release -p:PublishAot=false --logger 'trx;LogFileName=unit-tests.trx' --results-directory test-results -warnaserror
python3 -m unittest discover -s tests -p 'test_convert_database.py' -v
npm --prefix web run typecheck
npm --prefix web run generate
python3 tests/word-smoke.py --executable publish/osx-arm64/StreetEcoTool --report test-results/word.json
HBTOOL_WEB_ROOT="$PWD/web/.output/public" node web/browser-test.mjs --executable publish/osx-arm64/StreetEcoTool
```
