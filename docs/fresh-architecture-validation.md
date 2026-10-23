# 新模型实现与验证报告

日期：2026-10-23

## 已交付

- 事项办理独立模块：登记、过程记录、回复、移交、办结和重新办理；与排查、问题、活动分别关联。
- 设置统一管理标签：新增、改名、停用、引用保护；多标签同时匹配和跨模块查看。
- 点位原文件附件：上传、预览/下载、改名、备注、确认删除；完整备份及恢复包含附件。
- 活动与整治统一：整治为内置专用活动模板，保留点位、数量、阶段照片和专用Word导出；普通活动与整治共享列表及通用台账。
- 新数据库结构版本1；移除旧版转换工具、独立整治入口及旧接口，不提供旧版兼容。

## 验证结果

| 验证 | 结果 |
|---|---|
| 后端完整测试 | 260通过，0失败，0跳过 |
| 原生程序API集成 | 12组通过，322次请求；另1组主机网卡监听检查明确跳过 |
| Word导出集成 | 6组通过，63次请求 |
| Word布局样本生成与结构检查 | 30份DOCX，138次请求 |
| 浏览器完整流程 | 38组通过，33张响应式截图 |
| 浏览器运行错误 | 无外部资源请求、页面错误或意外控制台错误 |
| 程序退出 | 浏览器测试结束后正常退出，退出码0 |
| 前端类型检查与静态构建 | 通过 |
| macOS ARM64 NativeAOT发布 | 通过 |
| 源码差异格式检查 | 通过 |

## 需求对应证据

| 需求 | 自动化证据 |
|---|---|
| 事项办理 | `ResolvedClosureRequiresClosedIssuesAndReopenPreservesChronologicalHistory`；浏览器N02/N03 |
| 统一标签 | `TagFiltersUseAndSemanticsAndCountDistinctOwnersAcrossAllModules`；浏览器N01/N06 |
| 点位附件 | `AttachmentLimitsIncludeExactMaximumAndRejectedWritesLeaveExistingFileUntouched`；浏览器N04原字节下载比对 |
| 活动整治合并 | `UnifiedRemediationOwnsStagedPhotosLinksIssuesAndAppearsInPointAndMatterDetails`；普通/整治创建和导出浏览器流程 |
| 完整备份 | `FullBackupRestoresMatterHistoryTagRelationsAndUnifiedActivityTemplate`，附件损坏、越界、遗漏与恢复中断检查 |
| 关联保存原子性 | `CombinedRelationsRollbackBothAssociationsWhenLaterMatterValidationFailsThenCommitTogether` |

## 本轮发现并修复

- 保留历史停用点位的活动编辑受阻。
- 数量字段的舍入、数值草稿恢复和零值保存。
- 标签修改时错误覆盖“其他”排查来源。
- 取消活动编辑时已保存照片被提前删除；现在保存活动时才移除，并验证普通与整治三阶段照片。
- 浏览器测试未完整读取照片响应导致退出等待；测试已读取并比对完整文件，最终正常退出。

## 验证边界

所有测试使用隔离数据目录，没有修改用户业务数据。API主机网卡监听检查因本机非回环连接限制显式跳过；浏览器中的监听设置与实时切换流程通过。30份Word样本已生成并检查结构，本机未执行LibreOffice逐页渲染。Windows/Linux发布与CI中的Word渲染以远端流水线结果为准。macOS发布保留既有SQLite静态库最低系统版本链接警告。未执行变异测试，也不声称行/分支覆盖率。
