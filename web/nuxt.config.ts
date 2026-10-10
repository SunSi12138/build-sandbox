export default defineNuxtConfig({
  compatibilityDate: '2026-10-10',
  ssr: false,
  telemetry: false,
  devtools: { enabled: false },
  modules: ['@nuxt/ui'],
  css: ['~/assets/css/main.css'],
  router: { options: { hashMode: true } },
  typescript: { strict: true },
  ui: { fonts: false, colorMode: false },
  icon: {
    provider: 'none',
    serverBundle: false,
    fallbackToApi: false,
    clientBundle: {
      scan: true,
      icons: [
        'lucide:check', 'lucide:x', 'lucide:chevron-down', 'lucide:chevron-up',
        'lucide:chevron-left', 'lucide:chevron-right', 'lucide:chevrons-up-down',
        'lucide:loader-circle', 'lucide:circle-check', 'lucide:circle-alert',
        'lucide:info', 'lucide:triangle-alert', 'lucide:ellipsis',
        'lucide:house', 'lucide:map-pin', 'lucide:clipboard-plus', 'lucide:list-checks',
        'lucide:settings', 'lucide:clipboard-list', 'lucide:file-down', 'lucide:leaf',
        'lucide:search', 'lucide:plus', 'lucide:arrow-right', 'lucide:arrow-left',
        'lucide:calendar-days', 'lucide:clock-3', 'lucide:circle-check-big',
        'lucide:circle-dashed', 'lucide:refresh-cw', 'lucide:pencil', 'lucide:trash-2',
        'lucide:download', 'lucide:camera', 'lucide:upload', 'lucide:image',
        'lucide:building-2', 'lucide:phone', 'lucide:archive', 'lucide:eye',
        'lucide:filter', 'lucide:sliders-horizontal', 'lucide:arrow-up-right',
        'lucide:arrow-down', 'lucide:arrow-up', 'lucide:chevrons-left', 'lucide:chevrons-right', 'lucide:circle-x', 'lucide:copy', 'lucide:copy-check', 'lucide:eye-off', 'lucide:file', 'lucide:folder', 'lucide:folder-open', 'lucide:grip-vertical', 'lucide:hash', 'lucide:lightbulb', 'lucide:menu', 'lucide:minus', 'lucide:monitor', 'lucide:moon', 'lucide:panel-left-close', 'lucide:panel-left-open', 'lucide:rotate-ccw', 'lucide:square', 'lucide:star', 'lucide:sun'
      ]
    }
  },
  vite: {
    build: { target: ['chrome111', 'edge111', 'safari16.4', 'firefox128'] },
    optimizeDeps: { exclude: ['@jsquash/jpeg', '@jsquash/resize'] },
    worker: { format: 'es' }
  },
  nitro: {
    devProxy: {
      '/api': { target: 'http://127.0.0.1:5180/api', changeOrigin: true },
      '/photos': { target: 'http://127.0.0.1:5180/photos', changeOrigin: true }
    }
  },
  app: {
    head: {
      htmlAttrs: { lang: 'zh-CN', class: 'light' },
      title: '街道生态环保 · 排查与整改',
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1, viewport-fit=cover' }],
      link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }],
      script: [{
        key: 'browser-compatibility',
        innerHTML: "(function(){var c=window.CSS;var ok=!!(window.Promise&&window.fetch&&window.WebAssembly&&window.Worker&&window.createImageBitmap&&c&&c.supports&&c.registerProperty&&c.supports('color','oklch(0.5 0.1 120)')&&c.supports('color','color-mix(in oklab, red, blue)')&&c.supports('container-type','inline-size'));if(!ok){document.addEventListener('DOMContentLoaded',function(){var p=document.createElement('div');p.id='unsupported-browser';p.style.cssText='position:fixed;z-index:2147483647;inset:0;top:0;right:0;bottom:0;left:0;background:#fff;color:#172b25;padding:32px;font-family:Arial,sans-serif;font-size:18px;line-height:1.8';p.innerHTML='<h1>请升级浏览器后使用</h1><p>本工具需要 Chrome / Edge 111 及以上、Safari / iOS 16.4 及以上或 Firefox 128 及以上版本。</p><p>请使用手机或电脑的系统浏览器打开这个网址。</p>';document.body.appendChild(p);});}})();"
      }]
    }
  }
})
