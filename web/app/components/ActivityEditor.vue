<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { ActivityDetailDto, ActivityWriteRequest, PhotoUploadDto } from '~/types/api'
import { errorMessage, todayLocal } from '~/utils/format'

const props = defineProps<{ initial?: ActivityDetailDto | null }>()
const emit = defineEmits<{ saved: [detail: ActivityDetailDto] }>()
const { bootstrap, load } = useAppData()
const toast = useToast()
const draftKey = 'street-eco.activity-draft.v1'
interface State {
  name: string; typeId: string; communityId: string; location: string; date: string; endDate: string
  organizer: string; participants: string; participantCount: string; content: string; outcome: string; notes: string
  photos: PhotoUploadDto[]
}
function empty(): State { return { name: '', typeId: '', communityId: 'all', location: '', date: todayLocal(), endDate: '',
  organizer: '', participants: '', participantCount: '', content: '', outcome: '', notes: '', photos: [] } }
const state = reactive<State>(empty())
const initializing = ref(true)
const saving = ref(false)
const uploadBusy = ref(false)
const uploadFailed = ref(false)
const error = ref('')
const saved = ref(false)
const ready = ref(false)
const moreOpen = ref(false)
const leaveOpen = ref(false)
const draftSaved = ref(false)
let original = ''
let persistTimer: ReturnType<typeof setTimeout> | undefined
let resolveLeave: ((leave: boolean) => void) | undefined
const dirty = computed(() => Boolean(props.initial && ready.value && JSON.stringify(state) !== original))
function persist() {
  clearTimeout(persistTimer)
  if (!ready.value || props.initial || saved.value) return
  try {
    localStorage.setItem(draftKey, JSON.stringify({ version: 1, data: state }))
    draftSaved.value = true
  } catch { error.value = '草稿未能保存，请勿关闭页面。' }
}
function finishLeave(leave: boolean) { leaveOpen.value = false; resolveLeave?.(leave); resolveLeave = undefined }
function guardLeave() {
  persist()
  if (saving.value) return false
  if (saved.value || (!uploadBusy.value && !dirty.value)) return true
  leaveOpen.value = true
  return new Promise<boolean>(resolve => { resolveLeave = resolve })
}
function beforeUnload(event: BeforeUnloadEvent) {
  persist()
  if (!saved.value && (saving.value || uploadBusy.value || dirty.value)) { event.preventDefault(); event.returnValue = '' }
}
onBeforeRouteLeave(guardLeave)
onBeforeRouteUpdate(guardLeave)
onBeforeUnmount(() => {
  persist()
  window.removeEventListener('beforeunload', beforeUnload)
  document.removeEventListener('visibilitychange', persist)
  resolveLeave?.(false)
})
const communities = computed(() => [{ label: '跨社区', value: 'all' },
  ...(bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id }))])
const types = computed(() => (bootstrap.value?.activityTypes ?? []).map(item => ({ label: item.name, value: item.id })))
const disabled = computed(() => initializing.value || saving.value)
function restoreDraft() {
  try {
    const raw = localStorage.getItem(draftKey)
    if (!raw) return
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || !('version' in parsed) || parsed.version !== 1 || !('data' in parsed)) return
    const data = parsed.data as Record<string, unknown>
    const base = empty()
    for (const key of Object.keys(base) as (keyof State)[]) {
      if (key === 'photos') continue
      if (typeof data[key] === 'string') (state as unknown as Record<string, unknown>)[key] = data[key]
    }
    if (Array.isArray(data.photos)) state.photos = data.photos.filter((photo: unknown): photo is PhotoUploadDto => {
      if (!photo || typeof photo !== 'object') return false
      const p = photo as PhotoUploadDto
      return typeof p.id === 'string' && /^[0-9a-f]{32}$/.test(p.id) &&
        Number.isInteger(p.width) && p.width > 0 && Number.isInteger(p.height) && p.height > 0
    }).slice(0, 20)
  } catch { error.value = '草稿无法读取，可重新填写后保存。' }
}
watch(state, () => {
  if (!ready.value || props.initial || saved.value) return
  draftSaved.value = false
  clearTimeout(persistTimer)
  persistTimer = setTimeout(persist, 250)
}, { deep: true })
watch(() => props.initial, initial => {
  if (!initial) return
  const record = initial.activity
  Object.assign(state, {
    name: record.name, typeId: record.typeId, communityId: record.communityId ?? 'all', location: record.location,
    date: record.date, endDate: record.endDate ?? '', organizer: record.organizer, participants: record.participants,
    participantCount: record.participantCount === null ? '' : String(record.participantCount),
    content: record.content, outcome: record.outcome, notes: record.notes,
    photos: initial.photos.map(photo => ({ id: photo.id, width: photo.width, height: photo.height }))
  })
}, { immediate: true })
async function initialize() {
  initializing.value = true
  try {
    await load()
    if (!props.initial) {
      state.organizer = bootstrap.value?.settings.issuingUnit ?? ''
      restoreDraft()
      if (!state.communityId) state.communityId = 'all'
    }
    original = JSON.stringify(state)
    moreOpen.value = Boolean(state.endDate || state.participants || state.participantCount || state.outcome || state.notes)
    ready.value = true
  } catch (cause) { error.value = errorMessage(cause) }
  finally { initializing.value = false }
}
onMounted(() => {
  window.addEventListener('beforeunload', beforeUnload)
  document.addEventListener('visibilitychange', persist)
  void initialize()
})
function validate(data: State): FormError[] {
  const issues: FormError[] = []
  if (!data.name.trim()) issues.push({ name: 'name', message: '请填写活动名称' })
  if (!data.typeId) issues.push({ name: 'typeId', message: '请选择活动类型' })
  if (!data.date || data.date > todayLocal()) issues.push({ name: 'date', message: '请选择有效的开展日期' })
  if (data.endDate && data.endDate < data.date) issues.push({ name: 'endDate', message: '结束日期不能早于开展日期' })
  if (!data.content.trim()) issues.push({ name: 'content', message: '请填写开展情况' })
  if (data.participantCount && (!/^\d+$/.test(data.participantCount) || Number(data.participantCount) > 100000000))
    issues.push({ name: 'participantCount', message: '请填写有效的非负整数' })
  if (issues.some(issue => issue.name === 'endDate' || issue.name === 'participantCount')) moreOpen.value = true
  return issues
}
async function save() {
  if (saving.value || initializing.value) return
  if (uploadBusy.value || uploadFailed.value) { error.value = '请完成或移除未成功上传的照片'; return }
  if (validate(state).length) { error.value = '请检查必填项'; return }
  saving.value = true
  error.value = ''
  const body: ActivityWriteRequest = {
    name: state.name.trim(), typeId: state.typeId, date: state.date, endDate: state.endDate || null,
    communityId: state.communityId === 'all' ? null : state.communityId, location: state.location, organizer: state.organizer,
    participants: state.participants, participantCount: state.participantCount ? Number(state.participantCount) : null,
    content: state.content.trim(), outcome: state.outcome, notes: state.notes, photoIds: state.photos.map(photo => photo.id)
  }
  try {
    const id = props.initial?.activity.id
    const result = await $fetch<ActivityDetailDto>(id ? `/api/activities/${id}` : '/api/activities',
      { method: id ? 'PUT' : 'POST', body, retry: 0 })
    saved.value = true
    clearTimeout(persistTimer)
    if (!id) { try { localStorage.removeItem(draftKey) } catch { /* record was saved */ } }
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
    emit('saved', result)
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
</script>

<template>
  <UForm id="activity-form" :state="state" :validate="validate" class="space-y-3" @submit="save">
    <UAlert v-if="error" :description="error" color="error" variant="subtle" />
    <div class="form-sections">
      <section class="form-section">
        <h2>基本信息</h2>
        <div class="field-grid">
          <UFormField name="name" label="活动名称" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" :disabled="disabled" data-testid="activity-name" /></UFormField>
          <UFormField name="typeId" label="活动类型" required><USelect v-model="state.typeId" :items="types" placeholder="请选择" class="w-full" :disabled="disabled" data-testid="activity-type" /></UFormField>
          <UFormField name="communityId" label="所属社区"><USelect v-model="state.communityId" :items="communities" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="date" label="开展日期" required><UInput v-model="state.date" type="date" :max="todayLocal()" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="location" label="活动地点" class="field-full"><UInput v-model="state.location" maxlength="300" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="organizer" label="组织单位" class="field-full"><NameTagsInput v-model="state.organizer" :maxlength="200" class="w-full" :disabled="disabled" /></UFormField>
        </div>
      </section>
      <section class="form-section">
        <div class="field-grid">
          <UFormField name="content" label="开展情况" required class="field-full"><UTextarea v-model="state.content" :rows="5" :maxlength="12000" autoresize class="w-full" :disabled="disabled" data-testid="activity-content" /></UFormField>
        </div>
      </section>
      <section class="form-section"><h2>活动照片</h2><PhotoUploader v-model="state.photos" phase="activity" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" :max="20" @busy="uploadBusy = $event" @failed="uploadFailed = $event" /></section>
      <section class="form-section">
        <UButton type="button" label="更多信息" color="neutral" variant="ghost" :trailing-icon="moreOpen ? 'i-lucide-chevron-up' : 'i-lucide-chevron-down'" :aria-expanded="moreOpen" aria-controls="activity-more" @click="moreOpen = !moreOpen" />
        <div v-show="moreOpen" id="activity-more" class="field-grid mt-3">
          <UFormField name="endDate" label="结束日期"><UInput v-model="state.endDate" type="date" :min="state.date" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="participants" label="参加单位及人员" class="field-full"><NameTagsInput v-model="state.participants" :maxlength="2000" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="participantCount" label="参加人数"><UInput v-model="state.participantCount" type="number" min="0" step="1" inputmode="numeric" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="outcome" label="活动成效" class="field-full"><UTextarea v-model="state.outcome" :rows="3" :maxlength="12000" autoresize class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="notes" label="备注" class="field-full"><UTextarea v-model="state.notes" :rows="2" :maxlength="8000" autoresize class="w-full" :disabled="disabled" /></UFormField>
        </div>
      </section>
    </div>
    <p v-if="draftSaved && !initial" class="draft-state" role="status">草稿已保存到此浏览器</p>
    <div class="form-actions"><UButton type="button" :to="initial ? `/activities/${initial.activity.id}` : '/activities'" color="neutral" variant="outline" :disabled="saving">取消</UButton><UButton type="submit" :loading="saving" :disabled="disabled || uploadBusy || uploadFailed" data-testid="activity-save">保存活动</UButton></div>
  </UForm>
  <UModal v-model:open="leaveOpen" :title="uploadBusy ? '照片尚未上传完成' : '放弃未保存的修改？'" :description="uploadBusy ? '离开后，未上传的照片需重新选择' : '离开后，本次修改不会保存'" :dismissible="false" :close="false">
    <template #footer><UButton label="继续编辑" color="neutral" variant="outline" @click="finishLeave(false)" /><UButton label="离开" color="error" @click="finishLeave(true)" /></template>
  </UModal>
</template>
