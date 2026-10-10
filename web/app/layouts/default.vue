<script setup lang="ts">
import { todayLocal } from '~/utils/format'

const route = useRoute()
const { bootstrap, load } = useAppData()
function refreshOnReturn() {
  if (document.visibilityState === 'visible') void load().catch(() => {})
}
onMounted(() => {
  void load().catch(() => {})
  document.addEventListener('visibilitychange', refreshOnReturn)
})
onBeforeUnmount(() => document.removeEventListener('visibilitychange', refreshOnReturn))
const mainLinks = [
  { to: '/', label: '工作首页', icon: 'i-lucide-house' },
  { to: '/points', label: '点位管理', icon: 'i-lucide-map-pin' },
  { to: '/inspections/new', label: '新建排查', icon: 'i-lucide-clipboard-plus' },
  { to: '/issues', label: '整改跟踪', icon: 'i-lucide-list-checks' }
]
const archiveLinks = [
  { to: '/inspections', label: '排查记录', icon: 'i-lucide-clipboard-list' },
  { to: '/exports', label: '台账导出', icon: 'i-lucide-file-down' },
  { to: '/settings', label: '工具设置', icon: 'i-lucide-settings' }
]
const mobileLinks = [
  { to: '/', label: '首页', icon: 'i-lucide-house' },
  { to: '/points', label: '点位', icon: 'i-lucide-map-pin' },
  { to: '/inspections/new', label: '新建排查', icon: 'i-lucide-plus' },
  { to: '/issues', label: '整改', icon: 'i-lucide-list-checks' },
  { to: '/settings', label: '设置', icon: 'i-lucide-settings' }
]
function active(path: string) {
  if (path === '/' || path === '/inspections/new') return route.path === path
  if (path === '/inspections') return route.path.startsWith(path) && route.path !== '/inspections/new'
  return route.path.startsWith(path)
}
function skipToContent() {
  const main = document.getElementById('main-content')
  main?.focus()
  main?.scrollIntoView({ block: 'start' })
}
</script>

<template>
  <div class="app-shell">
    <a href="#main-content" class="skip-link" @click.prevent="skipToContent">跳到主要内容</a>
    <aside class="desktop-sidebar">
      <NuxtLink to="/" class="brand" aria-label="街道环保工作首页">
        <span class="brand-mark"><UIcon name="i-lucide-leaf" class="size-6" /></span>
        <span><strong class="block text-base font-semibold tracking-wide">街道生态环保</strong><span class="mt-1 block text-xs text-muted">排查 · 整改 · 留痕</span></span>
      </NuxtLink>
      <nav class="mt-9 flex-1" aria-label="主导航">
        <p class="nav-caption">日常工作</p>
        <div class="space-y-1.5">
          <UButton v-for="item in mainLinks" :key="item.to" :to="item.to" :icon="item.icon" :label="item.label" :variant="active(item.to) ? 'soft' : 'ghost'" :color="active(item.to) ? 'primary' : 'neutral'" class="w-full justify-start px-4" :aria-current="active(item.to) ? 'page' : undefined" />
        </div>
        <p class="nav-caption mt-8">资料与设置</p>
        <div class="space-y-1.5">
          <UButton v-for="item in archiveLinks" :key="item.to" :to="item.to" :icon="item.icon" :label="item.label" :variant="active(item.to) ? 'soft' : 'ghost'" :color="active(item.to) ? 'primary' : 'neutral'" class="w-full justify-start px-4" :aria-current="active(item.to) ? 'page' : undefined" />
        </div>
      </nav>
      <div class="sidebar-note">
        <p class="font-medium text-default">{{ bootstrap?.settings.streetName || '街道环保工作台' }}</p>
        <p class="mt-2 text-xs leading-5 text-muted">资料保存在本机<br>记得定期下载完整备份</p>
        <UButton to="/settings#backup" label="备份与恢复" icon="i-lucide-archive" color="neutral" variant="link" class="mt-1 -ml-2 text-xs" />
      </div>
    </aside>

    <div class="main-shell">
      <div class="workspace-bar">
        <div class="flex items-center gap-2.5 md:hidden"><span class="flex size-8 items-center justify-center rounded-xl bg-primary/10 text-primary"><UIcon name="i-lucide-leaf" class="size-5" /></span><span class="text-sm font-semibold">街道生态环保</span></div>
        <p class="hidden text-xs text-muted md:block">{{ bootstrap?.settings.streetName || '街道生态环保工作台' }}<span class="mx-3 text-dimmed">/</span>工作留痕</p>
        <div class="flex items-center gap-1">
          <span class="mr-3 hidden text-xs text-muted md:block">{{ todayLocal().replaceAll('-', '.') }}</span>
          <UButton to="/inspections" icon="i-lucide-clipboard-list" color="neutral" variant="ghost" aria-label="查看排查记录" class="md:hidden" />
          <UButton to="/exports" icon="i-lucide-file-down" color="neutral" variant="ghost" aria-label="导出台账" class="md:hidden" />
        </div>
      </div>
      <main id="main-content" class="workspace-content" tabindex="-1"><slot /></main>
    </div>

    <nav class="mobile-tabs" aria-label="底部主导航">
      <UButton v-for="item in mobileLinks" :key="item.to" :to="item.to" color="neutral" variant="ghost" class="mobile-tab" :class="{ 'is-active': active(item.to), 'create-tab': item.to === '/inspections/new' }" :aria-current="active(item.to) ? 'page' : undefined">
        <span class="mobile-tab-icon"><UIcon :name="item.icon" class="size-5" /></span>
        <span>{{ item.label }}</span>
      </UButton>
    </nav>
  </div>
</template>
