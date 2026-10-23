<script setup lang="ts">
import type { ListenUpdateResponse, SystemInfoResponse } from '~/types/api'
import { errorMessage } from '~/utils/format'
const toast = useToast()
const info = ref<SystemInfoResponse | null>(null), open = ref(false), selected = ref<string[]>([])
const loading = ref(false), applying = ref(false), error = ref(''), showOther = ref(false), warningOpen = ref(false)
const newAddresses = ref<string[]>([])
let disposed = false
const localOnly = computed(() => !info.value?.listen.some(value => value !== '127.0.0.1' && value !== '::1'))
const addresses = computed(() => {
  const values = (newAddresses.value.length ? newAddresses.value : info.value?.addresses ?? []).filter(url => !url.includes('localhost') && !url.includes('127.0.0.1') && !url.includes('[::1]'))
  function score(url:string){const ip=new URL(url).hostname;const parts=ip.split('.').map(Number);const privateAddress=parts[0]===10||(parts[0]===192&&parts[1]===168)||(parts[0]===172&&parts[1]!>=16&&parts[1]!<=31);const adapter=info.value?.interfaces.find(x=>x.address===ip);return (privateAddress?0:10)+(/Default Switch|WSL|Docker|VMware|VirtualBox|Clash|Tailscale|TUN|VPN/i.test(adapter?.name??'')?5:0)}
  return [...values].sort((a,b)=>score(a)-score(b))
})
const changed = computed(() => selected.value.slice().sort().join(',') !== info.value?.listen.slice().sort().join(','))
async function load() {
  loading.value = true; error.value = ''
  try { info.value = await $fetch<SystemInfoResponse>('/api/system'); selected.value = [...info.value.listen] }
  catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
}
function toggle(address: string, checked: boolean | 'indeterminate') {
  if (address === '0.0.0.0' && checked === true) selected.value = ['0.0.0.0']
  else if (checked === true) selected.value = [...selected.value.filter(value => value !== '0.0.0.0'), address]
  else selected.value = selected.value.filter(value => value !== address)
}
function confirmApply() {
  if (selected.value.length === 1 && selected.value[0] !== '127.0.0.1' && selected.value[0] !== '0.0.0.0') warningOpen.value = true
  else void apply()
}
async function apply() {
  if (!selected.value.length || applying.value) return
  warningOpen.value = false; applying.value = true; error.value = ''; newAddresses.value = []
  try {
    const response = await $fetch<ListenUpdateResponse>('/api/system/listen', { method: 'PUT', body: { listen: selected.value }, retry: 0 })
    newAddresses.value = response.addresses
    const deadline = Date.now() + 8000
    while (!disposed && Date.now() < deadline) {
      await new Promise(resolve => window.setTimeout(resolve, 400))
      try {
        await $fetch('/api/health', { retry: 0, timeout: 800 })
        const current = await $fetch<SystemInfoResponse>('/api/system', { retry: 0, timeout: 800 })
        if (current.lastError) { info.value = current; error.value = current.lastError; return }
        if (!current.switching && current.listen.slice().sort().join(',') === response.listen.slice().sort().join(',')) {
          info.value = current; selected.value = [...current.listen]; newAddresses.value = []; toast.add({ title: '已应用', color: 'success', duration: 2000 }); return
        }
      } catch { /* Rebinding briefly interrupts the current HTTP listener. */ }
    }
    if (!disposed) error.value = '当前地址不可用，请打开下方地址'
  } catch (value) { error.value = errorMessage(value) }
  finally { applying.value = false }
}
function copy(value: string) {
  const input = document.createElement('textarea')
  input.value = value; input.style.position = 'fixed'; input.style.opacity = '0'; document.body.appendChild(input)
  input.select()
  try { if (!document.execCommand('copy')) throw new Error(); toast.add({ title: '已复制', duration: 2000 }) }
  catch { error.value = '复制失败，请选中地址复制' }
  finally { input.remove() }
}
watch(open, value => { if (value && !applying.value) void load() })
onMounted(load)
onBeforeUnmount(() => { disposed = true })
</script>
<template>
  <UPopover v-model:open="open" :content="{ align: 'end' }"><UButton icon="i-lucide-wifi" :label="applying ? '正在切换…' : '手机连接'" trailing-icon="i-lucide-chevron-down" color="neutral" variant="outline" data-testid="listen-trigger" aria-label="手机连接" />
    <template #content><div class="w-80 max-w-[calc(100vw-24px)] space-y-2 p-3 compact">
      <h2 class="section-title">手机连接</h2>
      <p class="text-sm text-muted">手机与电脑连接同一 Wi-Fi，电脑保持运行。</p>
      <USkeleton v-if="loading && !info" class="h-24 rounded" />
      <template v-if="info?.canConfigure">
        <p class="text-sm">{{localOnly ? '目前仅电脑可用' : '已允许局域网访问'}}</p>
        <UButton v-if="localOnly" label="开启手机访问" :loading="applying" @click="selected=['0.0.0.0'];apply()" />
        <UButton v-else label="关闭手机访问" color="neutral" variant="outline" :loading="applying" @click="selected=['127.0.0.1'];apply()" />
        <details><summary class="cursor-pointer text-sm text-muted py-2">高级网络设置</summary>
        <div class="check-row"><UCheckbox :model-value="selected.includes('127.0.0.1')" :disabled="applying" label="仅本机" class="flex-1" @update:model-value="toggle('127.0.0.1', $event)" /><span class="text-xs text-muted">127.0.0.1</span></div>
        <div class="check-row"><UCheckbox :model-value="selected.includes('0.0.0.0')" :disabled="applying" label="所有网卡" class="flex-1" @update:model-value="toggle('0.0.0.0', $event)" /><span class="text-xs text-muted">0.0.0.0</span></div>
        <h3 class="border-t border-[var(--divider)] pt-2 text-xs text-muted">指定网卡</h3>
        <div v-for="item in info.interfaces.filter(item => !item.other)" :key="item.address" class="check-row"><UCheckbox :model-value="selected.includes(item.address)" :disabled="applying" :label="item.name" class="min-w-0 flex-1" @update:model-value="toggle(item.address, $event)" /><span class="ml-2 text-xs text-muted">{{ item.address }}</span></div>
        <UButton v-if="info.interfaces.some(item => item.other)" label="其他网卡" :trailing-icon="showOther ? 'i-lucide-chevron-up' : 'i-lucide-chevron-down'" color="neutral" variant="link" @click="showOther = !showOther" />
        <template v-if="showOther"><div v-for="item in info.interfaces.filter(item => item.other)" :key="item.address" class="check-row"><UCheckbox :model-value="selected.includes(item.address)" :disabled="applying" :label="item.name" class="min-w-0 flex-1" @update:model-value="toggle(item.address, $event)" /><span class="ml-2 text-xs text-muted">{{ item.address }}</span></div></template>
        <p v-for="address in info.unavailableSaved" :key="address" class="text-xs text-warning">已保存的 {{ address }} 当前不可用</p>
        <p v-if="!selected.length" class="text-xs text-error">至少选择一项</p>
        <div class="flex justify-end"><UButton label="应用" :loading="applying" :disabled="!selected.length || !changed" data-testid="listen-apply" @click="confirmApply" /></div>
        </details>
      </template>
      <p v-if="error" role="alert" class="text-sm text-error">{{ error }}</p>
      <h3 v-if="addresses.length" class="border-t border-[var(--divider)] pt-2 text-sm">手机浏览器打开以下地址</h3>
      <div v-if="addresses[0]" class="flex items-center gap-1"><a :href="addresses[0]" class="min-w-0 flex-1 break-all text-sm text-primary">{{ addresses[0] }}</a><UButton label="复制" color="neutral" variant="ghost" @click="copy(addresses[0])" /></div>
      <details v-if="addresses.length>1"><summary class="text-sm text-muted cursor-pointer py-2">打不开？查看其他地址</summary><div v-for="address in addresses.slice(1)" :key="address" class="flex items-center gap-1"><a :href="address" class="min-w-0 flex-1 break-all text-sm text-primary">{{address}}</a><UButton label="复制" color="neutral" variant="ghost" @click="copy(address)" /></div></details>
    </div></template>
  </UPopover>
  <UModal v-model:open="warningOpen" title="应用监听设置？" description="本机的 localhost 将无法访问"><template #footer><UButton label="取消" color="neutral" variant="outline" @click="warningOpen = false" /><UButton label="应用" @click="apply" /></template></UModal>
</template>
