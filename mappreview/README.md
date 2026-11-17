# mappreview · 吉林一号切片地图预览器

一个**不依赖后端框架**的本地地图预览器：

- OpenLayers 10.11（`viewer/ol.js` 本地离线文件）
- 卫星影像 + 地名标注分层显示
- Ctrl + 拖拽旋转 / 指北
- 加载深度 0–5 级（带 z18 核心区兜底）
- 单图高清导出（1×–16×）
- 超大图分块流式 PNG 导出（可调分块高度，支持低配/高配机器）
- 可选 HTTP/1.1（8765）或 HTTP/2 + TLS（8443）本地服务

这个目录是 `build-sandbox` 仓库的 `mappreview` 分支内容，方便其他 AI/开发者继续修改。

---

## 1. 目录结构

```text
mappreview/
├─ preview.html            # 全部前端逻辑（OpenLayers、图层、深度、导出）
├─ start_preview.py        # HTTP/1.1 静态服务，默认 127.0.0.1:8765
├─ start_preview_h2.py     # HTTP/2 + TLS 静态服务，默认 127.0.0.1:8443
├─ start-preview.bat       # Windows 双击启动 HTTP/1.1
├─ start-preview-h2.bat    # Windows 双击启动 HTTP/2
├─ requirements-h2.txt     # hypercorn / cryptography
├─ viewer/
│  ├─ ol.js                # OpenLayers 10.11.0 本地构建
│  └─ ol.css
├─ aoi.geojson             # 示例：桑珠孜区边界
└─ detail_aoi.geojson      # 示例：z18 核心区边界
```

预览器不包含瓦片数据。运行前需要把下载好的数据放到同级目录：

```text
mappreview/
├─ imagery/
│  └─ {z}/{x}/{y}.webp     # 卫星影像 XYZ
├─ labels/
│  └─ {z}/{x}/{y}.png      # 透明地名标注（缺失=透明）
├─ preview.html
└─ ...
```

`aoi.geojson` / `detail_aoi.geojson` 只是示例边界；换成其他区域时直接覆盖即可。它们只影响区界显示和“核心区”按钮，不影响瓦片图层。

---

## 2. 启动

### HTTP/1.1（最简单）

```bash
cd mappreview
python start_preview.py
# 浏览器打开 http://127.0.0.1:8765/preview.html
```

`start_preview.py` 只用 Python 标准库，**没有第三方依赖**。  
已启用 HTTP/1.1 Keep-Alive 和瓦片缓存，适合本地预览。

### HTTP/2（可选）

```bash
pip install -r requirements-h2.txt
python start_preview_h2.py
# 浏览器打开 https://127.0.0.1:8443/preview.html
```

首次运行会生成：

```text
certs/localhost.crt
certs/localhost.key
```

这是自签名证书，浏览器会提示不安全，选择“高级 → 继续访问”。  
页面标题下会显示实际协议：`h2` 或 `http/1.1`。

> `certs/` 不应该提交到仓库；`.gitignore` 已排除。

---

## 3. 页面功能

### 图层

- 卫星影像
- 地名标注（独立开关 + 透明度）
- 区界
- z18 核心区虚线框

### 加载深度 0–5

通过运行时补丁让瓦片源向更深 z 请求：

```js
grid.getZForResolution = function(resolution, direction) {
  const d = renderDepthFor(map.getView().getZoom());
  if (d > 0) return original(resolution / Math.pow(2, d), direction);
  return original(resolution, direction);
};

source.getTilePixelRatio = function(pixelRatio) {
  const d = renderDepthFor(...);
  return d ? pixelRatio / Math.pow(2, d) : original(pixelRatio);
};
```

关键函数：

- `maxRenderDepth()` / `renderDepthFor(z)`
- `initDeepTileSupport()`
- `refreshDeepSources()`
- `schedulePrefetch()` / `prefetchExtraTiles()`

`MIN_TILE_PIXEL_RATIO = 0.25`：低于 0.25 时 OpenLayers 的瓦片画布会小到只画出中心一块，所以实际渲染深度会被截断；超出的级别只做后台预取。

### 旋转

- `Ctrl + 拖拽` / macOS `Command + 拖拽`
- `ol.interaction.DragRotate` 的 condition 只响应 Ctrl/Meta
- “指北”按钮把旋转归零

### 离屏导出

点击“离屏导出高清图”：

- 输出倍率：1×–16×，0.5× 步进
- 加载深度：0–5
- 分块高度：128 / 256 / 512 / 1024 / 2048 px
- 强制分块开关
- 包含地名标注 / 区界
- 保持当前旋转
- 背景：透明 / 黑色（默认）/ 白色
- PNG / JPEG（分块模式强制 PNG）

#### 单图模式

- 新建隐藏的 `ol.Map`，无 controls / interactions / UI
- `pixelRatio: 1`
- 图片尺寸 = 当前视口 × 输出倍率
- 用 2×/4×/8× 输出时直接渲染到更大的 canvas
- 单图限制约 **80MP / 单边 14000px**
- 超过自动切分块

#### 分块流式 PNG

入口函数：`runChunkedExport(p)`

1. 用 `CompressionStream('deflate')` 建立连续 zlib 流
2. 按 `p.bandHeight` 逐条渲染横带
3. 每带等待 `tileloadstart/tileloadend/tileloaderror` 全部结束
4. 每带 `getImageData` → PNG filter 0 → 写入压缩流
5. 压缩流每块包装成 IDAT，最后拼 IHDR / IEND
6. 下载 `.png`

相关函数：

- `waitForTiles()`
- `pngChunkParts()` / `crc32Update()` / `CRC_TABLE`
- `runChunkedExport()`

分块限制：

- 单边 **32000px**
- 总量 **800MP**
- 仅 PNG
- 固定北向上（旋转分块目前不可靠，见下）

#### 旋转 + 分块说明

分块模式下按旋转视图切横带会出现错位/剪切，因此当前策略是：

- `保持当前旋转` 勾选 → 自动退回**单图模式**，受 80MP 限制
- 不勾选 → 走分块模式，固定北向上

弹窗会明确显示：

```text
旋转导出使用单图模式（分块只支持北向上）；倍率受 80MP 限制。
```

如果后续要支持“16× + 旋转”，需要单独实现旋转分块拼接（目前未完成）。

#### z18 核心区兜底

当输出层级 ≥ z18 时，先铺一层 `z17` 底图，再叠 `z18` 高清层：

- z18 核心区：高清
- 核心区外：z17 兜底
- 不会出现锯齿状透明/黑块

#### 背景

- `transparent`：PNG 透明
- `black`：黑色填充（默认）
- `white`：白色填充

区界外没有瓦片时，背景色决定最终显示。

---

## 4. 数据约定

- 影像：`imagery/{z}/{x}/{y}.webp`（XYZ，左上角原点）
- 标注：`labels/{z}/{x}/{y}.png`（透明，缺失视为透明）
- 边界：`aoi.geojson` / `detail_aoi.geojson`（WGS84）
- 预览器不会读取 `metadata.json` 作为必需文件；缺失也不影响瓦片显示

瓦片数据、截图、`certs/` 都不提交到仓库。

---

## 5. 已知限制

1. **分块 + 旋转**：未实现，勾选旋转会走单图；大倍率会被 80MP 限制。
2. **分块宽度**：当前是整宽横带，宽度上限 32000px；极端宽图会降低倍率。
3. **z18 覆盖**：示例数据只下载了核心区 z18；核心区外是 z17 兜底。
4. **HTTP/2 证书**：自签名，首次访问需要手动继续；没有做自动信任安装。
5. **OpenLayers 补丁**：`initDeepTileSupport()` 直接改写 TileGrid / TileSource 方法，升级 OpenLayers 后需要重新验证。

---

## 6. 快速改动入口

- 图层/UI：`preview.html` 顶部 panel 结构 + `initDeepTileSupport` 之前
- 深度逻辑：搜索 `MAX_TILE_ZOOM`、`MIN_TILE_PIXEL_RATIO`、`renderDepthFor`
- 导出参数：`exportPlan()`、`refreshExportInfo()`
- 单图导出：`runExport()`
- 分块导出：`runChunkedExport()`、`waitForTiles()`
- PNG 编码：`CRC_TABLE`、`pngChunkParts()`
- 服务：`start_preview.py` / `start_preview_h2.py`
