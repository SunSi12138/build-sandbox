<script setup lang="ts">
import { errorMessage } from '~/utils/format'

type Mode = 'off' | 'read' | 'write'
type TokenAction = 'keep' | 'generate' | 'clear'
interface McpSettings { mode: Mode; hasToken: boolean; canConfigure: boolean; available: boolean; url: string }
interface McpSaved { settings: McpSettings; token: string | null }
const toast = useToast()
const current = ref<McpSettings | null>(null)
const mode = ref<Mode>('off'), tokenAction = ref<TokenAction>('keep'), generatedToken = ref('')
const loading = ref(true), saving = ref(false), error = ref('')
const modes = [
  { label: '关闭', value: 'off' },
  { label: '只读（查询、导出）', value: 'read' },
  { label: '读写（查询、新增、修改、删除）', value: 'write' },
]
const disabled = computed(() => loading.value || saving.value || !current.value?.canConfigure)
const changed = computed(() => mode.value !== current.value?.mode || tokenAction.value !== 'keep')
async function load() {
  loading.value = true; error.value = ''
  try {
    const result = await $fetch<McpSettings>('/api/system/mcp')
    current.value = result; mode.value = result.mode; tokenAction.value = 'keep'; generatedToken.value = ''
  } catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
}
async function save() {
  if (disabled.value || !changed.value) return
  saving.value = true; error.value = ''
  try {
    const result = await $fetch<McpSaved>('/api/system/mcp', { method: 'PUT', body: { mode: mode.value, tokenAction: tokenAction.value }, retry: 0 })
    current.value = result.settings; mode.value = result.settings.mode; tokenAction.value = 'keep'
    if (result.token) generatedToken.value = result.token
    else if (!result.settings.hasToken) generatedToken.value = ''
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
async function copy(value: string) {
  try {
    if (navigator.clipboard && window.isSecureContext) await navigator.clipboard.writeText(value)
    else {
      const input = document.createElement('textarea')
      input.value = value; input.style.position = 'fixed'; input.style.opacity = '0'; document.body.appendChild(input)
      try { input.select(); if (!document.execCommand('copy')) throw new Error() }
      finally { input.remove() }
    }
    toast.add({ title: '已复制', duration: 2000 })
  } catch { error.value = '复制失败，请手动复制。' }
}
onMounted(load)
</script>

<template>
  <PageState :loading="loading" :error="!current ? error : ''" @retry="load">
    <template v-if="current">
      <section class="panel" data-testid="mcp-settings">
        <div class="panel-head"><h2 class="section-title">MCP 设置</h2><StatusBadge :tone="current.mode === 'off' ? 'neutral' : 'success'">{{ current.mode === 'off' ? '已关闭' : current.mode === 'read' ? '只读' : '读写' }}</StatusBadge></div>
        <div class="panel-body space-y-4">
          <p v-if="!current.canConfigure" class="text-sm text-muted">请在运行程序的电脑上修改 MCP 设置。</p>
          <UFormField label="访问权限">
            <USelect v-model="mode" :items="modes" :disabled="disabled" class="w-full sm:max-w-md" aria-label="MCP 访问权限" data-testid="mcp-mode" />
          </UFormField>
          <UFormField label="连接地址">
            <div class="flex items-center gap-2"><UInput :model-value="current.url" readonly class="min-w-0 flex-1" /><UButton label="复制" color="neutral" variant="outline" @click="copy(current.url)" /></div>
            <p v-if="!current.available" class="mt-1 text-xs text-warning">请在首页访问设置中启用本机监听。</p>
            <p v-else class="mt-1 text-xs text-muted">仅本机 Streamable HTTP 连接</p>
          </UFormField>
          <UFormField label="访问令牌">
            <div class="flex flex-wrap items-center gap-2">
              <span class="text-sm text-muted">{{ tokenAction === 'generate' ? '保存后生成新令牌' : tokenAction === 'clear' ? '保存后清除令牌' : current.hasToken ? '已设置' : '未设置' }}</span>
              <UButton :label="current.hasToken ? '重新生成' : '生成令牌'" color="neutral" variant="outline" :disabled="disabled" data-testid="mcp-generate" @click="tokenAction = 'generate'" />
              <UButton v-if="current.hasToken || tokenAction === 'generate'" label="清除令牌" color="neutral" variant="link" :disabled="disabled" data-testid="mcp-clear" @click="tokenAction = current.hasToken ? 'clear' : 'keep'" />
              <UButton v-if="tokenAction !== 'keep'" label="取消" color="neutral" variant="link" :disabled="disabled" @click="tokenAction = 'keep'" />
            </div>
            <div v-if="generatedToken" class="mt-2 space-y-2">
              <p class="text-xs text-warning">新令牌仅显示此次，请复制保存。重新生成会使旧令牌失效。</p>
              <div class="flex gap-2"><UInput :model-value="generatedToken" readonly class="min-w-0 flex-1" data-testid="mcp-generated-token" /><UButton label="复制" color="neutral" variant="outline" @click="copy(generatedToken)" /></div>
            </div>
          </UFormField>
          <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        </div>
        <div class="flex justify-end border-t border-[var(--divider)] p-3"><UButton label="保存" :loading="saving" :disabled="disabled || !changed" data-testid="mcp-save" @click="save" /></div>
      </section>
    </template>
  </PageState>
</template>
