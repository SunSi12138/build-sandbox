<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { CampaignDetailDto, CampaignWriteRequest, PhotoUploadDto, PointDto, NewPointDraft } from '~/types/api'
import { errorMessage, todayLocal, parseCoordinates, queryText } from '~/utils/format'
const props = defineProps<{ initial?: CampaignDetailDto | null }>()
const emit = defineEmits<{ saved: [detail: CampaignDetailDto] }>()
const { bootstrap, load } = useAppData()
const toast = useToast()
const route = useRoute()
const router = useRouter()
const draftKey = 'street-eco-campaign-draft-v3'
const selectedPoint = ref<PointDto | null>(null)
const pointLoading = ref(false)
const conflictOpen = ref(false)
const requestedPoint = ref('')
function emptyPoint(): NewPointDraft { return { name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', coordinates: '', extraValues: {}, photoIds: [] } }
interface CampaignState {
  extraValues: Record<string,string|number>
  name: string
  typeId: string
  pointId: string
  pointMode: 'regular' | 'temporary'
  newPoint: NewPointDraft
  date: string
  content: string
  outcome: string
  notes: string
  beforePhotos: PhotoUploadDto[]
  processPhotos: PhotoUploadDto[]
  afterPhotos: PhotoUploadDto[]
}
const state = reactive<CampaignState>({
  extraValues: {},
  name: '',
  typeId: '',
  pointId: '',
  pointMode: 'regular',
  newPoint: emptyPoint(),
  date: todayLocal(),
  content: '',
  outcome: '',
  notes: '',
  beforePhotos: [],
  processPhotos: [],
  afterPhotos: [],
})
const saving = ref(false),
  loading = ref(true),
  error = ref(''),
  savedAt = ref(''),
  storageFailed = ref(false),
  leaveOpen = ref(false)
const phaseBusy = reactive({ before: false, process: false, after: false, extra: false })
const phaseFailed = reactive({ before: false, process: false, after: false, extra: false })
const photosFailed = computed(() => Object.values(phaseFailed).some(Boolean))
const formDisabled = computed(() => loading.value || saving.value)
const photosBusy = computed(() => Object.values(phaseBusy).some(Boolean))
const communities = computed(() =>
  (bootstrap.value?.communities ?? []).map((item) => ({ label: item.name, value: item.id })),
)
const types = computed(() =>
  (bootstrap.value?.campaignTypes ?? []).map((item) => ({ label: item.name, value: item.id })),
)
const extraFields = computed(() => bootstrap.value?.campaignTypes?.find(x=>x.id===state.typeId)?.fields ?? [])
const extraPhotoIds = computed<string[]>({
  get: () => state.beforePhotos.map(x => x.id),
  set: ids => { state.beforePhotos = ids.map(id => state.beforePhotos.find(x=>x.id===id)??{ id, width: 1, height: 1 }) },
})
let ready = false,
  completed = false,
  original = '',
  leaveResolve: ((value: boolean) => void) | undefined
const textFields = [
  'name',
  'typeId',
  'pointId',
  'date',
  'content',
  'outcome',
  'notes',
] as const
const photoFields = ['beforePhotos', 'processPhotos', 'afterPhotos'] as const
function restoreDraft() {
  try {
    const raw = localStorage.getItem(draftKey)
    if (!raw) return
    const saved = JSON.parse(raw) as {
      version?: number
      state?: Record<string, unknown>
      savedAt?: string
    }
    if (saved.version !== 3 || !saved.state) {
      localStorage.removeItem(draftKey)
      return
    }
    state.pointMode = saved.state.pointMode === 'temporary' ? 'temporary' : 'regular'
    const point = saved.state.newPoint
    if (point && typeof point === 'object') {
      const values = point as Record<string, unknown>
      for (const key of ['name','communityId','typeId','address','contact','phone','coordinates'] as const)
        if (typeof values[key] === 'string') state.newPoint[key] = values[key] as string
      state.newPoint.extraValues = typeof values.extraValues === 'object' && values.extraValues ? values.extraValues as Record<string,string|number> : {}
      state.newPoint.photoIds = Array.isArray(values.photoIds) ? values.photoIds.filter((x): x is string => typeof x === 'string') : []
    }
    for (const field of textFields)
      if (typeof saved.state[field] === 'string') state[field] = saved.state[field] as string
    if (saved.state.extraValues && typeof saved.state.extraValues === 'object')
      state.extraValues = saved.state.extraValues as Record<string,string|number>
    for (const field of photoFields) {
      const values = saved.state[field]
      if (Array.isArray(values))
        state[field] = values
          .filter((value): value is PhotoUploadDto =>
            Boolean(
              value &&
                typeof value.id === 'string' &&
                /^[0-9a-f]{32}$/.test(value.id) &&
                typeof value.width === 'number' &&
                typeof value.height === 'number',
            ),
          )
          .slice(0, 20)
    }
    if (typeof saved.savedAt === 'string') savedAt.value = saved.savedAt
  } catch {
    try {
      localStorage.removeItem(draftKey)
    } catch {
      storageFailed.value = true
    }
  }
}
function persist() {
  if (!ready || props.initial || completed) return
  const now = new Date()
  const time = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`
  try {
    localStorage.setItem(draftKey, JSON.stringify({ version: 3, state, savedAt: time }))
    savedAt.value = time
    storageFailed.value = false
  } catch {
    storageFailed.value = true
  }
}
watch(state, persist, { deep: true, flush: 'sync' })
onMounted(async () => {
  try {
    await load()
    if (props.initial) {
      const record = props.initial.campaign
      for (const field of textFields)
        state[field] = record[field] === null ? '' : String(record[field])
      state.extraValues = record.extraValues ?? {}
      state.beforePhotos = props.initial.photos
        .filter((photo) => photo.phase === 'before')
        .map((photo) => ({ id: photo.id, width: photo.width, height: photo.height }))
      state.processPhotos = props.initial.photos
        .filter((photo) => photo.phase === 'process')
        .map((photo) => ({ id: photo.id, width: photo.width, height: photo.height }))
      state.afterPhotos = props.initial.photos
        .filter((photo) => photo.phase === 'after')
        .map((photo) => ({ id: photo.id, width: photo.width, height: photo.height }))
    } else {
      state.date = bootstrap.value?.today ?? todayLocal()
      restoreDraft()
      const requested = queryText(route.query.pointId)
      if (requested && (state.pointMode === 'temporary' || (state.pointId && state.pointId !== requested))) {
        requestedPoint.value = requested
        conflictOpen.value = true
      } else if (requested) state.pointId = requested
    }
    original = JSON.stringify(state)
    ready = true
  } catch (value) {
    error.value = errorMessage(value)
  } finally {
    loading.value = false
  }
  window.addEventListener('beforeunload', beforeUnload)
  document.addEventListener('visibilitychange', persist)
})
function validate(value: CampaignState): FormError[] {
  const errors: FormError[] = []
  for (const [field, label] of [
    ['name', '整治名称'],
    ['typeId', '整治类型'],
    ['date', '整治日期'],
    ['content', '整治内容'],
  ] as const)
    if (!value[field].trim())
      errors.push({
        name: field,
        message: `${field.endsWith('Id') || field === 'date' ? '请选择' : '请填写'}${label}`,
      })
  if (!props.initial) {
    if (value.pointMode === 'regular') {
      if (!value.pointId || !selectedPoint.value) errors.push({ name: 'pointId', message: '请选择场所' })
      else if (selectedPoint.value.disabled) errors.push({ name: 'pointId', message: '该场所已停用' })
    } else {
      for (const [key, label] of [['name', '名称'], ['communityId', '社区'], ['typeId', '类型'], ['address', '详细地址']] as const)
        if (!value.newPoint[key].trim()) errors.push({ name: `newPoint.${key}`, message: `请填写${label}` })
      const coordinates = parseCoordinates(value.newPoint.coordinates)
      if (coordinates.error) errors.push({ name: 'newPoint.coordinates', message: coordinates.error })
    }
  }
  return errors
}
async function save() {
  if (loading.value || saving.value || pointLoading.value || photosBusy.value || validate(state).length) return
  saving.value = true
  error.value = ''
  const body: CampaignWriteRequest = {
    name: state.name,
    extraValues: state.extraValues,
    typeId: state.typeId,
    pointId: props.initial ? props.initial.campaign.pointId : state.pointMode === 'regular' ? state.pointId : null,
    newPoint: !props.initial && state.pointMode === 'temporary' ? {
      name: state.newPoint.name, communityId: state.newPoint.communityId, typeId: state.newPoint.typeId,
      address: state.newPoint.address, contact: state.newPoint.contact, phone: state.newPoint.phone,
      extraValues: state.newPoint.extraValues, photoIds: state.newPoint.photoIds,
      ...parseCoordinates(state.newPoint.coordinates), frequency: 'irregular', disabled: false, temporary: true,
    } : null,
    date: state.date,
    content: state.content,
    outcome: state.outcome,
    notes: state.notes,
    beforePhotoIds: state.beforePhotos.map((photo) => photo.id),
    processPhotoIds: state.processPhotos.map((photo) => photo.id),
    afterPhotoIds: state.afterPhotos.map((photo) => photo.id),
  }
  try {
    const detail = await $fetch<CampaignDetailDto>(
      props.initial ? `/api/campaigns/${props.initial.campaign.id}` : '/api/campaigns',
      { method: props.initial ? 'PUT' : 'POST', body, retry: 0 },
    )
    completed = true
    if (!props.initial) {
      try {
        localStorage.removeItem(draftKey)
      } catch {
        /* The saved server record is authoritative. */
      }
    }
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
    emit('saved', detail)
  } catch (value) {
    error.value = errorMessage(value)
  } finally {
    saving.value = false
  }
}
function beforeUnload(event: BeforeUnloadEvent) {
  persist()
  if (
    !completed &&
    (saving.value ||
      photosBusy.value ||
      Boolean(props.initial && JSON.stringify(state) !== original))
  ) {
    event.preventDefault()
    event.returnValue = ''
  }
}
function finishLeave(leave: boolean) {
  leaveOpen.value = false
  leaveResolve?.(leave)
  leaveResolve = undefined
}
onBeforeRouteLeave(() => {
  persist()
  if (saving.value) return false
  if (completed || (!photosBusy.value && (!props.initial || JSON.stringify(state) === original)))
    return true
  leaveOpen.value = true
  return new Promise<boolean>((resolve) => {
    leaveResolve = resolve
  })
})
onBeforeUnmount(() => {
  persist()
  window.removeEventListener('beforeunload', beforeUnload)
  document.removeEventListener('visibilitychange', persist)
  leaveResolve?.(false)
})
function keepDraft() {
  conflictOpen.value = false
  void router.replace({ query: { ...route.query, pointId: undefined } })
}
function useRequestedPoint() {
  state.pointMode = 'regular'
  state.pointId = requestedPoint.value
  state.newPoint = emptyPoint()
  conflictOpen.value = false
}
</script>
<template>
  <UAlert v-if="error" :description="error" color="error" variant="subtle" role="alert" />
  <p v-if="!loading && (!communities.length || !types.length)" class="text-sm text-warning">
    请先维护社区和整治类型
  </p>
  <UForm :state="state" :validate="validate" class="form-sections" @submit="save">
    <RecordPointPicker v-if="!initial" v-model:mode="state.pointMode" v-model:point-id="state.pointId" v-model:new-point="state.newPoint" :disabled="formDisabled" @selected="selectedPoint = $event" @busy="pointLoading = $event" />
    <section v-else class="form-section"><h2>场所</h2><NuxtLink :to="`/points/${initial.campaign.pointId}`" class="text-sm text-primary">{{ initial.campaign.pointName }}</NuxtLink><p class="text-xs text-muted">{{ initial.campaign.communityName }} · {{ initial.campaign.location }}</p></section>
    <section class="form-section">
      <h2>基本信息</h2>
      <div class="field-grid">
        <UFormField label="整治名称" name="name" required class="field-full"
          ><UInput
            v-model="state.name"
            maxlength="200"
            class="w-full"
            data-testid="campaign-name"
            :disabled="formDisabled"
        /></UFormField>
        <UFormField label="整治类型" name="typeId" required
          ><USelect
            v-model="state.typeId"
            :items="types"
            placeholder="请选择"
            class="w-full"
            data-testid="campaign-type"
            :disabled="formDisabled"
        /></UFormField>
        <UFormField label="整治日期" name="date" required
          ><UInput
            v-model="state.date"
            type="date"
            class="w-full"
            data-testid="campaign-date"
            :disabled="formDisabled"
        /></UFormField>
      </div>
    </section>
    <section v-if="extraFields.length" class="form-section"><ExtraFieldValues v-model="state.extraValues" v-model:photo-ids="extraPhotoIds" :fields="extraFields" phase="before" :quality="bootstrap?.settings.jpegQuality" :disabled="formDisabled" @busy="phaseBusy.extra=$event" @failed="phaseFailed.extra=$event" /></section>
    <section class="form-section">
      <h2>整治情况</h2>
      <div class="field-grid">
        <UFormField label="整治内容" name="content" required class="field-full"
          ><UTextarea
            v-model="state.content"
            :rows="4"
            maxlength="12000"
            class="w-full"
            data-testid="campaign-content"
            :disabled="formDisabled"
        /></UFormField>
        <UFormField label="整治成效" name="outcome" class="field-full"
          ><UTextarea
            v-model="state.outcome"
            :rows="3"
            maxlength="12000"
            class="w-full"
            :disabled="formDisabled"
        /></UFormField>
        <UFormField label="备注" name="notes" class="field-full"
          ><UTextarea
            v-model="state.notes"
            :rows="2"
            maxlength="8000"
            class="w-full"
            :disabled="formDisabled"
        /></UFormField>
      </div>
    </section>
    <section class="form-section" data-testid="campaign-before">
      <h2>整治前照片</h2>
      <PhotoUploader
        v-model="state.beforePhotos"
        phase="before"
        label="整治前"
        :max="20"
        :quality="bootstrap?.settings.jpegQuality"
        :disabled="saving || loading"
        @busy="phaseBusy.before = $event"
        @failed="phaseFailed.before = $event"
      />
    </section>
    <section class="form-section" data-testid="campaign-process">
      <h2>整治中照片</h2>
      <PhotoUploader
        v-model="state.processPhotos"
        phase="process"
        label="整治中"
        :max="20"
        :quality="bootstrap?.settings.jpegQuality"
        :disabled="saving || loading"
        @busy="phaseBusy.process = $event"
        @failed="phaseFailed.process = $event"
      />
    </section>
    <section class="form-section" data-testid="campaign-after">
      <h2>整治后照片</h2>
      <PhotoUploader
        v-model="state.afterPhotos"
        phase="after"
        label="整治后"
        :max="20"
        :quality="bootstrap?.settings.jpegQuality"
        :disabled="saving || loading"
        @busy="phaseBusy.after = $event"
        @failed="phaseFailed.after = $event"
      />
    </section>
    <div class="form-actions">
      <span class="draft-state" aria-live="polite">{{
        photosFailed
          ? '有照片未完成，请重试或移除'
          : photosBusy
            ? '照片处理中'
            : props.initial
              ? '编辑整治'
              : storageFailed
                ? '草稿未保存'
                : savedAt
                  ? `草稿已保存 ${savedAt}`
                  : '草稿'
      }}</span
      ><UButton
        type="submit"
        label="保存"
        :loading="saving"
        :disabled="loading || pointLoading || photosBusy || conflictOpen || !communities.length || !types.length"
        data-testid="campaign-save"
      />
    </div>
  </UForm>
  <UModal v-model:open="conflictOpen" title="草稿场所不同" description="是否将草稿改为当前场所？" :dismissible="false" :close="false"><template #footer><UButton label="继续草稿" color="neutral" variant="outline" @click="keepDraft" /><UButton label="使用当前场所" @click="useRequestedPoint" /></template></UModal>
  <UModal
    v-model:open="leaveOpen"
    title="离开页面？"
    :description="photosBusy ? '离开后将中断照片上传' : '未保存的更改将丢失'"
    :dismissible="false"
    :close="false"
    ><template #footer
      ><UButton
        label="取消"
        color="neutral"
        variant="outline"
        @click="finishLeave(false)" /><UButton
        label="离开"
        color="error"
        @click="finishLeave(true)" /></template
  ></UModal>
</template>
