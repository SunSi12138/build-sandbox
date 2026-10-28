<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { ActivityDetailDto, ActivityWriteRequest, PhotoUploadDto, NewPointDraft, PointDto } from '~/types/api'
import { errorMessage, todayLocal, queryText, parseCoordinates } from '~/utils/format'

const props = defineProps<{ initial?: ActivityDetailDto | null }>()
const emit = defineEmits<{ saved: [detail: ActivityDetailDto] }>()
const route = useRoute()
const { bootstrap, load } = useAppData()
const toast = useToast()
const draftKey = 'street-eco.activity-draft.v2'
interface State {
  name: string; typeId: string; communityId: string; location: string; date: string; endDate: string
  organizer: string; participants: string; participantCount: string; content: string; outcome: string; notes: string
  photos: PhotoUploadDto[]; beforePhotos: PhotoUploadDto[]; processPhotos: PhotoUploadDto[]; afterPhotos: PhotoUploadDto[]
  pointMode: 'regular' | 'temporary'; pointId: string; newPoint: NewPointDraft; remediationTypeId: string
  vehicleCount: string; garbageTons: string; areaSqm: string; tagIds: string[]; matterIds: string[]; issueIds: string[]
}
function emptyPoint(): NewPointDraft { return { name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', coordinates: '' } }
function empty(): State { return { name: '', typeId: '', communityId: 'all', location: '', date: todayLocal(), endDate: '',
  organizer: '', participants: '', participantCount: '', content: '', outcome: '', notes: '', photos: [], beforePhotos: [], processPhotos: [], afterPhotos: [], pointMode: 'regular', pointId: '', newPoint: emptyPoint(), remediationTypeId: '', vehicleCount: '', garbageTons: '', areaSqm: '', tagIds: [], matterIds: [], issueIds: [] } }
const state = reactive<State>(empty())
const initializing = ref(true)
const saving = ref(false)
const photoBusy = reactive({ general: false, before: false, process: false, after: false })
const photoFailed = reactive({ general: false, before: false, process: false, after: false })
const uploadBusy = computed(() => Object.values(photoBusy).some(Boolean))
const uploadFailed = computed(() => Object.values(photoFailed).some(Boolean))
const pointBusy = ref(false), selectedPoint = ref<PointDto | null>(null)
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
    localStorage.setItem(draftKey, JSON.stringify({ version: 2, data: state }))
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
const remediation = computed(() => (bootstrap.value?.activityTypes.find(item => item.id === state.typeId)?.template ?? props.initial?.activity.template) === 'remediation')
const types = computed(() => (bootstrap.value?.activityTypes ?? []).map(item => ({ label: item.name, value: item.id, disabled: !!props.initial && item.template !== props.initial.activity.template })))
const remediationTypes = computed(() => (bootstrap.value?.remediationTypes ?? []).map(item => ({ label: item.name, value: item.id })))
const typeChangeOpen = ref(false), pendingType = ref(''), conflictOpen = ref(false)
function chooseType(value: string) {
  const next = bootstrap.value?.activityTypes.find(item => item.id === value)?.template === 'remediation'
  if (!props.initial && next !== remediation.value && (state.photos.length + state.beforePhotos.length + state.processPhotos.length + state.afterPhotos.length > 0 || state.vehicleCount || state.garbageTons || state.areaSqm)) { pendingType.value = value; typeChangeOpen.value = true; return }
  state.typeId = value
}
function confirmType() { state.photos = []; state.beforePhotos = []; state.processPhotos = []; state.afterPhotos = []; state.vehicleCount = ''; state.garbageTons = ''; state.areaSqm = ''; state.typeId = pendingType.value; if (!remediation.value && state.pointMode === 'temporary') { state.pointMode = 'regular'; state.pointId = ''; state.newPoint = emptyPoint() }; typeChangeOpen.value = false }
function useRequestedContext() {
  Object.assign(state, empty()); state.organizer = bootstrap.value?.settings.issuingUnit ?? ''
  state.pointId = queryText(route.query.pointId)
  const matter = queryText(route.query.matterId); if (matter) state.matterIds = [matter]
  if (queryText(route.query.template) === 'remediation') state.typeId = bootstrap.value?.activityTypes.find(item => item.template === 'remediation')?.id ?? ''
  conflictOpen.value = false
}

const disabled = computed(() => initializing.value || saving.value || conflictOpen.value)
function restoreDraft() {
  try {
    const raw = localStorage.getItem(draftKey)
    if (!raw) return
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object' || !('version' in parsed) || parsed.version !== 2 || !('data' in parsed) || !parsed.data || typeof parsed.data !== 'object') return
    const data = parsed.data as Record<string, unknown>
    const base = empty()
    for (const key of Object.keys(base) as (keyof State)[]) if (typeof base[key] === 'string' && typeof data[key] === 'string') (state as unknown as Record<string, unknown>)[key] = data[key]
    for (const key of ['participantCount', 'vehicleCount', 'garbageTons', 'areaSqm'] as const) if (typeof data[key] === 'number' && Number.isFinite(data[key])) state[key] = String(data[key])
    state.pointMode = data.pointMode === 'temporary' ? 'temporary' : 'regular'
    for (const key of ['photos', 'beforePhotos', 'processPhotos', 'afterPhotos'] as const) if (Array.isArray(data[key])) state[key] = (data[key] as unknown[]).filter((value): value is PhotoUploadDto => !!value && typeof value === 'object' && 'id' in value && typeof value.id === 'string' && /^[0-9a-f]{32}$/.test(value.id) && 'width' in value && typeof value.width === 'number' && value.width > 0 && 'height' in value && typeof value.height === 'number' && value.height > 0).slice(0, key === 'photos' ? 20 : 100)
    for (const key of ['tagIds', 'matterIds', 'issueIds'] as const) if (Array.isArray(data[key])) state[key] = (data[key] as unknown[]).filter((id): id is string => typeof id === 'string' && /^[0-9a-f]{32}$/.test(id)).slice(0, key === 'issueIds' ? 50 : 20)
    if (data.newPoint && typeof data.newPoint === 'object') for (const key of Object.keys(emptyPoint()) as (keyof NewPointDraft)[]) if (typeof (data.newPoint as Record<string, unknown>)[key] === 'string') state.newPoint[key] = (data.newPoint as Record<string, string>)[key]!
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
    photos: initial.photos.filter(photo => photo.phase === 'activity').map(photo => ({ id: photo.id, width: photo.width, height: photo.height })),
    beforePhotos: initial.photos.filter(photo => photo.phase === 'before'), processPhotos: initial.photos.filter(photo => photo.phase === 'process'), afterPhotos: initial.photos.filter(photo => photo.phase === 'after'),
    pointId: record.pointId ?? '', pointMode: 'regular', remediationTypeId: record.remediation?.typeId ?? '',
    vehicleCount: record.remediation?.vehicleCount == null ? '' : String(record.remediation.vehicleCount),
    garbageTons: record.remediation?.garbageTons == null ? '' : String(record.remediation.garbageTons),
    areaSqm: record.remediation?.areaSqm == null ? '' : String(record.remediation.areaSqm),
    tagIds: record.tags.map(tag => tag.id), matterIds: record.matters.map(matter => matter.id), issueIds: record.relatedIssues.map(issue => issue.id)
  })
}, { immediate: true })
async function initialize() {
  initializing.value = true
  try {
    await load()
    if (!props.initial) {
      useRequestedContext()
      const requestedPoint = state.pointId, requestedMatter = state.matterIds[0], requestedType = state.typeId
      restoreDraft()
      if ((state.name || state.content || state.photos.length || state.beforePhotos.length) && ((requestedPoint && state.pointId !== requestedPoint) || (requestedMatter && !state.matterIds.includes(requestedMatter)) || (requestedType && state.typeId !== requestedType))) conflictOpen.value = true
      if (!state.communityId) state.communityId = 'all'
    }
    original = JSON.stringify(state)
    moreOpen.value = Boolean(state.endDate || state.participants || state.participantCount || state.outcome || state.notes || state.tagIds.length || state.matterIds.length || state.issueIds.length)
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
  if (remediation.value) {
    if (!data.remediationTypeId) issues.push({ name: 'remediationTypeId', message: '请选择整治类别' })
    if (data.pointMode === 'regular' && !data.pointId) issues.push({ name: 'pointId', message: '请选择点位' })
    if (data.pointMode === 'temporary') {
      for (const [key, label] of [['name', '名称'], ['communityId', '社区'], ['typeId', '类型'], ['address', '地址']] as const) if (!data.newPoint[key].trim()) issues.push({ name: `newPoint.${key}`, message: `请填写${label}` })
      const coordinates = parseCoordinates(data.newPoint.coordinates); if (coordinates.error) issues.push({ name: 'newPoint.coordinates', message: coordinates.error })
    }
    for (const key of ['vehicleCount', 'garbageTons', 'areaSqm'] as const) if (data[key] && (!Number.isFinite(Number(data[key])) || Number(data[key]) < 0 || Number(data[key]) > (key === 'vehicleCount' ? 100000000 : 1000000000000) || (key === 'vehicleCount' && !/^\d+$/.test(data[key])))) issues.push({ name: key, message: '请填写有效的非负数量' })
  }
  if (!data.content.trim()) issues.push({ name: 'content', message: '请填写开展情况' })
  if (data.participantCount && (!/^\d+$/.test(data.participantCount) || Number(data.participantCount) > 100000000))
    issues.push({ name: 'participantCount', message: '请填写有效的非负整数' })
  if (issues.some(issue => issue.name === 'endDate' || issue.name === 'participantCount')) moreOpen.value = true
  return issues
}
async function save() {
  if (saving.value || initializing.value || pointBusy.value || conflictOpen.value) return
  if (uploadBusy.value || uploadFailed.value) { error.value = '请完成或移除未成功上传的照片'; return }
  if (validate(state).length) { error.value = '请检查必填项'; return }
  saving.value = true
  error.value = ''
  const body: ActivityWriteRequest = {
    name: state.name.trim(), typeId: state.typeId, date: state.date, endDate: state.endDate || null,
    communityId: state.communityId === 'all' ? null : state.communityId, location: state.location, organizer: state.organizer,
    participants: state.participants, participantCount: state.participantCount === '' ? null : Number(state.participantCount),
    content: state.content.trim(), outcome: state.outcome, notes: state.notes, photoIds: remediation.value ? [] : state.photos.map(photo => photo.id),
    pointId: remediation.value && state.pointMode === 'temporary' ? null : state.pointId || null,
    newPoint: remediation.value && state.pointMode === 'temporary' ? { ...state.newPoint, ...parseCoordinates(state.newPoint.coordinates), frequency: 'irregular', disabled: false } : null,
    remediation: remediation.value ? { typeId: state.remediationTypeId, vehicleCount: state.vehicleCount === '' ? null : Number(state.vehicleCount), garbageTons: state.garbageTons === '' ? null : Number(state.garbageTons), areaSqm: state.areaSqm === '' ? null : Number(state.areaSqm), beforePhotoIds: state.beforePhotos.map(photo => photo.id), processPhotoIds: state.processPhotos.map(photo => photo.id), afterPhotoIds: state.afterPhotos.map(photo => photo.id) } : null,
    tagIds: state.tagIds, matterIds: state.matterIds, issueIds: state.issueIds
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
    <UAlert role="alert" v-if="error" :description="error" color="error" variant="subtle" />
    <div class="form-sections">
      <section class="form-section">
        <h2>基本信息</h2>
        <div class="field-grid">
          <UFormField name="name" label="活动名称" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" :disabled="disabled" data-testid="activity-name" /></UFormField>
          <UFormField name="typeId" label="活动类型" required><USelect :model-value="state.typeId" @update:model-value="chooseType" :items="types" placeholder="请选择" class="w-full" :disabled="disabled" data-testid="activity-type" /></UFormField>
          <UFormField v-if="!remediation" name="communityId" label="所属社区"><USelect v-model="state.communityId" :items="communities" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="date" label="开展日期" required><UInput v-model="state.date" type="date" :max="todayLocal()" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField v-if="!remediation" name="location" label="活动地点" class="field-full"><UInput v-model="state.location" maxlength="300" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="organizer" label="组织单位" class="field-full"><NameTagsInput v-model="state.organizer" :maxlength="200" class="w-full" :disabled="disabled" /></UFormField>
        </div>
      </section>
      <template v-if="remediation">
        <section v-if="initial" class="form-section"><h2>关联点位</h2><NuxtLink :to="`/points/${initial.activity.pointId}`" class="text-primary min-h-10 inline-flex items-center">{{ initial.activity.pointName }}</NuxtLink></section>
        <RecordPointPicker v-else v-model:mode="state.pointMode" v-model:point-id="state.pointId" v-model:new-point="state.newPoint" prefix="activity" :disabled="disabled" @selected="selectedPoint = $event" @busy="pointBusy = $event" />
        <section class="form-section"><div class="field-grid"><UFormField label="整治类别" name="remediationTypeId" required class="field-full"><USelect v-model="state.remediationTypeId" :items="remediationTypes" placeholder="请选择" class="w-full" :disabled="disabled" data-testid="activity-remediation-type" /></UFormField>
          <UFormField label="出动车辆（辆）" name="vehicleCount"><UInput :model-value="state.vehicleCount" @update:model-value="state.vehicleCount = String($event ?? '')" type="number" min="0" step="1" class="w-full" :disabled="disabled" data-testid="activity-vehicle-count" /></UFormField>
          <UFormField label="清运垃圾（吨）" name="garbageTons"><UInput :model-value="state.garbageTons" @update:model-value="state.garbageTons = String($event ?? '')" type="number" min="0" step="0.01" class="w-full" :disabled="disabled" data-testid="activity-garbage-tons" /></UFormField>
          <UFormField label="清理面积（㎡）" name="areaSqm"><UInput :model-value="state.areaSqm" @update:model-value="state.areaSqm = String($event ?? '')" type="number" min="0" step="0.01" class="w-full" :disabled="disabled" data-testid="activity-area-sqm" /></UFormField>
        </div></section>
      </template>
      <section class="form-section">
        <div class="field-grid">
          <UFormField name="content" label="开展情况" required class="field-full"><UTextarea v-model="state.content" :rows="5" :maxlength="12000" autoresize class="w-full" :disabled="disabled" data-testid="activity-content" /></UFormField>
        </div>
      </section>
      <section v-if="!remediation" class="form-section"><h2>活动照片</h2><PhotoUploader :defer-delete-ids="initial?.photos.map(photo => photo.id)" v-model="state.photos" phase="activity" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" :max="20" @busy="photoBusy.general = $event" @failed="photoFailed.general = $event" /></section>
      <section v-else class="form-section"><h2>整治照片</h2><div class="space-y-4">
        <div data-testid="activity-before-photos"><h3 class="mb-2 text-sm">整治前</h3><PhotoUploader :defer-delete-ids="initial?.photos.map(photo => photo.id)" v-model="state.beforePhotos" phase="before" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" @busy="photoBusy.before = $event" @failed="photoFailed.before = $event" /></div>
        <div data-testid="activity-process-photos"><h3 class="mb-2 text-sm">整治中</h3><PhotoUploader :defer-delete-ids="initial?.photos.map(photo => photo.id)" v-model="state.processPhotos" phase="process" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" @busy="photoBusy.process = $event" @failed="photoFailed.process = $event" /></div>
        <div data-testid="activity-after-photos"><h3 class="mb-2 text-sm">整治后</h3><PhotoUploader :defer-delete-ids="initial?.photos.map(photo => photo.id)" v-model="state.afterPhotos" phase="after" :quality="bootstrap?.settings.jpegQuality" :disabled="disabled" @busy="photoBusy.after = $event" @failed="photoFailed.after = $event" /></div>
      </div></section>
      <section class="form-section">
        <UButton type="button" label="更多信息" color="neutral" variant="ghost" :trailing-icon="moreOpen ? 'i-lucide-chevron-up' : 'i-lucide-chevron-down'" :aria-expanded="moreOpen" aria-controls="activity-more" @click="moreOpen = !moreOpen" />
        <div v-show="moreOpen" id="activity-more" class="field-grid mt-3">
          <UFormField v-if="!remediation" label="关联点位" class="field-full"><PointSelect v-model="state.pointId" active-only :disabled="disabled" /></UFormField>
          <UFormField label="标签" class="field-full"><TagPicker v-model="state.tagIds" :disabled="disabled" /></UFormField>
          <UFormField label="关联事项" class="field-full"><MatterPicker v-model="state.matterIds" :selected="initial?.activity.matters" :disabled="disabled" /></UFormField>
          <UFormField label="关联整改问题" class="field-full"><IssuePicker v-model="state.issueIds" :point-id="state.pointId" :selected="initial?.activity.relatedIssues" :disabled="disabled" /></UFormField>
          <UFormField name="endDate" label="结束日期"><UInput v-model="state.endDate" type="date" :min="state.date" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="participants" label="参加单位及人员" class="field-full"><NameTagsInput v-model="state.participants" :maxlength="2000" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="participantCount" label="参加人数"><UInput :model-value="state.participantCount" @update:model-value="state.participantCount = String($event ?? '')" type="number" min="0" step="1" inputmode="numeric" class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="outcome" label="活动成效" class="field-full"><UTextarea v-model="state.outcome" :rows="3" :maxlength="12000" autoresize class="w-full" :disabled="disabled" /></UFormField>
          <UFormField name="notes" label="备注" class="field-full"><UTextarea v-model="state.notes" :rows="2" :maxlength="8000" autoresize class="w-full" :disabled="disabled" /></UFormField>
        </div>
      </section>
    </div>
    <p v-if="draftSaved && !initial" class="draft-state" role="status">草稿已保存到此浏览器</p>
    <div class="form-actions"><UButton type="button" :to="initial ? `/activities/${initial.activity.id}` : '/activities'" color="neutral" variant="outline" :disabled="saving">取消</UButton><UButton type="submit" :loading="saving" :disabled="disabled || uploadBusy || uploadFailed || pointBusy" data-testid="activity-save">保存活动</UButton></div>
  </UForm>
  <UModal v-model:open="leaveOpen" :title="uploadBusy ? '照片尚未上传完成' : '放弃未保存的修改？'" :description="uploadBusy ? '离开后，未上传的照片需重新选择' : '离开后，本次修改不会保存'" :dismissible="false" :close="false">
    <template #footer><UButton label="继续编辑" color="neutral" variant="outline" @click="finishLeave(false)" /><UButton label="离开" color="error" @click="finishLeave(true)" /></template>
  </UModal>
  <UModal v-model:open="typeChangeOpen" title="更换填报模板？" description="当前模板的专用字段和照片选择将清空，公共信息保留" :dismissible="false" :close="false"><template #footer><UButton label="取消" color="neutral" variant="outline" @click="typeChangeOpen = false" /><UButton label="更换模板" color="error" @click="confirmType" /></template></UModal>
  <UModal v-model:open="conflictOpen" title="已有其他工作草稿" description="可以继续原草稿，或按当前事项、点位新建" :dismissible="false" :close="false"><template #footer><UButton label="继续草稿" color="neutral" variant="outline" @click="conflictOpen = false" /><UButton label="清空并新建" color="error" @click="useRequestedContext" /></template></UModal>
</template>
