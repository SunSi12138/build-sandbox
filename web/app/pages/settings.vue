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
  { value: 'template', label: '通知单模板' },
  { value: 'dictionaries', label: '网格与分类' },
  { value: 'backup', label: '备份与恢复' }
]
const active = ref<string | number>(tabs.some(t => t.value === route.query.tab) ? String(route.query.tab) : 'basic')
watch(active, tab => { void router.replace({ query: { ...route.query, tab: String(tab) } }) })
const form = reactive<SettingsDto>({ streetName: '', issuingUnit: '', defaultInspector: '', noticeTemplate: '', jpegQuality: 75 })
const defaultTemplate = '于{日期}对{场所}开展检查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并反馈整改情况。'
const templateHost = ref<HTMLElement | null>(null)
const templateError = ref('')
const placeholders = ['{日期}', '{场所}', '{问题}', '{期限}', '{单位名称}', '{社区}']

async function insertVariable(variable: string) {
  if (saving.value) return
  const editor = templateHost.value?.querySelector('textarea')
  if (!editor) return
  const start = editor.selectionStart ?? form.noticeTemplate.length
  const end = editor.selectionEnd ?? start
  const value = form.noticeTemplate.slice(0, start) + variable + form.noticeTemplate.slice(end)
  if (value.length > 12000) { templateError.value = '正文最多 12000 字'; return }
  templateError.value = ''
  form.noticeTemplate = value
  await nextTick()
  editor.focus({ preventScroll: true })
  editor.setSelectionRange(start + variable.length, start + variable.length)
}
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
  if (state.noticeTemplate.split('{问题}').length > 2) errors.push({ name: 'noticeTemplate', message: '{问题} 最多只能出现一次' })
  if (!state.noticeTemplate.trim()) errors.push({ name: 'noticeTemplate', message: '请填写通知单正文模板' })
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
    <div class="flex gap-2 flex-wrap"><UButton to="/catalogs" label="人员与检查类型" icon="i-lucide-list-checks" variant="outline" /><UButton to="/layers" label="地图图层" icon="i-lucide-map" variant="outline" /></div>
    <UTabs v-model="active" :items="tabs" :content="false" variant="link" :ui="{ root: 'min-w-0', list: 'overflow-x-auto flex-nowrap justify-start', trigger: 'shrink-0 min-h-10 text-sm px-3', indicator: 'h-0.5' }" />
    <UAlert v-if="error" :description="error" color="error" variant="subtle"><template #actions><UButton v-if="!ready" color="neutral" variant="outline" @click="fetchSettings(true)">重试</UButton></template></UAlert>
    <div v-if="loading && !ready" class="space-y-3" aria-busy="true"><USkeleton v-for="n in 4" :key="n" class="h-16 w-full rounded-md" /></div>
    <template v-if="ready">
      <UForm v-if="active === 'basic'" id="settings-basic" :state="form" :validate="validate" class="form-sections" @submit="saveSettings">
        <section class="form-section">
          <div class="field-grid">
            <UFormField class="field-full" label="单位名称" name="streetName" required><UInput v-model="form.streetName" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField class="field-full" label="通知单落款单位" name="issuingUnit" help="留空使用单位名称"><UInput v-model="form.issuingUnit" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField label="默认检查人" name="defaultInspector" help="可留空，每次检查时填写"><NameTagsInput data-testid="default-inspector" v-model="form.defaultInspector" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField label="照片清晰度" name="jpegQuality"><USelect v-model="form.jpegQuality" :items="[{label:'标准（推荐）',value:75},{label:'更清晰，文件较大',value:90},{label:'更省空间',value:60},...([60,75,90].includes(form.jpegQuality)?[]:[{label:`原有设置（${form.jpegQuality}）`,value:form.jpegQuality}])]" class="w-full" :disabled="saving" /></UFormField>
          </div>
        </section>
        <section class="form-section flex justify-end"><UButton data-testid="settings-save" type="submit" :loading="saving">保存</UButton></section>
      </UForm>
      <UForm v-else-if="active === 'template'" id="settings-template" :state="form" :validate="validate" class="form-sections" @submit="saveSettings">
        <section class="form-section">
          <div class="section-heading mb-2"><h2 class="!mb-0">通知单正文模板</h2><UButton color="neutral" variant="ghost" :disabled="saving" @click="form.noticeTemplate = defaultTemplate">重置模板</UButton></div>
          <div ref="templateHost"><UFormField label="正文内容" name="noticeTemplate" required><UTextarea v-model="form.noticeTemplate" :rows="10" :maxlength="12000" class="w-full" :disabled="saving" data-testid="notice-template" /></UFormField></div><p v-if="templateError" class="text-xs text-error" role="alert">{{ templateError }}</p>
          <p class="mt-3 text-xs text-muted">{问题} 为问题表格，最多一次；不填写时在正文后补表。</p>
          <p class="mt-2 text-xs text-muted">示例：经{单位名称}于{日期}对{社区}辖区{场所}检查，发现以下问题：</p>
          <div class="mt-3 flex flex-wrap items-center gap-2 text-xs text-muted"><span>变量</span><UButton v-for="placeholder in placeholders" :key="placeholder" type="button" color="neutral" variant="outline" :disabled="saving" :aria-label="`插入${placeholder}`" @pointerdown.prevent @mousedown.prevent @click="insertVariable(placeholder)">{{ placeholder }}</UButton></div>
        </section>
        <section class="form-section flex justify-end"><UButton type="submit" :loading="saving">保存</UButton></section>
      </UForm>
      <div v-else-if="active === 'dictionaries'" class="space-y-4">
        <UAlert v-if="dictionaryError" :description="dictionaryError" color="error" variant="subtle"><template #actions><UButton color="neutral" variant="outline" @click="dictionaryChanged">重试</UButton></template></UAlert>
        <SettingsDictionary kind="communities" :items="bootstrap?.communities ?? []" @changed="dictionaryChanged" />
        <SettingsDictionary kind="point-types" :items="bootstrap?.pointTypes ?? []" @changed="dictionaryChanged" />
        <SettingsDictionary kind="campaign-types" :items="bootstrap?.campaignTypes ?? []" @changed="dictionaryChanged" />
      </div>
      <SettingsBackups v-else-if="active === 'backup'" @restored="afterRestore" />
      <details class="panel panel-body"><summary class="cursor-pointer">更多工具：导入、合并、恢复</summary><div class="flex gap-3 flex-wrap mt-3"><UButton to="/imports" label="导入场所、网格" variant="outline" /><UButton to="/management" label="重复资料合并、人员交接" variant="outline" /><UButton to="/archive" label="作废记录与恢复" variant="outline" /><UButton to="/campaigns" label="整治记录" variant="outline" /></div></details>
    </template>
  </div>
</template>
