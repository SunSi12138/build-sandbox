# 照片压缩 spike 验证结果

**结论：jSquash + Vite 模块 Worker 在普通 HTTP 下完成 JPEG 压缩、八种 EXIF 方向处理与 WASM 失败回退，CI 验证通过。**

## 证据与环境

| 项目 | 实际值 |
|---|---|
| 测试日期 | 2026-10-10 |
| 测试提交 | `7c1d6aefd142764a73391f392438294ea7e0345f` |
| GitHub Actions | [run 38050177110 / job 114207546993](https://github.com/SunSi12138/build-sandbox/actions/runs/38050177110/job/114207546993) |
| 操作系统 | Ubuntu 24.04.5，x86_64 |
| Node.js | 24.21.0 |
| 浏览器 | HeadlessChrome 151.0.7922.34 |
| 构建 | TypeScript strict；Vite 8.3.4；生产静态产物 |
| 编码器 | `@jsquash/jpeg` 1.6.0；`@jsquash/resize` 2.1.1 |
| 运行地址 | `http://hbtool-spike.test:5179`，测试域名解析到本机 |
| 安全上下文 | `secureContext=false`；`crossOriginIsolated=false` |
| SharedArrayBuffer | 不可用；正常压缩通过 |

版本、数值和结果取自该 job 日志及其 `test-results.json`；不是根据代码推测。

## 约 1200 万像素样本的压缩结果

样本包含渐变、周期纹理、噪声和四个不同颜色的角标，不是纯色图。JPEG 编码尺寸为 4032×3024，附 EXIF Orientation=6；遵循该方向后的显示尺寸为 3024×4032。

| 检查项 | 正常 Worker / MozJPEG | 阻断 WASM 后 canvas 回退 |
|---|---:|---:|
| 输入字节数 | 7,946,458 | 7,946,458 |
| 输出字节数 | **301,622** | **425,867** |
| 输出 KB（1000 bytes） | **301.6 KB** | **425.9 KB** |
| 输出 KiB（1024 bytes） | 294.6 KiB | 415.9 KiB |
| 体积减少 | **96.20%** | **94.64%** |
| 输出尺寸 | 1200×1600 | 1200×1600 |
| JPEG 魔数 | 正确 | 正确 |
| 输出 EXIF | 无 | 无 |
| 输出四角（左上、右上、左下、右下） | 蓝、红、黄、绿 | 蓝、红、黄、绿 |
| 样本验真 | 通过 | 通过 |

正常路径质量为 75，最长边限制为 1600。回退测试在全新的浏览器上下文中阻断真实 `.wasm` 请求；`forceFallback` 仍为 `false`，因此实际检验了加载失败后的恢复路径。

正常路径整轮测试用时 3844 ms，回退整轮用时 1526 ms。这两个值包含生成测试图、八方向处理及大图处理，**不是单张照片耗时，也不能当作手机性能指标**。

## 八种 EXIF 方向

小样本原始编码尺寸为 800×600；表中按“左上、右上、左下、右下”顺序列出输出角标。程序逐一对比预期角标位置与输出尺寸，同时检查 JPEG 格式和 EXIF 清除。

| EXIF Orientation | 输出尺寸 | 输出角标顺序 | Worker 路径 | canvas 回退 |
|---:|---|---|---|---|
| 1 | 800×600 | 红、绿、蓝、黄 | 通过 | 通过 |
| 2 | 800×600 | 绿、红、黄、蓝 | 通过 | 通过 |
| 3 | 800×600 | 黄、蓝、绿、红 | 通过 | 通过 |
| 4 | 800×600 | 蓝、黄、红、绿 | 通过 | 通过 |
| 5 | 600×800 | 红、蓝、绿、黄 | 通过 | 通过 |
| 6 | 600×800 | 蓝、红、黄、绿 | 通过 | 通过 |
| 7 | 600×800 | 黄、绿、蓝、红 | 通过 | 通过 |
| 8 | 600×800 | 绿、黄、红、蓝 | 通过 | 通过 |

## 并发、请求与图片复核

两条路径的 `observedMax` 均为 2；完成时 `active=0`、`queued=0`。正常路径的页面异常数组和外部请求数组均为空。

实际只请求以下两个同源 WASM 文件：

```text
/assets/squoosh_resize_bg-YY9xfwUg.wasm
/assets/mozjpeg_enc-DO-zoExo.wasm
```

HQX 与 Magic Kernel 虽被依赖包构建到静态目录，但本次处理没有请求它们。没有在线 WASM、图片、字体或 CDN 请求。

已下载并打开 CI 原图、压缩图和页面截图做目视检查：原图与输出图按照 EXIF 显示的四角标记、纹理方向一致；输出为竖图，内容完整，没有误旋转、镜像或裁切。另用 JPEG 元数据读取复核原图为 `(4032, 3024)` 且 Orientation=6，输出为 `(1200, 1600)` 且 EXIF 字典为空。

## CI 产物

[browser-spike-photo-compression artifact 11668992049](https://github.com/SunSi12138/build-sandbox/actions/runs/38050177110/artifacts/11668992049) 包含：

| 文件 | 字节数 |
|---|---:|
| `test-results.json` | 6,487 |
| `test-input-exif6.jpg` | 7,946,458 |
| `test-output-exif6.jpg` | 301,622 |
| `test-screenshot.png` | 429,370 |
| `test-fallback-screenshot.png` | 430,840 |

下载 ZIP 为 8,961,020 bytes，SHA-256 已与 GitHub 记录一致：

```text
dcbba1842d3e9f06015611b6f1270f094d483486dd5d611c9e92922e1044c0ef
```

该次 artifact 按工作流保留 14 天；自动测试可随时重新运行生成同类证据。

## 尚未覆盖的范围

本次证据证明了 Chromium 151 上的实现链路，未声称已经完成 Safari 16.4、最低支持版本浏览器或实际 iPhone / Android 真机验收。没有用户提供的真实手机照片，故不能据此确定所有现场照片的压缩体积或文字细节质量。Word / WPS 是否正常打开导出文档，需要在后续 Word 导出阶段单独验证。
