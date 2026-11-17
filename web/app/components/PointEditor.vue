<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { PointDetailDto, PointDto, PointWriteRequest, PhotoUploadDto, Frequency, GeoShape } from '~/types/api'
import { coordinatesText, errorMessage, frequencyOptions, parseCoordinates } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ point?: PointDto | null }>()
const emit = defineEmits<{ saved: [point: PointDto] }>()
const { bootstrap, load } = useAppData()
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
const saving = ref(false), loading = ref(false), error = ref(''), photosBusy = ref(false)
const originalPhotoIds = ref<string[]>([])
interface PointState { shape: GeoShape | null; name: string; communityId: string; typeId: string; address: string; coordinates: string; contact: string; phone: string; frequency: Frequency; temporary: boolean; notes: string; disabled: boolean; photos: PhotoUploadDto[]; tagIds: string[] }
const defaults = (): PointState => ({ shape: null, name: '', communityId: '', typeId: '', address: '', coordinates: '', contact: '', phone: '', frequency: 'irregular', temporary: false, notes: '', disabled: false, photos: [], tagIds: [] })
const state = reactive<PointState>(defaults())
const pointNature = computed({ get: () => state.temporary ? 'temporary' : 'regular', set: (value: string) => { state.temporary = value === 'temporary' } })
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id })))
const detailRequest = useLatestRequest()
const draftRecovery = useDraftRecovery('street-eco.point-draft.v1', 1, 'data')
const draftRestored = ref(false)
let savedNew = false
function hasUsefulDraft() {
  return Boolean(state.name || state.communityId || state.typeId || state.address || state.coordinates ||
    state.shape || state.contact || state.phone || state.notes || state.photos.length || state.tagIds.length)
}
function restorePointDraft() {
  try {
    const data = draftRecovery.read<Partial<PointState>>()
    if (!data) return
    const restored = defaults()
    for (const key of ['name', 'communityId', 'typeId', 'address', 'coordinates', 'contact', 'phone', 'notes'] as const)
      if (typeof data[key] === 'string') restored[key] = data[key]
    if (data.frequency && frequencyOptions.some(option => option.value === data.frequency)) restored.frequency = data.frequency
    if (typeof data.temporary === 'boolean') restored.temporary = data.temporary
    if (data.shape && (data.shape.type === 'Point' || data.shape.type === 'Polygon') &&
      Array.isArray(data.shape.coordinates)) restored.shape = data.shape
    if (Array.isArray(data.photos)) restored.photos = data.photos.filter(photo =>
      photo && typeof photo.id === 'string' && /^[0-9a-f]{32}$/.test(photo.id) &&
      Number.isInteger(photo.width) && photo.width > 0 && Number.isInteger(photo.height) && photo.height > 0).slice(0, 9)
    if (Array.isArray(data.tagIds)) restored.tagIds = data.tagIds.filter(id => typeof id === 'string').slice(0, 30)
    Object.assign(state, restored)
    draftRestored.value = true
  } catch { error.value = draftRecovery.error.value }
}
function clearPointDraft() {
  draftRecovery.clear()
  draftRestored.value = false
  Object.assign(state, defaults())
}
watch(state, () => {
  if (!open.value || props.point || loading.value || saving.value || savedNew) return
  if (hasUsefulDraft()) draftRecovery.schedule(() => state)
  else draftRecovery.clear()
}, { deep: true })
watch(open, async value => {
  overlayForm.value = value
  if (!value) { detailRequest.cancel(); if (!props.point && !savedNew && hasUsefulDraft()) draftRecovery.flush(() => state); return }
  savedNew = false; draftRestored.value = false; originalPhotoIds.value = []
  const request = detailRequest.start()
  error.value = ''; loading.value = true
  const point = props.point
  Object.assign(state, point ? { ...point, shape: point.shape ?? (point.longitude != null && point.latitude != null ? { type: "Point", coordinates: [[point.longitude, point.latitude]] } : null), coordinates: coordinatesText(point.longitude, point.latitude), photos: [], tagIds: point.tags.map(tag => tag.id) } : defaults())
  try {
    await load()
    if (!request.isCurrent()) return
    if (!point) restorePointDraft()
    if (point) {
      const result = await $fetch<PointDetailDto>(`/api/points/${point.id}`, { signal: request.signal })
      if (request.isCurrent()) { state.photos = result.photos?.map(photo => ({ id: photo.id, width: photo.width, height: photo.height })) ?? []; originalPhotoIds.value = state.photos.map(photo => photo.id) }
    }
  } catch (value) { if (request.isCurrent()) error.value = errorMessage(value) }
  finally { if (request.isCurrent()) loading.value = false }
})
function validate(value: PointState): FormError[] {
  const errors: FormError[] = []
  if (!value.name.trim()) errors.push({ name: 'name', message: '请填写点位名称' })
  if (!value.communityId) errors.push({ name: 'communityId', message: '请选择所属社区' })
  if (!value.typeId) errors.push({ name: 'typeId', message: '请选择点位类型' })
  if (!value.address.trim()) errors.push({ name: 'address', message: '请填写详细地址' })
  const coordinates = parseCoordinates(value.coordinates)
  if (coordinates.error) errors.push({ name: 'coordinates', message: coordinates.error })
  return errors
}
async function save() {
  if (loading.value || saving.value || photosBusy.value || validate(state).length) return
  saving.value = true; error.value = ''
  const { longitude, latitude } = parseCoordinates(state.coordinates)
  const body: PointWriteRequest = { shape: state.shape, name: state.name, communityId: state.communityId, typeId: state.typeId, address: state.address, longitude, latitude, contact: state.contact, phone: state.phone, frequency: state.temporary ? 'irregular' : state.frequency, notes: state.notes, disabled: state.disabled, temporary: state.temporary, photoIds: state.photos.map(photo => photo.id), tagIds: state.tagIds }
  try { const point = await $fetch<PointDto>(props.point ? `/api/points/${props.point.id}` : '/api/points', { method: props.point ? 'PUT' : 'POST', body, retry: 0 }); if (!props.point) { savedNew = true; draftRecovery.clear() } emit('saved', point); open.value = false }
  catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
onBeforeUnmount(() => { overlayForm.value = false })
</script>
<template>
  <USlideover v-model:open="open" :title="point ? '编辑点位' : '新增点位'" :dismissible="!saving && !photosBusy" :close="!saving && !photosBusy" :ui="{ content: 'w-full max-w-[480px] max-md:max-w-none' }">
    <template #body>
      <UAlert v-if="error" :description="error" color="error" variant="subtle" class="mb-3" />
      <div v-if="!point && (draftRestored || draftRecovery.savedAt.value)" class="mb-3 flex items-center justify-between text-xs text-muted"><span>{{ draftRestored ? "已恢复上次新建点位草稿" : "草稿已保存到此浏览器" }}</span><UButton type="button" label="清空草稿" color="neutral" variant="link" :disabled="saving || loading" @click="clearPointDraft" /></div>
      <p v-if="!loading && (!communities.length || !types.length)" class="mb-3 text-sm text-warning">请先维护社区和点位类型</p>
      <UForm id="point-editor-form" :state="state" :disabled="loading || saving" :validate="validate" class="field-grid" @submit="save">
        <UFormField label="名称" name="name" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" data-testid="point-name" /></UFormField>
        <UFormField label="社区" name="communityId" required><USelect v-model="state.communityId" :items="communities" placeholder="请选择" class="w-full" /></UFormField>
        <UFormField label="类型" name="typeId" required><USelect v-model="state.typeId" :items="types" placeholder="请选择" class="w-full" /></UFormField>
        <UFormField label="详细地址" name="address" required class="field-full"><UInput v-model="state.address" maxlength="1000" class="w-full" /></UFormField>
        <UFormField label="地图位置（GCJ-02）" class="field-full"><MapLocationField :model-value="state.shape" :disabled="loading || saving" @update:model-value="state.shape = $event; state.coordinates = $event ? coordinatesText($event.coordinates[0]?.[0] ?? null, $event.coordinates[0]?.[1] ?? null) : ''" /></UFormField>
        <details class="field-full text-sm text-muted"><summary class="cursor-pointer">手动填写经纬度</summary><UFormField name="coordinates" class="mt-2"><UInput v-model="state.coordinates" aria-label="经纬度" placeholder="88.893426,29.274331" class="w-full" @update:model-value="state.shape = null" /></UFormField><p class="mt-1 text-xs text-muted">请填写 GCJ-02 经纬度（高德坐标）；GPS/WGS84 坐标需先转换。</p></details>
        <UFormField label="联系人" name="contact"><NameTagsInput :disabled="loading || saving" v-model="state.contact" maxlength="100" class="w-full" /></UFormField>
        <UFormField label="电话" name="phone"><UInput v-model="state.phone" type="tel" maxlength="100" class="w-full" /></UFormField>
        <UFormField label="点位性质" name="temporary" class="field-full"><USelect v-model="pointNature" :items="[{ label: '临时', value: 'temporary' }, { label: '长期（常态）', value: 'regular' }]" class="w-full" data-testid="point-temporary" /></UFormField>
        <UFormField label="排查频次" name="frequency" class="field-full"><USelect v-model="state.frequency" :items="frequencyOptions" :disabled="state.temporary" class="w-full" /><p v-if="state.temporary" class="mt-1 text-xs text-muted">临时点位不参与常态排查提醒</p></UFormField>
        <UFormField label="标签" class="field-full"><TagPicker v-model="state.tagIds" :disabled="loading || saving" /></UFormField>
        <UFormField label="备注" name="notes" class="field-full"><UTextarea v-model="state.notes" :rows="3" maxlength="8000" class="w-full" /></UFormField>
        <UFormField label="现场图片" class="field-full"><PhotoUploader v-model="state.photos" :defer-delete-ids="point ? originalPhotoIds : undefined" phase="point" :max="9" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="photosBusy = $event" /></UFormField>
        <UFormField v-if="point" label="状态" name="disabled" class="field-full"><div class="check-row"><UCheckbox v-model="state.disabled" label="停用" /></div></UFormField>
      </UForm>
    </template>
    <template #footer><span v-if="photosBusy" class="mr-auto text-xs text-muted">照片上传中</span><UButton label="取消" color="neutral" variant="outline" :disabled="saving || photosBusy" @click="open = false" /><UButton type="submit" form="point-editor-form" label="保存" :loading="saving" :disabled="loading || photosBusy || !communities.length || !types.length" data-testid="point-save" /></template>
  </USlideover>
</template>
