<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { CampaignDetailDto, CampaignWriteRequest, PhotoUploadDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'
const props = defineProps<{ initial?: CampaignDetailDto | null }>()
const emit = defineEmits<{ saved: [detail: CampaignDetailDto] }>()
const { bootstrap, load } = useAppData()
const toast = useToast()
const draftKey = 'street-eco-campaign-draft-v2'
interface CampaignState {
  name: string; typeId: string; communityId: string; location: string; date: string; endDate: string; organizer: string; participants: string
  participantCount: string; vehicleCount: string; garbageTons: string; areaSqm: string
  content: string; outcome: string; notes: string; beforePhotos: PhotoUploadDto[]; processPhotos: PhotoUploadDto[]; afterPhotos: PhotoUploadDto[]
}
const state = reactive<CampaignState>({ name: '', typeId: '', communityId: '', location: '', date: todayLocal(), endDate: '', organizer: '', participants: '', participantCount: '', vehicleCount: '', garbageTons: '', areaSqm: '', content: '', outcome: '', notes: '', beforePhotos: [], processPhotos: [], afterPhotos: [] })
const saving = ref(false), loading = ref(true), error = ref(''), savedAt = ref(''), storageFailed = ref(false), leaveOpen = ref(false)
const phaseBusy = reactive({ before: false, process: false, after: false })
const phaseFailed = reactive({ before: false, process: false, after: false })
const photosFailed = computed(() => Object.values(phaseFailed).some(Boolean))
const formDisabled = computed(() => loading.value || saving.value)
const photosBusy = computed(() => Object.values(phaseBusy).some(Boolean))
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const types = computed(() => (bootstrap.value?.campaignTypes ?? []).map(item => ({ label: item.name, value: item.id })))
let ready = false, completed = false, original = '', leaveResolve: ((value: boolean) => void) | undefined
const quantityFields = ['participantCount', 'vehicleCount', 'garbageTons', 'areaSqm'] as const
const textFields = ['name', 'typeId', 'communityId', 'location', 'date', 'endDate', 'organizer', 'participants', 'content', 'outcome', 'notes'] as const
const photoFields = ['beforePhotos', 'processPhotos', 'afterPhotos'] as const
function quantity(value: number | string): number | null { return value === '' ? null : Number(value) }
function restoreDraft() {
  try {
    const raw = localStorage.getItem(draftKey)
    if (!raw) return
    const saved = JSON.parse(raw) as { version?: number; state?: Record<string, unknown>; savedAt?: string }
    if (saved.version !== 2 || !saved.state) { localStorage.removeItem(draftKey); return }
    for (const field of textFields) if (typeof saved.state[field] === 'string') state[field] = saved.state[field] as string
    for (const field of quantityFields) { const value = saved.state[field]; if (typeof value === 'string' || typeof value === 'number') state[field] = String(value) }
    for (const field of photoFields) {
      const values = saved.state[field]
      if (Array.isArray(values)) state[field] = values.filter((value): value is PhotoUploadDto => Boolean(value && typeof value.id === 'string' && /^[0-9a-f]{32}$/.test(value.id) && typeof value.width === 'number' && typeof value.height === 'number')).slice(0, 20)
    }
    if (typeof saved.savedAt === 'string') savedAt.value = saved.savedAt
  } catch { try { localStorage.removeItem(draftKey) } catch { storageFailed.value = true } }
}
function persist() {
  if (!ready || props.initial || completed) return
  const now = new Date()
  const time = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`
  try { localStorage.setItem(draftKey, JSON.stringify({ version: 2, state, savedAt: time })); savedAt.value = time; storageFailed.value = false }
  catch { storageFailed.value = true }
}
watch(state, persist, { deep: true, flush: 'sync' })
onMounted(async () => {
  try {
    await load()
    if (props.initial) {
      const record = props.initial.campaign
      for (const field of textFields) state[field] = record[field] === null ? '' : String(record[field])
      for (const field of quantityFields) state[field] = record[field] === null ? '' : String(record[field])
      state.beforePhotos = props.initial.photos.filter(photo => photo.phase === 'before').map(photo => ({ id: photo.id, width: photo.width, height: photo.height }))
      state.processPhotos = props.initial.photos.filter(photo => photo.phase === 'process').map(photo => ({ id: photo.id, width: photo.width, height: photo.height }))
      state.afterPhotos = props.initial.photos.filter(photo => photo.phase === 'after').map(photo => ({ id: photo.id, width: photo.width, height: photo.height }))
    } else { state.date = bootstrap.value?.today ?? todayLocal(); state.organizer = bootstrap.value?.settings.issuingUnit ?? ''; restoreDraft() }
    original = JSON.stringify(state); ready = true
  } catch (value) { error.value = errorMessage(value) }
  finally { loading.value = false }
  window.addEventListener('beforeunload', beforeUnload)
  document.addEventListener('visibilitychange', persist)
})
function validate(value: CampaignState): FormError[] {
  const errors: FormError[] = []
  for (const [field, label] of [['name', '整治名称'], ['typeId', '整治类型'], ['communityId', '所属社区'], ['location', '整治地点'], ['date', '整治日期'], ['content', '整治内容']] as const)
    if (!value[field].trim()) errors.push({ name: field, message: `${field.endsWith('Id') || field === 'date' ? '请选择' : '请填写'}${label}` })
  if (value.endDate && value.endDate < value.date) errors.push({ name: 'endDate', message: '结束日期不能早于整治日期' })
  for (const field of quantityFields) {
    const number = quantity(value[field])
    if (number !== null && (!Number.isFinite(number) || number < 0 || number > (field === 'participantCount' || field === 'vehicleCount' ? 2147483647 : 1000000000000))) errors.push({ name: field, message: '请输入有效的非负数量' })
    else if (number !== null && (field === 'participantCount' || field === 'vehicleCount') && !Number.isInteger(number)) errors.push({ name: field, message: '请输入整数' })
  }
  return errors
}
async function save() {
  if (loading.value || saving.value || photosBusy.value || validate(state).length) return
  saving.value = true; error.value = ''
  const body: CampaignWriteRequest = {
    name: state.name, typeId: state.typeId, communityId: state.communityId, location: state.location, date: state.date, endDate: state.endDate || null,
    organizer: state.organizer, participants: state.participants, participantCount: quantity(state.participantCount), vehicleCount: quantity(state.vehicleCount),
    garbageTons: quantity(state.garbageTons), areaSqm: quantity(state.areaSqm), content: state.content, outcome: state.outcome, notes: state.notes,
    beforePhotoIds: state.beforePhotos.map(photo => photo.id), processPhotoIds: state.processPhotos.map(photo => photo.id), afterPhotoIds: state.afterPhotos.map(photo => photo.id)
  }
  try {
    const detail = await $fetch<CampaignDetailDto>(props.initial ? `/api/campaigns/${props.initial.campaign.id}` : '/api/campaigns', { method: props.initial ? 'PUT' : 'POST', body, retry: 0 })
    completed = true
    if (!props.initial) { try { localStorage.removeItem(draftKey) } catch { /* The saved server record is authoritative. */ } }
    toast.add({ title: '已保存', color: 'success', duration: 2000 }); emit('saved', detail)
  } catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
function beforeUnload(event: BeforeUnloadEvent) {
  persist()
  if (!completed && (saving.value || photosBusy.value || Boolean(props.initial && JSON.stringify(state) !== original))) { event.preventDefault(); event.returnValue = '' }
}
function finishLeave(leave: boolean) { leaveOpen.value = false; leaveResolve?.(leave); leaveResolve = undefined }
onBeforeRouteLeave(() => {
  if (saving.value) return false
  if (completed || (!photosBusy.value && (!props.initial || JSON.stringify(state) === original))) return true
  leaveOpen.value = true
  return new Promise<boolean>(resolve => { leaveResolve = resolve })
})
onBeforeUnmount(() => { persist(); window.removeEventListener('beforeunload', beforeUnload); document.removeEventListener('visibilitychange', persist); leaveResolve?.(false) })
</script>
<template>
  <UAlert v-if="error" :description="error" color="error" variant="subtle" role="alert" />
  <p v-if="!loading && (!communities.length || !types.length)" class="text-sm text-warning">请先维护社区和整治类型</p>
  <UForm :state="state" :validate="validate" class="form-sections" @submit="save">
    <section class="form-section"><h2>基本信息</h2><div class="field-grid">
      <UFormField label="整治名称" name="name" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" data-testid="campaign-name"  :disabled="formDisabled" /></UFormField>
      <UFormField label="整治类型" name="typeId" required><USelect v-model="state.typeId" :items="types" placeholder="请选择" class="w-full" data-testid="campaign-type"  :disabled="formDisabled" /></UFormField>
      <UFormField label="所属社区" name="communityId" required><USelect v-model="state.communityId" :items="communities" placeholder="请选择" class="w-full" data-testid="campaign-community"  :disabled="formDisabled" /></UFormField>
      <UFormField label="整治地点" name="location" required class="field-full"><UInput v-model="state.location" maxlength="1000" class="w-full" data-testid="campaign-location"  :disabled="formDisabled" /></UFormField>
      <UFormField label="整治日期" name="date" required><UInput v-model="state.date" type="date" class="w-full" data-testid="campaign-date"  :disabled="formDisabled" /></UFormField>
      <UFormField label="结束日期" name="endDate"><UInput v-model="state.endDate" type="date" :min="state.date" class="w-full"  :disabled="formDisabled" /></UFormField>
      <UFormField label="组织单位" name="organizer" class="field-full"><UInput v-model="state.organizer" maxlength="200" class="w-full"  :disabled="formDisabled" /></UFormField>
      <UFormField label="参加单位及人员" name="participants" class="field-full"><UTextarea v-model="state.participants" :rows="2" maxlength="2000" class="w-full"  :disabled="formDisabled" /></UFormField>
      <UFormField label="参加人数（人）" name="participantCount"><UInput v-model="state.participantCount" type="number" inputmode="numeric" min="0" step="1" class="w-full" data-testid="campaign-people"  :disabled="formDisabled" /></UFormField>
      <UFormField label="出动车辆（辆）" name="vehicleCount"><UInput v-model="state.vehicleCount" type="number" inputmode="numeric" min="0" step="1" class="w-full"  :disabled="formDisabled" /></UFormField>
      <UFormField label="清运垃圾（吨）" name="garbageTons"><UInput v-model="state.garbageTons" type="number" inputmode="decimal" min="0" step="0.01" class="w-full" data-testid="campaign-garbage"  :disabled="formDisabled" /></UFormField>
      <UFormField label="清理面积（㎡）" name="areaSqm"><UInput v-model="state.areaSqm" type="number" inputmode="decimal" min="0" step="0.01" class="w-full"  :disabled="formDisabled" /></UFormField>
    </div></section>
    <section class="form-section"><h2>整治情况</h2><div class="field-grid">
      <UFormField label="整治内容" name="content" required class="field-full"><UTextarea v-model="state.content" :rows="4" maxlength="12000" class="w-full" data-testid="campaign-content"  :disabled="formDisabled" /></UFormField>
      <UFormField label="整治成效" name="outcome" class="field-full"><UTextarea v-model="state.outcome" :rows="3" maxlength="12000" class="w-full"  :disabled="formDisabled" /></UFormField>
      <UFormField label="备注" name="notes" class="field-full"><UTextarea v-model="state.notes" :rows="2" maxlength="8000" class="w-full"  :disabled="formDisabled" /></UFormField>
    </div></section>
    <section class="form-section" data-testid="campaign-before"><h2>整治前照片</h2><PhotoUploader v-model="state.beforePhotos" phase="before" label="整治前" :max="20" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="phaseBusy.before = $event" @failed="phaseFailed.before = $event" /></section>
    <section class="form-section" data-testid="campaign-process"><h2>整治中照片</h2><PhotoUploader v-model="state.processPhotos" phase="process" label="整治中" :max="20" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="phaseBusy.process = $event" @failed="phaseFailed.process = $event" /></section>
    <section class="form-section" data-testid="campaign-after"><h2>整治后照片</h2><PhotoUploader v-model="state.afterPhotos" phase="after" label="整治后" :max="20" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="phaseBusy.after = $event" @failed="phaseFailed.after = $event" /></section>
    <div class="form-actions"><span class="draft-state" aria-live="polite">{{ photosFailed ? '有照片未完成，请重试或移除' : photosBusy ? '照片处理中' : props.initial ? '编辑整治' : storageFailed ? '草稿未保存' : savedAt ? `草稿已保存 ${savedAt}` : '草稿' }}</span><UButton type="submit" label="保存" :loading="saving" :disabled="loading || photosBusy || !communities.length || !types.length" data-testid="campaign-save" /></div>
  </UForm>
  <UModal v-model:open="leaveOpen" title="离开页面？" :description="photosBusy ? '离开后将中断照片上传' : '未保存的更改将丢失'" :dismissible="false" :close="false"><template #footer><UButton label="取消" color="neutral" variant="outline" @click="finishLeave(false)" /><UButton label="离开" color="error" @click="finishLeave(true)" /></template></UModal>
</template>
