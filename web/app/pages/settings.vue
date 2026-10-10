<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { SettingsDto, SystemInfoResponse } from '~/types/api'
import { errorMessage } from '~/utils/format'

useHead({ title: '设置与备份' })
const { bootstrap, load, refresh } = useAppData()
const toast = useToast()
const ready = ref(false)
const loading = ref(true)
const saving = ref(false)
const error = ref('')
const dictionaryError = ref('')
const system = ref<SystemInfoResponse | null>(null)
const systemLoading = ref(true)
const systemError = ref('')
const form = reactive<SettingsDto>({ streetName: '', issuingUnit: '', defaultInspector: '', noticeTemplate: '', jpegQuality: 75 })
const defaultTemplate = '经我街道于{日期}对你社区辖区内{点位}开展环保排查，发现以下问题：\n{问题}\n请于{期限}前整改完毕，并将整改情况书面反馈我街道。'
const placeholders = ['{日期}', '{点位}', '{问题}', '{期限}']

async function fetchSettings(force = false) {
  loading.value = true
  error.value = ''
  try {
    const data = await load(force)
    Object.assign(form, data.settings)
    ready.value = true
  } catch (cause) { error.value = errorMessage(cause) }
  finally { loading.value = false }
}

async function fetchSystem() {
  systemLoading.value = true
  systemError.value = ''
  try { system.value = await $fetch<SystemInfoResponse>('/api/system') }
  catch (cause) { systemError.value = errorMessage(cause) }
  finally { systemLoading.value = false }
}

function validate(state: SettingsDto): FormError[] {
  const errors: FormError[] = []
  if (!state.streetName.trim()) errors.push({ name: 'streetName', message: '请填写街道全称。' })
  if (!state.issuingUnit.trim()) errors.push({ name: 'issuingUnit', message: '请填写导出材料使用的落款单位。' })
  if (!state.defaultInspector.trim()) errors.push({ name: 'defaultInspector', message: '请填写默认排查人。' })
  if (!state.noticeTemplate.trim()) errors.push({ name: 'noticeTemplate', message: '请填写通知单正文模板。' })
  if (!Number.isInteger(Number(state.jpegQuality)) || Number(state.jpegQuality) < 1 || Number(state.jpegQuality) > 100) errors.push({ name: 'jpegQuality', message: '照片质量应为 1 至 100 的整数。' })
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
    toast.add({ title: '设置已保存', description: '新建排查和导出材料会使用这些设置。', color: 'success' })
  } catch (cause) {
    error.value = errorMessage(cause)
    toast.add({ title: '设置未保存', description: error.value, color: 'error' })
  }
  finally { saving.value = false }
}

async function dictionaryChanged() {
  dictionaryError.value = ''
  try { await refresh() }
  catch (cause) { dictionaryError.value = `分类已保存，但列表暂未刷新：${errorMessage(cause)}` }
}

function afterRestore() {
  if (bootstrap.value) Object.assign(form, bootstrap.value.settings)
  dictionaryError.value = ''
  error.value = ''
  void fetchSystem()
}

function selectAddress(event: Event) {
  if (event.target instanceof HTMLInputElement) event.target.select()
}

onMounted(() => { void fetchSettings(); void fetchSystem() })
</script>

<template>
  <div class="page-stack">
    <PageHeader title="设置与备份" eyebrow="让每份留痕都有准确落款" description="维护街道信息、常用选项，以及手机访问与数据备份。" />

    <UAlert v-if="error" title="设置暂未完成处理" :description="error" color="error" variant="subtle" icon="i-lucide-circle-alert">
      <template v-if="!ready" #actions><UButton color="error" variant="soft" class="min-h-12" @click="fetchSettings(true)">重新载入</UButton></template>
    </UAlert>
    <div v-if="loading && !ready" class="space-y-4" aria-busy="true" aria-label="正在载入设置"><USkeleton class="h-96 w-full rounded-xl" /><USkeleton class="h-56 w-full rounded-xl" /></div>
    <template v-if="ready">
      <UForm id="settings-form" :state="form" :validate="validate" class="space-y-5" @submit="saveSettings">
        <UCard>
          <template #header><div><h2 class="font-semibold text-highlighted">街道与留痕信息</h2><p class="mt-1 text-sm text-muted">排查人可在每次登记时修改，街道及落款信息用于 Word 材料。</p></div></template>
          <div class="grid gap-5 md:grid-cols-2">
            <UFormField label="街道全称" name="streetName" required><UInput v-model="form.streetName" placeholder="例如：示例街道办事处" :maxlength="100" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
            <UFormField label="落款单位" name="issuingUnit" required><UInput v-model="form.issuingUnit" placeholder="填写材料末尾的落款单位" :maxlength="100" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
            <UFormField label="默认排查人" name="defaultInspector" required><UInput v-model="form.defaultInspector" placeholder="填写姓名，多人可用顿号分隔" :maxlength="100" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
            <UFormField label="照片 JPEG 质量" name="jpegQuality" description="1 至 100，建议 75。数值越高，画面越清晰、文件也越大。"><UInput v-model.number="form.jpegQuality" type="number" min="1" max="100" step="1" inputmode="numeric" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
          </div>
        </UCard>
        <UCard>
          <template #header><div class="flex flex-wrap items-center justify-between gap-3"><h2 class="font-semibold text-highlighted">整改通知单正文模板</h2><UButton color="neutral" variant="ghost" class="min-h-12" :disabled="saving" @click="form.noticeTemplate = defaultTemplate">使用示例正文</UButton></div></template>
          <UFormField label="正文内容" name="noticeTemplate" required>
            <UTextarea v-model="form.noticeTemplate" :rows="7" :maxlength="12000" size="xl" class="w-full" :ui="{ base: 'leading-7' }" :disabled="saving" />
          </UFormField>
          <div class="mt-3 flex flex-wrap items-center gap-2 text-xs text-muted"><span>导出时自动替换：</span><UBadge v-for="placeholder in placeholders" :key="placeholder" color="neutral" variant="subtle">{{ placeholder }}</UBadge></div>
          <p class="mt-3 text-xs leading-6 text-muted">保留花括号及其中的文字即可自动填入本次排查信息。{问题}会列出本次排查登记的全部问题。</p>
          <template #footer><div class="flex justify-end"><UButton data-testid="settings-save" type="submit" icon="i-lucide-check" size="xl" class="min-h-12 w-full justify-center sm:w-auto" :loading="saving">保存设置</UButton></div></template>
        </UCard>
      </UForm>

      <UAlert v-if="dictionaryError" :description="dictionaryError" color="warning" variant="subtle"><template #actions><UButton color="warning" variant="soft" class="min-h-12" @click="dictionaryChanged">刷新列表</UButton></template></UAlert>
      <div class="grid items-start gap-5 lg:grid-cols-2"><SettingsDictionary kind="communities" :items="bootstrap?.communities ?? []" @changed="dictionaryChanged" /><SettingsDictionary kind="point-types" :items="bootstrap?.pointTypes ?? []" @changed="dictionaryChanged" /></div>
    </template>

    <UCard>
      <template #header><div class="flex items-center justify-between gap-3"><div><h2 class="font-semibold text-highlighted">手机访问地址</h2><p class="mt-1 text-sm text-muted">手机与电脑连接同一局域网，或加入同一个蒲公英网络。</p></div><UButton icon="i-lucide-refresh-cw" color="neutral" variant="ghost" class="min-h-12 shrink-0" :loading="systemLoading" @click="fetchSystem">刷新</UButton></div></template>
      <UAlert v-if="systemError" :description="systemError" color="error" variant="subtle" />
      <USkeleton v-else-if="systemLoading && !system" class="h-12 w-full rounded-lg" />
      <div v-else-if="system" class="space-y-4">
        <p class="text-sm leading-6 text-muted">在手机浏览器中输入下列地址。保持电脑开机、工具运行即可访问。</p>
        <div v-if="system.addresses.length" class="grid gap-3 md:grid-cols-2"><UInput v-for="address in system.addresses" :key="address" :model-value="address" readonly :aria-label="`手机访问地址 ${address}`" icon="i-lucide-wifi" size="xl" class="w-full" :ui="{ base: 'min-h-12 font-mono text-sm' }" @focus="selectAddress" /></div>
        <UAlert v-else title="暂未找到局域网地址" description="请检查电脑是否已连接网络，连接后点击刷新。" color="warning" variant="subtle" />
        <p class="text-xs leading-6 text-muted">手机无法打开时，请检查电脑防火墙是否允许端口 {{ system.port }}，并确认手机使用的网络能访问这台电脑。</p>
      </div>
    </UCard>
    <section id="backup" class="scroll-mt-6"><SettingsBackups @restored="afterRestore" /></section>
  </div>
</template>
