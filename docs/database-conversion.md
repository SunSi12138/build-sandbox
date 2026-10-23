# 旧数据库一次性转换

新版数据库版本统一为 **1**。主程序只接受新版完整结构，不在启动时迁移旧库；旧版结构 1 与新版结构 1 不可直接互换。独立工具支持旧版结构 1、2、3，仅需 Python 3.8 或以上，无第三方依赖。

## 操作步骤

1. 关闭旧程序，保留整个旧目录的副本（包括数据库、照片及 WAL 文件）。转换期间不要重新启动旧程序。
2. 从新版发布包 `tools` 目录运行转换工具。输出路径必须不存在，而且不能位于旧目录内部。

Windows：

```powershell
py -3 tools/convert_database.py --source "D:\hbtool-old" --output "D:\hbtool-new"
```

Linux / macOS：

```sh
python3 tools/convert_database.py --source /path/hbtool-old --output /path/hbtool-new
```

在源码目录中运行时，将 `tools/convert_database.py` 改为 `scripts/convert_database.py`。`--source` 也可直接指向旧目录的 `data/app.db`。

3. 工具会创建 `data`、`photos`、`backup`、`temp`，并复制原有 `appsettings.json`。转换前快照保存在 `backup/before-convert.db`；它仍是旧版结构，不能作为新版备份直接恢复。
4. 把新版可执行文件及完整 `wwwroot` 放入输出目录，启动新版程序。核对点位名称、地址、照片、排查及整治记录，再下载一份新版完整备份。

## 转换规则

- 旧目录不修改；先在临时目录复制数据库及日志，再通过 SQLite 快照转换。失败时删除本次临时输出，不覆盖任何已有输出目录。
- 保留原有点位、排查、问题、反馈／复查／退回记录、照片和整治记录。旧版照片位置 `data/photos` 与 `photos` 均可识别。
- 原有问题统一归入“其他”，可在后续记录中选择新分类；已有记录不凭描述自动猜分类。
- 没有绑定点位的旧版整治记录会建立对应临时点位，保留原整治地点和正文。
- 新增问题类型与活动类型字典；活动列表初始为空。
- 缺失已登记照片、损坏数据库、外键错误、不支持的版本及重复转换都会拒绝。不要通过手工修改版本号绕过检查。
- 如需回退，使用原目录和原版程序；不要将转换后的库交给旧程序打开。
