# Spike 3：本地照片压缩

这是独立的最小技术验证，不包含业务系统或上传接口。

## 固定版本与官方依据

2026-10-10 通过 npm registry 查询：`@jsquash/jpeg` 最新版本为 **1.6.0**，`@jsquash/resize` 最新版本为 **2.1.1**。本样例固定这两个版本，并固定 Vite 8.3.4、TypeScript 5.9.3。

- [jSquash 官方仓库及 Vite / Nuxt 已知问题](https://github.com/jamsinclair/jSquash)
- [JPEG 官方包说明](https://www.npmjs.com/package/@jsquash/jpeg)
- [Resize 官方包说明](https://www.npmjs.com/package/@jsquash/resize)
- [Vite 官方 Web Worker 与 WASM 说明](https://vite.dev/guide/features.html#web-workers)
- [MDN：createImageBitmap 默认按照 EXIF 方向解码](https://developer.mozilla.org/en-US/docs/Web/API/Window/createImageBitmap)

已同时阅读所安装 npm 包中的 `encode.js`、`encode.d.ts`、`resize/index.js`、`index.d.ts`、`emscripten-types.d.ts`，核对实际导出与类型。

## 实现

1. `createImageBitmap(file)` 在 Worker 中解码，自动应用 EXIF 方向；不再手动旋转。
2. 绘入 `OffscreenCanvas`，透明背景合成为白色，获得 `ImageData`。
3. 用 jSquash Resize 的 Lanczos3 将长边缩到不超过 1600 px，不放大小图；完整图像采用 `stretch`，尺寸按原始比例计算，不触发裁剪分支。
4. MozJPEG 以质量 75 重新编码。输出统一 JPEG，不复制 EXIF。
5. WASM 通过 `?url` 导入为本地静态资源；JPEG 使用 `init({ locateFile })`，Resize 使用 `initResize(url)`。Vite `optimizeDeps.exclude` 排除两个 jSquash 包。
6. 用 `new Worker(new URL(..., import.meta.url), { type: 'module' })` 启动；队列最多同时处理 2 张。阶段进度是解码、初始化、缩放、编码进度，不伪装为编码器逐像素进度。
7. WASM、Worker 或 Worker 画布失败时，回退到主线程 `createImageBitmap` + canvas + `toBlob('image/jpeg', quality / 100)`，保留相同尺寸限制。Worker 处理完成即终止并释放位图和画布。

没有使用 SharedArrayBuffer、多线程 WASM、Service Worker、随机 UUID、剪贴板或任何在线图片处理资源。

## 运行

```sh
npm ci
npm run dev
```

打开页面可选择本机照片；`accept="image/*" multiple` 没有设置 `capture`。测试按钮可检查合成样本或人为触发回退。

## 标准 CI 验证

```sh
npm ci
npm run build
npx playwright install --with-deps chromium
npm test
```

`browser-test.mjs` 自行启动静态服务器。使用 `http://hbtool-spike.test:5179`，Chromium 将该测试域名解析到本机；这是普通、不可信的 HTTP origin，可检查 `isSecureContext === false`，没有开启任何浏览器安全特性的绕过。

测试内容：

- 8 种 EXIF 方向，包括镜像，检查输出尺寸与四个角的颜色位置。
- 4032×3024 渐变、纹理、噪声与四角标记合成照片，附 Orientation=6；检查缩到 1200×1600、输出 JPEG、EXIF 被移除、输入与输出字节数。
- 在新浏览器上下文阻断真实 `.wasm` 请求，验证 canvas 回退及方向。
- 检查最多并行 2 个任务、WASM 来自同源、没有外部域名请求、没有 SharedArrayBuffer。

生成 `test-results.json`、`test-screenshot.png`、`test-fallback-screenshot.png`、`test-input-exif6.jpg`、`test-output-exif6.jpg`，由 CI 保存为 artifacts。JPEG 体积取决于场景复杂度，不保证所有照片都固定为某个 KB 数值。

## 当前本地结果与限制

- `npm run build` 已通过：TypeScript strict 检查通过，Vite 生产构建通过。
- 构建包含独立模块 Worker（约 29.9 KB）、MozJPEG WASM（251.5 KB）、Resize WASM（34.5 KB）。Resize 包还让 Vite 输出 HQX / Magic Kernel 资源；本流程只调用 Lanczos3，不加载这些附加算法。
- 本环境没有预装 Chromium；标准 Playwright 下载得到截断 ZIP。通过 npm 准备 Chromium 后，其版本查询可运行，但浏览器启动立即 SIGTRAP；strace 也被执行环境禁止。这属于本环境限制，**本地浏览器压缩、方向和回退尚未验收通过**，以上测试由标准 GitHub Actions runner 执行后再更新结论。
- 没有用户提供的实际手机照片。合成图用于可重复的方向、尺寸和编码验证；真实 iPhone / Android 相册照片以及 Safari / WPS 最终验收仍需在相应设备上完成。
