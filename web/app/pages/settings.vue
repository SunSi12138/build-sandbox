<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { SettingsDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

useHead({ title: '工具设置' })
const { bootstrap, load, refresh } = useAppData()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const ready = ref(false)
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const dictionaryError = ref('')
const tabs = [
  { value: 'basic', label: '基本信息' },
  { value: 'grids', label: '网格' },
  { value: 'places', label: '场所与用途' },
  { value: 'map', label: '地图设置' },
  { value: 'backup', label: '备份与恢复' }
]
const active = ref<string | number>(tabs.some(t => t.value === route.query.tab) ? String(route.query.tab) : 'basic')
watch(active, tab => { void router.replace({ query: { ...route.query, tab: String(tab) } }) })
const form = reactive<SettingsDto>({ streetName: '', issuingUnit: '', defaultInspector: '', noticeTemplate: '', jpegQuality: 75 })
async function fetchSettings(force = false) {
  loading.value = true
  error.value = ''
  try { const data = await load(force); Object.assign(form, data.settings); ready.value = true }
  catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
}
function validate(state: SettingsDto): FormError[] {
  const errors: FormError[] = []
  if (active.value === 'basic' && !state.streetName.trim()) errors.push({ name: 'streetName', message: '请填写单位名称' })
  if (!Number.isInteger(Number(state.jpegQuality)) || Number(state.jpegQuality) < 1 || Number(state.jpegQuality) > 100) errors.push({ name: 'jpegQuality', message: '请填写 1 至 100 的整数' })
  return errors
}
async function saveSettings() {
  if (saving.value) return
  saving.value = true
  error.value = ''
  try {
    const result = await $fetch<SettingsDto>('/api/settings', { method: 'PUT', body: { ...form, issuingUnit: form.issuingUnit.trim() || form.streetName.trim(), jpegQuality: Number(form.jpegQuality) } })
    Object.assign(form, result)
    if (bootstrap.value) bootstrap.value = { ...bootstrap.value, settings: result }
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
async function dictionaryChanged() {
  dictionaryError.value = ''
  try { await refresh() } catch (cause) { dictionaryError.value = errorMessage(cause) }
}
function afterRestore() {
  if (bootstrap.value) Object.assign(form, bootstrap.value.settings)
  dictionaryError.value = ''; error.value = ''
}
onMounted(() => { void fetchSettings() })
</script>

<template>
  <div class="page-stack">
    <PageHeader title="系统设置" />
    <div class="flex gap-2 flex-wrap"><UButton to="/catalogs" label="人员与检查类型" icon="i-lucide-list-checks" variant="outline" /></div>
    <UTabs v-model="active" :items="tabs" :content="false" variant="link" :ui="{ root: 'min-w-0', list: 'overflow-x-auto flex-nowrap justify-start', trigger: 'shrink-0 min-h-10 text-sm px-3', indicator: 'h-0.5' }" />
    <UAlert v-if="error" :description="error" color="error" variant="subtle"><template #actions><UButton v-if="!ready" color="neutral" variant="outline" @click="fetchSettings(true)">重试</UButton></template></UAlert>
    <div v-if="loading && !ready" class="space-y-3" aria-busy="true"><USkeleton v-for="n in 4" :key="n" class="h-16 w-full rounded-md" /></div>
    <template v-if="ready">
      <UForm v-if="active === 'basic'" id="settings-basic" :state="form" :validate="validate" class="form-sections" @submit="saveSettings">
        <section class="form-section">
          <div class="field-grid">
            <UFormField class="field-full" label="单位名称" name="streetName" required><UInput v-model="form.streetName" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField label="默认检查人" name="defaultInspector"><NameTagsInput data-testid="default-inspector" v-model="form.defaultInspector" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField label="照片清晰度" name="jpegQuality"><USelect v-model="form.jpegQuality" :items="[{label:'标准（推荐）',value:75},{label:'更清晰，文件较大',value:90},{label:'更省空间',value:60}]" class="w-full" :disabled="saving" /></UFormField>
          </div>
        </section>
        <section class="form-section flex justify-end"><UButton data-testid="settings-save" type="submit" :loading="saving">保存</UButton></section>
      </UForm>
      <div v-else-if="active === 'grids' || active === 'places'" class="space-y-4">
        <UAlert v-if="dictionaryError" :description="dictionaryError" color="error" variant="subtle"><template #actions><UButton color="neutral" variant="outline" @click="dictionaryChanged">重试</UButton></template></UAlert>
        <div v-if="active==='grids'" class="flex gap-3 flex-wrap"><UButton to="/imports?kind=grid" label="导入网格" icon="i-lucide-upload" variant="outline" /><UButton to="/imports?kind=point" label="导入场所" variant="outline" /></div>
        <SettingsDictionary v-if="active==='grids'" kind="communities" :items="bootstrap?.communities ?? []" @changed="dictionaryChanged" />
        <SettingsDictionary v-if="active==='places'" kind="point-types" :items="bootstrap?.pointTypes ?? []" @changed="dictionaryChanged" /><MapCatalogSettings v-if="active==='places'" />
        <SettingsDictionary v-if="active==='places'" kind="campaign-types" :items="bootstrap?.campaignTypes ?? []" @changed="dictionaryChanged" />
      </div>
      <SettingsMapLayers v-else-if="active === 'map'" />
      <SettingsBackups v-else-if="active === 'backup'" @restored="afterRestore" />
      <details class="panel panel-body"><summary class="cursor-pointer">资料管理</summary><div class="flex gap-3 flex-wrap mt-3"><UButton to="/management" label="重复资料合并" variant="outline" /><UButton to="/archive" label="作废记录与恢复" variant="outline" /></div></details>
    </template>
  </div>
</template>
