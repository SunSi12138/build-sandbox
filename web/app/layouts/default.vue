<script setup lang="ts">
const route = useRoute()
const { bootstrap, load } = useAppData()
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
const formPage = computed(() => Boolean(route.meta.formPage) || overlayForm.value)
const links = [
  { to: '/', label: '工作首页', short: '首页', icon: 'i-lucide-house' },
  { to: '/points', label: '点位管理', short: '点位', icon: 'i-lucide-map-pin' },
  { to: '/inspections', label: '排查记录', short: '排查', icon: 'i-lucide-clipboard-list' },
  { to: '/campaigns', label: '整治记录', short: '整治', icon: 'i-lucide-brush-cleaning' },
  { to: '/activities', label: '活动记录', short: '活动', icon: 'i-lucide-calendar-days' },
  { to: '/settings', label: '工具设置', short: '设置', icon: 'i-lucide-settings' }
]
function active(path: string) { return path === '/' ? route.path === '/' : route.path.startsWith(path) }
function refreshOnReturn() { if (document.visibilityState === 'visible') void load().catch(() => {}) }
function skipToContent() { document.getElementById('main-content')?.focus() }
onMounted(() => { void load().catch(() => {}); document.addEventListener('visibilitychange', refreshOnReturn) })
onBeforeUnmount(() => document.removeEventListener('visibilitychange', refreshOnReturn))
</script>

<template>
  <div class="app-shell" :class="{ 'form-page': formPage }">
    <a href="#main-content" class="skip-link" @click.prevent="skipToContent">跳到主要内容</a>
    <aside class="desktop-sidebar">
      <NuxtLink to="/" class="brand" aria-label="街道生态环保首页"><UIcon name="i-lucide-leaf" /><strong>街道生态环保</strong></NuxtLink>
      <nav class="sidebar-nav" aria-label="主导航">
        <UTooltip v-for="item in links" :key="item.to" :text="item.label" :content="{ side: 'right' }">
          <NuxtLink :to="item.to" class="sidebar-nav-link" :class="{ 'is-active': active(item.to) }" :aria-label="item.label" :aria-current="active(item.to) ? 'page' : undefined"><UIcon :name="item.icon" /><span class="sidebar-nav-label">{{ item.label }}</span></NuxtLink>
        </UTooltip>
      </nav>
      <p class="sidebar-street">{{ bootstrap?.settings.streetName }}</p>
    </aside>
    <div class="main-shell"><main id="main-content" class="workspace-content" tabindex="-1"><slot /></main></div>
    <nav v-if="!formPage" class="mobile-tabs" aria-label="底部主导航">
      <NuxtLink v-for="item in links" :key="item.to" :to="item.to" class="mobile-tab" :class="{ 'is-active': active(item.to) }" :aria-current="active(item.to) ? 'page' : undefined"><UIcon :name="item.icon" /><span>{{ item.short }}</span></NuxtLink>
    </nav>
  </div>
</template>
