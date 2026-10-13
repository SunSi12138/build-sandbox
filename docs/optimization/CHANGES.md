# 优化变更与验证说明

执行依据为2026-10-13实施任务书、DOCX专项提示词和审计报告，原始审计基线05eba126e7b9f3de13e5a2b055624ac6f97ee76d。只做已明确修复，不改变D01—D04业务决策。最终构建和原始证据以同一提交CI为准，不以旧README结果替代新测试。

## 问题对应与删除范围

| 编号 | 修改文件／范围 | 结果与边界 |
|---|---|---|
| F01/P0 | Store.Points.cs、Api.cs、Contracts/AppJsonContext、points/[id].vue | 照片关联和停用局部写入，不再携带旧点位快照；锁内事务、9张上限、阶段和归属保留 |
| F02 | PhotoUploader.vue、optimization-browser-test.mjs | 取消先失效并abort；await后检查；删除失败保留可重试引用；导航真正生效／卸载清理尚未发布照片；缩略图可选降级保留 |
| F03 | CampaignEditor.vue、inspections/new.vue、inspections/[id].vue | 加载及保存字段、选择器、模式冻结；失败保留输入和草稿 |
| F04 | InspectionBatch.vue | 空表直接关闭；实际改动才确认放弃；取消、Escape、关闭使用同一处理；失败不重置 |
| F05 | inspections/[id].vue | 明确最后一条临时排查会删除临时点位和点位照片；不改变级联规则 |
| F06 | Database.cs、BackupStore.cs、StoreV2Tests.cs、api-smoke.py | 直接初始化当前schema2；旧／未来版本与旧布局无损拒绝；删除旧迁移链、目录合并、旧ZIP／日志兼容和专属夹具，保留混合测试文件 |
| F07 | LedgerExport.vue及两列表 | 实际生效条件全部可见，整治类型可调整；日期独立，明确未继承条件 |
| F08 | 排查新增／编辑 | 最大100字，与后端一致；不截短已存数据 |
| F09/W06 | Store.Settings.cs、WordExporter.cs、settings.vue | 六变量提示，重复问题块保存／导出明确拒绝，不重置存量模板；没有问题块时补表 |
| F10 | SourceCopyTests.cs、word-smoke.py | 去掉全局普通词黑名单；保留具体危险提示及颜色断言，合法业务原文增加保存和导出测试；注释剥离器仍被颜色检查调用，保留 |
| F11 | WordStore.cs、Store.Issues/Batches.cs、页面／测试调用 | 去掉旧issues/exports前端跳转壳及重复Word别名；单项调用共用批量规则；问题详情/编辑/退回、示例仍可用 |
| F12/W02 | WordExporter.cs | 长表续行带日期、点位／记录（续）；照片表重复问题上下文；逻辑记录和统计不因物理行增加 |
| F13/W03 | WordExporter.cs | 长文本改为较小续行块，横向6行／纵向12行；保留全部cantSplit，不降低字体字号或收窄页边距 |
| F14/W04 | WriteCampaignRecord | 备注先于照片，不删除备注；照片仍按原比例，横向照片高度上限局部调整以容纳新增上下文 |
| F15/W01 | 记录单Section计数器 | 只对实际输出章节连续中文编号，不以空章节补号 |
| F16/W05 | WriteIssuePhotoTables、历史供数 | 根据已有ActionId及记录顺序区分反馈/复查轮次；缺归属标明未关联，不编造拍摄时间或同机位配对；保留全部旧轮次照片 |
| F17/W07 | word-layout-samples.py、render-word.py、word-fonts.conf、build.yml | 同环境原始基线/新版本真实API样本、PDF、逐页图、哈希、审阅清单；发布依赖浏览器及文档步骤 |
| F18 | 四个指定Vue文件 | 先独立格式化提交，再按任务边界修改；未拆成不必要的组件体系 |
| F19 | BackupStore/DTO、SettingsBackups.vue | 恢复预览增加整治计数，照片只计原图；确认／过期／覆盖前副本不变 |
| 额外修改 | Startup.cs | 默认Warning及以上框架日志；保留启动地址和故障提示 |

### 保持的数据保护

SQL参数、外键、事务、当前schema版本、业务快照、照片唯一归属及阶段、文件路径/JPEG安全检查、pending文件恢复、完整ZIP一致性、覆盖确认、覆盖前副本、当前中断恢复、午夜写入保护和before-*副本均保留。测试使用隔离目录，没有访问生产部署数据。

无损拒绝先把需要校验的库和WAL复制到独立临时目录，避免SQLite只读打开原目录仍生成SHM/WAL。保留完整原文件哈希断言。现行manifest FormatVersion=1不代表旧schema，仍支持。回退必须恢复更新前整目录副本或同时间完整备份；本次没有自动迁移真实旧数据的权限。

### 规范入口

照片关联 `PUT /api/points/{id}/photos`，停用 `PUT /api/points/{id}/status`；整改记录单 `/api/inspections/{id}/export/rectification`；整改台账 `/api/exports/rectification-ledger`。旧 `/api/exports/ledger`、`/export/rectification-record` 已删除。问题详情、编辑、退回及仍有调用的单项API保留；单项内部复用批量核心，不是两套规则。

## DOCX验证方法

保留原12样本，增加合法业务原文、正常无照片备注、两个问题两轮反馈与退回、回填日期、奇数和极横/极竖合成照片、整治短/长备注、零/空数量、空台账、长通知单和无问题块模板。图片有纹理、编号和方向内容，明确是合成测试图，不是用户现场实拍。

`tests/word-layout-samples.py` 逐份校验字段和媒体字节；原长台账测试重组逻辑列并比较全部正文，不能用跨单元格的全文拼接误判新增上下文为内容丢失。`render-word.py` 输出样本/程序/字体/引擎哈希与每页清单；人工审阅不得由conversionPassed推导。保留表头、单元格、页码、全局单倍自动行距和图片比例断言。

本轮人工审阅在同版本LibreOffice和同一Noto CJK替代配置中进行；CI也在单一宿主前后比较。不同引擎的页数不能直接相减。Microsoft Word/WPS图形界面、打印机与真实移动设备结果仍须单列，不能由LibreOffice成功推定。

## 性能观察，不为重构而重构

`tests/measure-api.py` 在复制的原生程序上生成100/1000条记录，每端点重复7次，记录计数、列表、首页和台账响应、进程高水位RSS。`optimization-browser-test.mjs` 测量50问题草稿的20次输入、存储写入次数和立即离开/重开最后输入，并保留真实上传照片ID。

这些是隔离桌面环境观察，不是生产容量或真机速度保证。没有实测SQL执行数、生产并发或大量照片的独立峰值归因时不能宣称解决N+1；本轮未引入缓存/连接池/异步仓储或草稿节流，避免在没有收益证据时增加复杂度和丢字风险。

## 尚待确认／未覆盖

D01整治台账是否全文；D02通知单统一期限是否改逐项；D03是否禁止退回前业务日期补录；D04是否完整继承列表范围。均保持现状。用户说额外两项小改动但只提供Warning日志这一项，未推测第二项。

目标Word/WPS GUI、真实打印、最低浏览器、真机iOS/Android相机、蒲公英实网、完整SQL/照片峰值性能分析没有实际工具或场景证据时明确未验证，不签全部通过。
