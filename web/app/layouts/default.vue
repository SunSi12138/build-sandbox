<script setup lang="ts">
const route = useRoute()
const { bootstrap, load } = useAppData()
const sidebarCollapsed = useState<boolean>('community-sidebar-collapsed', () => false)
watch(sidebarCollapsed, value => { if (import.meta.client) localStorage.setItem('community-sidebar-collapsed', String(value)) })
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
const formPage = computed(() => Boolean(route.meta.formPage) || overlayForm.value)
const links = [
  { to: '/', label: '工作首页', short: '首页', icon: 'i-lucide-house' },
  { to: '/map', label: '社区地图', short: '地图', icon: 'i-lucide-map' },
  { to: '/points', label: '场所档案', short: '场所', icon: 'i-lucide-map-pin' },
  { to: '/inspections', label: '检查记录', short: '检查', icon: 'i-lucide-clipboard-list' },
  { to: '/campaigns', label: '整治记录', short: '整治', icon: 'i-lucide-hard-hat' },
  { to: '/reports', label: '统计导出', short: '导出', icon: 'i-lucide-file-down' },
  { to: '/settings', label: '工具设置', short: '设置', icon: 'i-lucide-settings' }
]
function active(path: string) {
  const groups:Record<string,string[]>= {'/settings':['/catalogs','/layers','/imports','/management','/archive'], '/inspections':['/plans','/drafts','/reminders'], '/reports':['/statistics']}
  return path==='/'?route.path==='/':route.path.startsWith(path)||(groups[path]??[]).some(prefix=>route.path.startsWith(prefix))
}
function refreshOnReturn() { if (document.visibilityState === 'visible') void load().catch(() => {}) }
function skipToContent() { document.getElementById('main-content')?.focus() }
onMounted(() => { sidebarCollapsed.value = localStorage.getItem('community-sidebar-collapsed') === 'true'; void load().catch(() => {}); document.addEventListener('visibilitychange', refreshOnReturn) })
onBeforeUnmount(() => document.removeEventListener('visibilitychange', refreshOnReturn))
</script>

<template>
  <div class="app-shell" :class="{ 'form-page': formPage, 'map-page': route.path === '/map', 'sidebar-collapsed': sidebarCollapsed }">
    <a href="#main-content" class="skip-link" @click.prevent="skipToContent">跳到主要内容</a>
    <aside class="desktop-sidebar">
      <NuxtLink to="/" class="brand" aria-label="社区管理首页"><UIcon name="i-lucide-leaf" /><strong>社区管理</strong></NuxtLink>
      <nav class="sidebar-nav" aria-label="主导航">
        <UTooltip v-for="item in links" :key="item.to" :text="item.label" :content="{ side: 'right' }">
          <NuxtLink :to="item.to" class="sidebar-nav-link" :class="{ 'is-active': active(item.to) }" :aria-label="item.label" :aria-current="active(item.to) ? 'page' : undefined"><UIcon :name="item.icon" /><span class="sidebar-nav-label">{{ item.label }}</span></NuxtLink>
        </UTooltip>
      </nav>
      <button class="sidebar-toggle" type="button" :aria-label="sidebarCollapsed ? '展开侧边栏' : '收起侧边栏'" :aria-expanded="!sidebarCollapsed" @click="sidebarCollapsed = !sidebarCollapsed"><UIcon :name="sidebarCollapsed ? 'i-lucide-panel-left-open' : 'i-lucide-panel-left-close'" /><span class="sidebar-nav-label">{{ sidebarCollapsed ? '展开' : '收起' }}</span></button>
      <p class="sidebar-street">{{ bootstrap?.settings.streetName }}</p>
    </aside>
    <div class="main-shell"><main id="main-content" class="workspace-content" tabindex="-1"><slot /></main></div>
    <nav v-if="!formPage" class="mobile-tabs" aria-label="底部主导航">
      <NuxtLink v-for="item in links" :key="item.to" :to="item.to" class="mobile-tab" :class="{ 'is-active': active(item.to) }" :aria-current="active(item.to) ? 'page' : undefined"><UIcon :name="item.icon" /><span>{{ item.short }}</span></NuxtLink>
    </nav>
  </div>
</template>
