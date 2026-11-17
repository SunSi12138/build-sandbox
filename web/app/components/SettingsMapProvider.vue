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
const preloadDepths = [
  { label: '0 · 原始层级', value: 0 },
  { label: '1 · 高清 +1 级', value: 1 },
  { label: '2 · 高清 +2 级', value: 2 },
  { label: '3 · 高清 +3 级', value: 3 },
]
const tileFormats = [{ label: '图片（PNG / JPG / WebP）', value: 'raster' }, { label: 'MVT 矢量地名（PBF）', value: 'mvt' }, { label: 'PMTiles 单文件（MVT / 图片）', value: 'pmtiles' }]
const tileFormat = computed<'raster' | 'mvt' | 'pmtiles'>({
  get: () => provider.format,
  set: value => {
    provider.format = value
    if (value === 'pmtiles') {
      provider.scheme = 'xyz'
      provider.forceRefresh = false
      if (!provider.folder.toLowerCase().endsWith('.pmtiles')) provider.folder += '.pmtiles'
    }
  },
})
function validate(state: TileProvider): FormError[] {
  const result: FormError[] = []
  if (!state.name.trim()) result.push({ name: 'name', message: '请填写名称' })
  if (!Number.isInteger(state.preload) || state.preload < 0 || state.preload > 3) result.push({ name: 'preload', message: '加载深度必须为 0–3' })
  if (!state.folder.trim()) result.push({ name: 'folder', message: '请填写缓存目录或 PMTiles 文件路径' })
  if (state.format === 'pmtiles' && !state.folder.toLowerCase().endsWith('.pmtiles')) result.push({ name: 'folder', message: 'PMTiles 文件路径必须以 .pmtiles 结尾' })
  if (state.styleDirectory && state.styleDirectory.length > 1000) result.push({ name: 'styleDirectory', message: '样式目录路径不能超过 1000 个字符' })
  if (state.forceRefresh && !state.urlTemplate.trim()) result.push({ name: 'urlTemplate', message: '强制更新需要调用链接' })
  if (state.urlTemplate.includes('{key}') && !state.apiKey.trim()) result.push({ name: 'apiKey', message: '请填写服务密钥' })
  if (state.urlTemplate.includes('{mk}') && !state.mapKey.trim()) result.push({ name: 'mapKey', message: '请填写地图密钥' })
  return result
}
function preset(value: TileProvider) {
  provider.urlTemplate = value.urlTemplate; provider.scheme = value.scheme; provider.subdomains = value.subdomains; provider.format = value.format
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
          <UFormField :label="tileFormat === 'pmtiles' ? '在线 PMTiles 归档链接（可选）' : '调用链接'" name="urlTemplate" class="field-full"><UTextarea v-model="provider.urlTemplate" :rows="3" :maxlength="4000" :disabled="saving" class="w-full" :placeholder="tileFormat === 'pmtiles' ? 'https://example.com/labels.pmtiles' : 'https://example.com/tiles/{z}/{x}/{y}?key={key}'" /><div class="mt-1 flex flex-wrap items-center justify-between gap-2"><span class="text-xs text-muted">{{ tileFormat === 'pmtiles' ? '本地文件优先；在线归档必须支持 HTTP Range 请求，不会自动下载整包。' : '留空时只读取本地缓存；MVT 使用 z/x/y.pbf 或 z/x/y.mvt' }}</span><UDropdownMenu :items="[defaults.map(item => ({ label: item.name, onSelect: () => preset(item) }))]"><UButton type="button" label="选择示例链接" variant="link" color="neutral" :disabled="saving" /></UDropdownMenu></div></UFormField>
          <UFormField label="服务密钥（key）" name="apiKey" class="field-full"><UInput v-model="provider.apiKey" :type="showKeys ? 'text' : 'password'" :maxlength="2000" autocomplete="off" :disabled="saving" class="w-full"><template #trailing><UButton type="button" :icon="showKeys ? 'i-lucide-eye-off' : 'i-lucide-eye'" variant="ghost" color="neutral" :aria-label="showKeys ? '隐藏密钥' : '显示密钥'" @click="showKeys = !showKeys" /></template></UInput></UFormField>
          <UFormField v-if="provider.urlTemplate.includes('{mk}') || provider.mapKey" label="地图密钥（mk）" name="mapKey" class="field-full"><UInput v-model="provider.mapKey" :type="showKeys ? 'text' : 'password'" :maxlength="2000" autocomplete="off" :disabled="saving" class="w-full" /></UFormField>
          <UFormField :label="tileFormat === 'pmtiles' ? '本地 PMTiles 文件路径' : '缓存目录'" name="folder" required class="field-full"><UInput v-model="provider.folder" :maxlength="1000" :disabled="saving" class="w-full" :placeholder="tileFormat === 'pmtiles' ? 'labels.pmtiles' : undefined" /><p class="mt-1 break-all text-xs text-muted">{{ tileFormat === 'pmtiles' ? '相对文件路径基于 ' : '相对目录基于 ' }}{{ cacheRoot }}，也可填写绝对路径。{{ tileFormat === 'pmtiles' ? '离线使用时将完整 .pmtiles 文件复制到此路径。' : '' }}</p></UFormField>
          <UFormField label="图层格式"><USelect v-model="tileFormat" :items="tileFormats" :disabled="saving" class="w-full" /><p v-if="tileFormat === 'mvt'" class="mt-1 text-xs text-muted">MVT/PBF 的样式由可选的外部目录提供，不再使用内置的手写样式。</p></UFormField>
<UFormField v-if="tileFormat !== 'raster'" label="PBF 样式模板目录" name="styleDirectory" class="field-full"><UInput v-model="provider.styleDirectory" :maxlength="1000" :disabled="saving" class="w-full" placeholder="例如：style-template 或 D:\\maps\\style" /><p class="mt-1 break-all text-xs text-muted">目录内直接放置 style.json、font/ 和 sprite/。相对路径基于 {{ cacheRoot }}；也可填写绝对路径。应用按需读取，不复制、不打包模板。未配置有效样式目录时，矢量 PBF 不会显示。{{ provider.styleDirectory ? (provider.hasStyle ? '当前已找到 style.json。' : '当前未找到 style.json；请检查目录。') : '' }}</p></UFormField>
          <UFormField v-if="tileFormat !== 'pmtiles'" label="图块坐标"><USelect v-model="provider.scheme" :items="schemes" :disabled="saving" class="w-full" /></UFormField><p v-else class="field-full text-xs text-muted">PMTiles v3 归档使用 XYZ 坐标，自动识别内部 MVT 或图片类型。</p>
          <UFormField label="加载深度" name="preload"><USelect v-model="provider.preload" :items="preloadDepths" :disabled="saving" class="w-full" /><p class="mt-1 text-xs text-muted">每个图层独立设置。0 使用原始瓦片；1–3 向更高 z 级加载影像（最高 z18），受画布限制的部分只预取缓存。普通图层始终保留 OpenLayers preload=1 的低级别兜底；矢量 PBF 保持原始比例渲染，额外预取更深层瓦片。</p></UFormField>
          <UFormField v-if="provider.urlTemplate.includes('{s}')" label="子域名"><UInput v-model="provider.subdomains" placeholder="0,1,2,3,4,5,6,7" :maxlength="200" :disabled="saving" class="w-full" /></UFormField>
          <div v-if="tileFormat !== 'pmtiles'" class="field-full"><UCheckbox v-model="provider.forceRefresh" label="强制更新缓存" :disabled="saving" /><p class="mt-1 text-xs text-muted">重新下载加载的图块；失败时使用已有缓存。</p></div>
        </div>
        <div class="mt-4 flex items-center justify-end gap-3"><span v-if="saved" class="text-sm text-primary" role="status">已保存，立即生效</span><UButton type="submit" label="保存" :loading="saving" :disabled="saving" :data-testid="`map-save-${provider.id}`" /></div>
      </UForm>
</template>
