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
  { value: 'dictionaries', label: '社区与分类' },
  { value: 'tags', label: '标签管理' },
  { value: 'maps', label: '地图管理' },
  { value: 'backup', label: '备份与恢复' }
]
const active = ref<string | number>(tabs.some(t => t.value === route.query.tab) ? String(route.query.tab) : 'basic')
watch(active, tab => { void router.replace({ query: { ...route.query, tab: String(tab) } }) })
watch(() => route.query.tab, tab => { const selected = tabs.some(item => item.value === tab) ? String(tab) : 'basic'; if (active.value !== selected) active.value = selected })
const form = reactive<SettingsDto>({ streetName: '', issuingUnit: '', defaultInspector: '', noticeTemplate: '', jpegQuality: 75 })
const defaultTemplate = '经我街道于{日期}对你社区辖区内{点位}开展环保排查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并将整改情况书面反馈我街道。'
const advancedQuality = ref(false)
const qualityOptions = [{ label: '节省空间', value: '60' }, { label: '标准', value: '75' }, { label: '清晰', value: '90' }, { label: '自定义', value: 'custom' }]
const qualityPreset = computed({ get: () => [60, 75, 90].includes(Number(form.jpegQuality)) ? String(form.jpegQuality) : 'custom', set: (value: string) => { if (value === 'custom') advancedQuality.value = true; else { form.jpegQuality = Number(value); advancedQuality.value = false } } })
const dictionaryKind = ref<'communities' | 'point-types' | 'remediation-types' | 'issue-types' | 'activity-types'>('communities')
const dictionaryOptions = computed(() => [
  { label: '社区', value: 'communities' }, { label: '点位类型', value: 'point-types' },
  { label: '问题类型', value: 'issue-types' }, { label: '整治类别', value: 'remediation-types' }, { label: '活动类型', value: 'activity-types' }
])
const dictionaryItems = computed(() => {
  const data = bootstrap.value
  if (!data) return []
  return ({ communities: data.communities, 'point-types': data.pointTypes, 'issue-types': data.issueTypes, 'remediation-types': data.remediationTypes, 'activity-types': data.activityTypes })[dictionaryKind.value] ?? []
})
const templateHost = ref<HTMLElement | null>(null)
const templateError = ref('')
const placeholders = ['{日期}', '{点位}', '{问题}', '{期限}', '{街道全称}', '{社区}']

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
  if (!state.streetName.trim()) errors.push({ name: 'streetName', message: '请填写街道全称' })
  if (!state.issuingUnit.trim()) errors.push({ name: 'issuingUnit', message: '请填写落款单位' })
  if (!state.defaultInspector.trim()) errors.push({ name: 'defaultInspector', message: '请填写默认排查人' })
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
    const result = await $fetch<SettingsDto>('/api/settings', { method: 'PUT', body: { ...form, jpegQuality: Number(form.jpegQuality) } })
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
    <PageHeader title="工具设置" />
    <UTabs v-model="active" :items="tabs" :content="false" variant="link" :ui="{ root: 'min-w-0', list: 'overflow-x-auto flex-nowrap justify-start', trigger: 'shrink-0 min-h-10 text-sm px-3', indicator: 'h-0.5' }" />
    <UAlert v-if="error" :description="error" color="error" variant="subtle"><template #actions><UButton v-if="!ready" color="neutral" variant="outline" @click="fetchSettings(true)">重试</UButton></template></UAlert>
    <div v-if="loading && !ready" class="space-y-3" aria-busy="true"><USkeleton v-for="n in 4" :key="n" class="h-16 w-full rounded-md" /></div>
    <template v-if="ready">
      <UForm v-if="active === 'basic'" id="settings-basic" :state="form" :validate="validate" class="form-sections" @submit="saveSettings">
        <section class="form-section">
          <div class="field-grid">
            <UFormField class="field-full" label="街道全称" name="streetName" required><UInput v-model="form.streetName" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField class="field-full" label="落款单位" name="issuingUnit" required><UInput v-model="form.issuingUnit" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField class="field-full" label="默认排查人" name="defaultInspector" required><NameTagsInput data-testid="default-inspector" v-model="form.defaultInspector" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
            <UFormField label="照片清晰度"><USelect v-model="qualityPreset" :items="qualityOptions" class="w-full" :disabled="saving" /></UFormField>
            <div class="field-full"><UButton label="高级设置" color="neutral" variant="link" :aria-expanded="advancedQuality" @click="advancedQuality = !advancedQuality" />
              <UFormField v-if="advancedQuality" label="照片质量（1–100）" name="jpegQuality"><UInput v-model.number="form.jpegQuality" type="number" min="1" max="100" step="1" inputmode="numeric" class="w-full" :disabled="saving" /></UFormField>
            </div>
          </div>
        </section>
        <section class="form-section flex justify-end"><UButton data-testid="settings-save" type="submit" :loading="saving">保存</UButton></section>
      </UForm>
      <UForm v-else-if="active === 'template'" id="settings-template" :state="form" :validate="validate" class="form-sections" @submit="saveSettings">
        <section class="form-section">
          <div class="section-heading mb-2"><h2 class="!mb-0">通知单正文模板</h2><UButton color="neutral" variant="ghost" :disabled="saving" @click="form.noticeTemplate = defaultTemplate">重置模板</UButton></div>
          <div ref="templateHost"><UFormField label="正文内容" name="noticeTemplate" required><UTextarea v-model="form.noticeTemplate" :rows="10" :maxlength="12000" class="w-full" :disabled="saving" data-testid="notice-template" /></UFormField></div><p v-if="templateError" class="text-xs text-error" role="alert">{{ templateError }}</p>
          <p class="mt-3 text-xs text-muted">{问题} 为问题表格，最多一次；不填写时在正文后补表。</p>
          <details class="mt-2 text-xs text-muted"><summary class="min-h-10 flex items-center cursor-pointer">查看示例</summary><p>经{街道全称}于{日期}对{社区}辖区{点位}排查，发现以下问题：</p></details>
          <div class="mt-3 flex flex-wrap items-center gap-2 text-xs text-muted"><span>变量</span><UButton v-for="placeholder in placeholders" :key="placeholder" type="button" color="neutral" variant="outline" :disabled="saving" :aria-label="`插入${placeholder}`" @pointerdown.prevent @mousedown.prevent @click="insertVariable(placeholder)">{{ placeholder }}</UButton></div>
        </section>
        <section class="form-section flex justify-end"><UButton type="submit" :loading="saving">保存</UButton></section>
      </UForm>
      <div v-else-if="active === 'dictionaries'" class="space-y-4">
        <UAlert v-if="dictionaryError" :description="dictionaryError" color="error" variant="subtle"><template #actions><UButton color="neutral" variant="outline" @click="dictionaryChanged">重试</UButton></template></UAlert>
        <UFormField label="分类管理"><USelect v-model="dictionaryKind" :items="dictionaryOptions" class="w-full sm:max-w-xs" aria-label="选择分类组" /></UFormField>
        <SettingsDictionary :key="dictionaryKind" :kind="dictionaryKind" :items="dictionaryItems" @changed="dictionaryChanged" />
      </div>
      <SettingsTags v-else-if="active === 'tags'" />
      <SettingsMaps v-else-if="active === 'maps'" />
      <SettingsBackups v-else-if="active === 'backup'" @restored="afterRestore" />
    </template>
  </div>
</template>
