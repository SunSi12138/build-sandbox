# 下载工具

本目录只放代码，不提交瓦片产物。

```text
tools/
├─ jl1mall_crawler.py          # 官方接口：按区界枚举瓦片，支持 z10-z18、tk/mk 切换、断点续传
├─ prepare_z18_missing.py      # 枚举 z18 缺失瓦片，按市中心排序，可切 4 份分片
├─ clean_tiles.py              # 删除 <800B 空白图和已知占位图
└─ jl1tool/
   ├─ jl1_tile.py              # 第三方 CDN 通道：jlcdn.thread0.com + 本地 wasm 签名
   ├─ signer.js
   ├─ security.wasm
   ├─ wasm_exec_tiny.js
   ├─ tiles.txt
   └─ README.txt
```

## 产物路径

两份工具都只写：

```text
<out>/imagery/{z}/{x}/{y}.webp
<out>/labels/{z}/{x}/{y}.png
```

`mappreview/imagery/`、`mappreview/labels/`、`mappreview/certs/` 已在根 `.gitignore` 排除，**不会提交**。

---

## 1. 官方接口 `jl1mall_crawler.py`

需要：

```bash
pip install requests shapely
set JL1_TOKEN=浏览器 localStorage 里的 token
```

生成区界瓦片并下载：

```bash
python jl1mall_crawler.py download ^
  --aoi aoi.geojson ^
  --zooms 10-18 ^
  --layers imagery,labels ^
  --out 桑珠孜区_2025 ^
  --workers 16
```

只补 z18：

```bash
python jl1mall_crawler.py download ^
  --aoi aoi.geojson ^
  --zooms 18 ^
  --layers imagery ^
  --out 桑珠孜区_2025 ^
  --workers 16
```

### 换 tk/mk 续传

账号额度用完、或者要换另一份流量包时：

```bash
python jl1mall_crawler.py download ^
  --aoi aoi.geojson ^
  --zooms 18 ^
  --layers imagery ^
  --out 桑珠孜区_2025 ^
  --workers 16 ^
  --tile-tk 新的TK ^
  --tile-sk 新的SK ^
  --tile-mk 87ef1846fbacfb91a8f45eff68115cd7 ^
  --ignore-account-quota ^
  --overwrite
```

- 已存在的有效文件默认跳过
- `--overwrite` 用于覆盖旧占位图
- `--tile-sk` 只在 tk 对应不同 sk 时需要
- **不要把真实 token 写进仓库**

### 重要：占位图识别

接口在额度耗尽时可能仍返回 HTTP 200 + WebP，但内容是：

- `参数有误，请检查服务地址`
- `当前流量已达使用上限`
- `当前图源已过期`

这类图通常只有 **430B / 2,194B / 10,400B / 12,590B**，并且大量瓦片哈希完全相同。下载后必须抽查像素，不能只看 HTTP 200。

---

## 2. CDN 通道 `jl1tool/jl1_tile.py`

```bash
python jl1_tile.py 18 195792 108765 -o D:\tiles
python jl1_tile.py --list tiles.txt --z 18 -o D:\tiles --workers 16 --skip-existing
```

特点：

- 使用 `jlcdn.thread0.com` + 本地 wasm 签名
- 不消耗 jl1mall 账号额度
- 输出 XYZ `<out>/18/{x}/{y}.webp`
- 单进程 16 线程实测约 24 张/秒

### 大批量建议分片

先生成缺失列表并切成 4 份：

```bash
python prepare_z18_missing.py ^
  --aoi aoi.geojson ^
  --out 桑珠孜区_2025 ^
  --list missing_z18.txt ^
  --shards 4
```

然后 4 个进程并行：

```bash
python jl1_tile.py --list missing_z18_shard1.txt --z 18 -o out\imagery --workers 16 --skip-existing
python jl1_tile.py --list missing_z18_shard2.txt --z 18 -o out\imagery --workers 16 --skip-existing
python jl1_tile.py --list missing_z18_shard3.txt --z 18 -o out\imagery --workers 16 --skip-existing
python jl1_tile.py --list missing_z18_shard4.txt --z 18 -o out\imagery --workers 16 --skip-existing
```

4 个进程并行时整体约 **90–120 张/秒**，但会受 CDN 限速影响。

### 跑完必须清理空图

`jl1_tile.py` 会把 430B 的空白瓦片也保存下来，也可能遇到接口占位图。先预览，再执行：

```bash
python clean_tiles.py --root 桑珠孜区_2025\imagery\18 --dry-run
python clean_tiles.py --root 桑珠孜区_2025\imagery\18
```

它会删除：

- 小于 800 字节的空白图
- 已知哈希的“参数有误/流量上限/图源过期”占位图

删完再抽查大图，确认没有异常。

---

## 3. 2026-11-18 z18 补全记录

- 官方接口 + 公共 getMap key：新增 39,201 张后 key 达到上限，返回 12,590B 占位图；已删除 86,414 张占位图。
- 改用 `jl1tool` CDN 通道，4 分片并行：
  - 新增有效 z18 约 86,298 张
  - 删除 430B 空白图 203 张
  - 最终 z18 有效 **209,265 / 209,489**
  - 剩余 224 个网格源无有效影像
- 数据产物不提交到 GitHub。

> `jl1tool` 是第三方通道，签名或限速随时可能变化；仅用于个人数据备份，注意来源与使用条款。
