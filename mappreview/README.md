# mappreview · 吉林一号切片地图预览器

一个**不依赖后端框架**的本地地图预览器：

- OpenLayers 10.11（`viewer/ol.js` 本地离线文件）
- 卫星影像 + 地名标注分层显示
- Ctrl + 拖拽旋转 / 指北
- 加载深度 0–5 级（带 z18 核心区兜底）
- 单图高清导出（1×–16×）
- 超大图分块流式 PNG 导出（可调分块高度，支持低配/高配机器及旋转地图）
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

## 2. 下载工具

`tools/` 里放了两套下载器，产物不提交到仓库：

- `tools/jl1mall_crawler.py`：官方接口，支持区界枚举、tk/mk 切换、断点续传
- `tools/jl1tool/`：第三方 CDN 通道（本地 wasm 签名），适合大批量补 z18

详细用法、分片方式和占位图清理见 [tools/README.md](tools/README.md)。

---

## 3. 启动

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

## 4. 页面功能

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
- 可选保留当前旋转（旋转分块与北向上共用流式 PNG 管线）

#### 旋转 + 分块说明

勾选“保持当前旋转”时，**单图和分块导出都保留当前角度**，不再自动回退单图模式：

- 每条横带的中心使用当前旋转角及**整张输出图的像素坐标系**换算成地图坐标，避免横带偏移。
- 合成图层时保留 OpenLayers Canvas 的 CSS transform / 透明度，不直接拉伸单个图层 Canvas。
- 等待离屏地图 `rendercomplete`，确认新横带对应的瓦片已渲染；超时会报告失败，不会默默写入旧画面。
- 禁用分块瓦片的渐变过渡；离屏 View 允许超过 z18 的虚拟缩放，超出源瓦片级别时按比例放大，不改变导出范围。
- 是否分块由输出尺寸（80MP / 单边 14000px）或“强制分块”决定，**与旋转角度无关**。

建议分别用 0°、45°、90° 与任意非整角旋转测试不同分块高度的接缝、标注、边界和 z18 兜底。在没有真实瓦片数据的环境下，可运行几何/合成回归测试，但仍应在浏览器中用目标影像做视觉验收。

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

## 5. 数据约定

- 影像：`imagery/{z}/{x}/{y}.webp`（XYZ，左上角原点）
- 标注：`labels/{z}/{x}/{y}.png`（透明，缺失视为透明）
- 边界：`aoi.geojson` / `detail_aoi.geojson`（WGS84）
- 预览器不会读取 `metadata.json` 作为必需文件；缺失也不影响瓦片显示

瓦片数据、截图、`certs/` 都不提交到仓库。

---

## 6. 已知限制

1. **旋转分块**：已支持；建议用实际瓦片检查横带接缝、标注和边界描边，尤其是大倍率及缺片场景。
2. **分块宽度**：当前是整宽横带，宽度上限 32000px；极端宽图会降低倍率。
3. **z18 覆盖**：示例数据只下载了核心区 z18；核心区外是 z17 兜底。
4. **HTTP/2 证书**：自签名，首次访问需要手动继续；没有做自动信任安装。
5. **OpenLayers 补丁**：`initDeepTileSupport()` 直接改写 TileGrid / TileSource 方法，升级 OpenLayers 后需要重新验证。

---

## 7. 快速改动入口

- 图层/UI：`preview.html` 顶部 panel 结构 + `initDeepTileSupport` 之前
- 深度逻辑：搜索 `MAX_TILE_ZOOM`、`MIN_TILE_PIXEL_RATIO`、`renderDepthFor`
- 导出参数：`exportPlan()`、`refreshExportInfo()`
- 旋转横带几何 / Canvas 合成：`exportPixelToCoordinate()`、`drawOffscreenLayers()`
- 单图导出：`runExport()`
- 分块导出：`runChunkedExport()`、`waitForTiles()`
- PNG 编码：`CRC_TABLE`、`pngChunkParts()`
- 服务：`start_preview.py` / `start_preview_h2.py`

## 8. 回归测试

在仓库根目录运行（Node.js 18+，无需安装依赖）：

```bash
node --test mappreview/rotation-export.test.mjs
```

覆盖旋转坐标基底、任意角度及末段短横带的像素连续性、16× 旋转分块模式、Canvas 变换与透明度、渲染完成事件和超时处理。浏览器视觉检查仍是必要的补充。
