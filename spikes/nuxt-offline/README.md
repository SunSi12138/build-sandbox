# Nuxt 离线静态 SPA spike

此目录仅验证选型，不连接业务数据库，也不写入排查记录。

## 已完成的本地验证

- 2026-10-10 从 npm 官方发布包确认并固定 Nuxt **4.6.1**、Nuxt UI **4.11.3**、Tailwind CSS **4.3.3**、`@iconify-json/lucide` **1.2.142**。
- 使用 Node **24.19.0** 实际运行 `npm run generate` 成功。构建使用 Nitro **2.13.4**、Vite **8.3.4**、Vue **3.5.43**。
- 产物只有 `.output/public` 下的 `index.html`、`200.html`、`404.html`、本地 `favicon.svg`、`_nuxt/`；没有需要发布的 Node 服务目录。
- Nuxt Icon 构建输出确认打包 **50 个图标，12.73 KB（未压缩）**。Nuxt UI 当前版本通过 `icon:clientBundleIcons` hook 将其默认图标一起加入客户端包。
- 产物 HTML 的 `src` / `href` 没有外部域名，产物中没有 `.woff` / `.woff2` / `.ttf` / `.otf` 字体文件。
- `npm run typecheck` 成功，TypeScript 严格模式开启。当前 `vue-tsc` 无法与 TypeScript 7 的导出结构配合，因此固定 **TypeScript 6.0.3**；无需改变 Nuxt 选型。

本地执行环境中的 Chromium 启动立即收到 `SIGTRAP`，所以**以上构建检查不代表浏览器验收已通过**。浏览器结果由 CI 的 `evidence/offline-report.json` 和截图证明；不要把未运行的断网测试记作通过。

## 复现

```bash
npm ci
npm run typecheck
npm run generate
npx playwright install --with-deps chromium
npm test
```

`npm test` 使用普通 Playwright，并在同一 Node 进程内启动临时 HTTP 静态服务器，不需要后端、HTTPS 或外网服务。

自动验证项目：

1. 首次加载前拦截全部非本机 origin 请求，发现任何外部请求即失败。
2. 检查图标实际具有 SVG CSS mask，包含复选框及下拉默认图标。
3. 操作 Nuxt UI 下拉、复选框、弹窗、中文关闭按钮、Toast。
4. 点击 hash 路由并完整刷新 `/#/settings`，服务器不配置 history fallback。
5. 保存手机及桌面截图；全部网络断开后检查已加载页面的组件、样式、字体栈与图标。
6. 模拟缺少必需浏览器特性，检查 ES5 兼容性提示可见。
7. 浏览器 console error / warning、page error、外部请求均必须为零。

产物：`evidence/mobile.png`、`evidence/desktop.png`、`evidence/offline-report.json`。

## 采用的配置

- Nuxt `ssr: false`、`router.options.hashMode: true`，页面放在 `app/pages/`。
- `ui.fonts: false`、`ui.colorMode: false`；使用系统字体栈、`light` 根类与 `color-scheme: light`。
- `icon.provider: 'none'`、`serverBundle: false`、`fallbackToApi: false`，`clientBundle.scan: true`，动态或重要默认图标显式列入 `icons`。
- 中文 locale 由 `import { zh_cn } from '@nuxt/ui/locale'` 和 `<UApp :locale="zh_cn">` 提供。
- Vite 构建目标为 Chrome / Edge **111**、Safari / iOS **16.4**、Firefox **128**；这是 Tailwind v4 官方最低基线，不声称已逐一实测这些旧版本。
- `index.html` 内嵌 ES5 特性检测，检查 CSS 自定义属性注册、OKLCH、color-mix、container-type 与浏览器基础 API；缺少支持时显示中文升级提示。
- `nitro.devProxy` 仅用于 `nuxt dev`，将 `/api` 和 `/photos` 代理到 5180 端口；生产产物没有 Nitro 接口。

## 官方资料

- [Nuxt v4 自定义路由 / Hash Mode](https://nuxt.com/docs/4.x/guide/recipes/custom-routing)
- [Nuxt 渲染模式](https://nuxt.com/docs/4.x/guide/concepts/rendering)
- [Nuxt UI 安装与 fonts / colorMode 配置](https://ui.nuxt.com/docs/getting-started/installation/nuxt)
- [Nuxt UI 中文与其他 locale](https://ui.nuxt.com/docs/getting-started/integrations/i18n)
- [Nuxt Icon 禁止运行时请求及客户端图标包](https://nuxt.com/modules/icon)
- [Tailwind CSS 浏览器兼容性](https://tailwindcss.com/docs/compatibility)
- [Nuxt npm 发布包](https://www.npmjs.com/package/nuxt)
- [Nuxt UI npm 发布包](https://www.npmjs.com/package/@nuxt/ui)
