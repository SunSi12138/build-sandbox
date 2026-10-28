<script setup lang="ts">
import type { TileProvider } from '~/types/api'
import type { FormError } from '@nuxt/ui'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ initial: TileProvider; defaults: TileProvider[]; cacheRoot: string }>()
const emit = defineEmits<{ saved: [provider: TileProvider]; busy: [value: boolean] }>()
const provider = reactive<TileProvider>({ ...props.initial })
const saving = ref(false), error = ref(''), saved = ref(false), showKeys = ref(false)
watch(saving, value => emit('busy', value))
const schemes = [{ label: 'XYZ', value: 'xyz' }, { label: 'TMS', value: 'tms' }]
function validate(state: TileProvider): FormError[] {
  const result: FormError[] = []
  if (!state.name.trim()) result.push({ name: 'name', message: '请填写名称' })
  if (!state.folder.trim()) result.push({ name: 'folder', message: '请填写缓存目录' })
  if (state.forceRefresh && !state.urlTemplate.trim()) result.push({ name: 'urlTemplate', message: '强制更新需要调用链接' })
  if (state.urlTemplate.includes('{key}') && !state.apiKey.trim()) result.push({ name: 'apiKey', message: '请填写服务密钥' })
  if (state.urlTemplate.includes('{mk}') && !state.mapKey.trim()) result.push({ name: 'mapKey', message: '请填写地图密钥' })
  return result
}
function preset(value: TileProvider) {
  provider.urlTemplate = value.urlTemplate; provider.scheme = value.scheme; provider.subdomains = value.subdomains
  if (!provider.name) provider.name = value.name
  saved.value = false
}

async function save() {
  if (saving.value || validate(provider).length) return
  saving.value = true; error.value = ''; saved.value = false
  try {
    const value = await $fetch<TileProvider>(provider.id ? `/api/map/settings/${provider.id}` : '/api/map/settings', { method: provider.id ? 'PUT' : 'POST', body: { ...provider }, retry: 0 })
    Object.assign(provider, value); saved.value = true; emit('saved', value)
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
</script>
<template>
<UForm :state="provider" :validate="validate" class="space-y-3" :data-testid="`map-provider-${provider.id}`" @submit="save" @input="saved = false" @change="saved = false">
        <UAlert v-if="error" role="alert" :description="error" color="error" class="mb-3" />
        <div class="field-grid">
          <UFormField label="名称" name="name" required class="field-full"><UInput v-model="provider.name" :maxlength="80" :disabled="saving" class="w-full" /></UFormField>
          <UFormField label="调用链接" name="urlTemplate" class="field-full"><UTextarea v-model="provider.urlTemplate" :rows="3" :maxlength="4000" :disabled="saving" class="w-full" placeholder="https://example.com/tiles/{z}/{x}/{y}?key={key}" /><div class="mt-1 flex flex-wrap items-center justify-between gap-2"><span class="text-xs text-muted">留空时只读取本地缓存</span><UDropdownMenu :items="[defaults.map(item => ({ label: item.name, onSelect: () => preset(item) }))]"><UButton type="button" label="选择示例链接" variant="link" color="neutral" :disabled="saving" /></UDropdownMenu></div></UFormField>
          <UFormField label="服务密钥（key）" name="apiKey" class="field-full"><UInput v-model="provider.apiKey" :type="showKeys ? 'text' : 'password'" :maxlength="2000" autocomplete="off" :disabled="saving" class="w-full"><template #trailing><UButton type="button" :icon="showKeys ? 'i-lucide-eye-off' : 'i-lucide-eye'" variant="ghost" color="neutral" :aria-label="showKeys ? '隐藏密钥' : '显示密钥'" @click="showKeys = !showKeys" /></template></UInput></UFormField>
          <UFormField v-if="provider.urlTemplate.includes('{mk}') || provider.mapKey" label="地图密钥（mk）" name="mapKey" class="field-full"><UInput v-model="provider.mapKey" :type="showKeys ? 'text' : 'password'" :maxlength="2000" autocomplete="off" :disabled="saving" class="w-full" /></UFormField>
          <UFormField label="缓存目录" name="folder" required class="field-full"><UInput v-model="provider.folder" :maxlength="1000" :disabled="saving" class="w-full" /><p class="mt-1 break-all text-xs text-muted">相对目录基于 {{ cacheRoot }}，也可填写绝对路径。</p></UFormField>
          <UFormField label="图块坐标"><USelect v-model="provider.scheme" :items="schemes" :disabled="saving" class="w-full" /></UFormField>
          <UFormField v-if="provider.urlTemplate.includes('{s}')" label="子域名"><UInput v-model="provider.subdomains" placeholder="0,1,2,3,4,5,6,7" :maxlength="200" :disabled="saving" class="w-full" /></UFormField>
          <div class="field-full"><UCheckbox v-model="provider.forceRefresh" label="强制更新缓存" :disabled="saving" /><p class="mt-1 text-xs text-muted">重新下载加载的图块；失败时使用已有缓存。</p></div>
        </div>
        <div class="mt-4 flex items-center justify-end gap-3"><span v-if="saved" class="text-sm text-primary" role="status">已保存，立即生效</span><UButton type="submit" label="保存" :loading="saving" :disabled="saving" :data-testid="`map-save-${provider.id}`" /></div>
      </UForm>
</template>
