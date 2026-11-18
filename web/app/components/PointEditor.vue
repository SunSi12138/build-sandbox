<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { GeoShape, MapState, UseData } from '~/types/community'
import type { PointDetailDto, PointDto, PointWriteRequest, PhotoUploadDto, Frequency } from '~/types/api'
import { coordinatesText, errorMessage, frequencyOptions, parseCoordinates } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ point?: PointDto | null; initialCoordinates?: string; initialShape?: GeoShape | null }>()
const emit = defineEmits<{ saved: [point: PointDto] }>()
const { bootstrap, load } = useAppData()
const overlayForm = useState<boolean>('street-eco-overlay-form', () => false)
const saving = ref(false), loading = ref(false), error = ref(''), photosBusy = ref(false), photosFailed = ref(false), extraPhotosBusy = ref(false), extraPhotosFailed = ref(false)
interface PointState { extraValues: Record<string,string|number>; color: string; name: string; communityId: string; typeId: string; address: string; coordinates: string; contact: string; phone: string; frequency: Frequency; notes: string; disabled: boolean; photos: PhotoUploadDto[] }
const defaults = (): PointState => ({ extraValues: {}, color: '#087f70', name: '', communityId: '', typeId: '', address: '', coordinates: '', contact: '', phone: '', frequency: 'irregular', notes: '', disabled: false, photos: [] })
const state = reactive<PointState>(defaults())
const mapShape = ref<GeoShape|null>(null), shapeRevision = ref(0), revision = ref<number | undefined>()
const inlineUses = ref<{ key: number; name: string; data: UseData; busy: boolean; failed: boolean }[]>([])
let nextUseKey = 0
const usesBusy = computed(() => inlineUses.value.some(x => x.busy))
const usesFailed = computed(() => inlineUses.value.some(x => x.failed))
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id })))
const extraFields = computed(() => (props.point && state.typeId === props.point.typeId
  ? props.point.fieldSnapshot : bootstrap.value?.pointTypes.find(t => t.id === state.typeId)?.fields) ?? [])
const extraPhotoIds = ref<string[]>([])
const initialSnapshot=ref('')
function snapshot(){return JSON.stringify({state,shape:mapShape.value,uses:inlineUses.value.map(x=>({name:x.name,data:x.data}))})}
function mayClose(){return !saving.value&&!photosBusy.value&&!extraPhotosBusy.value&&!usesBusy.value&&(loading.value||snapshot()===initialSnapshot.value||window.confirm('场所资料尚未保存，放弃修改吗？'))}
function changeOpen(value:boolean){if(value||mayClose())open.value=value}
onBeforeRouteLeave(()=>!open.value||mayClose())
let openingVersion = 0
watch([open, () => props.point?.id], async ([isOpen]) => {
  // Each opening starts from an isolated, non-null form model. An earlier
  // request must not replace the contents of a subsequently opened point.
  const version = ++openingVersion
  overlayForm.value = isOpen
  if (!isOpen) return
  error.value = ''; loading.value = true
  inlineUses.value = []; photosFailed.value = false; extraPhotoIds.value = []
  extraPhotosFailed.value = false; photosBusy.value = false; extraPhotosBusy.value = false
  const point = props.point
  revision.value = point?.revision
  mapShape.value = props.initialShape ?? null; shapeRevision.value = 0
  Object.assign(state, defaults(), point ? {
    name: point.name, communityId: point.communityId, typeId: point.typeId,
    address: point.address, contact: point.contact, phone: point.phone,
    frequency: point.frequency, notes: point.notes, disabled: point.disabled,
    color: point.color, coordinates: coordinatesText(point.longitude, point.latitude),
    extraValues: { ...(point.extraValues ?? {}) }, photos: [],
  } : { coordinates: props.initialCoordinates ?? '' })
  try {
    await load()
    if (version !== openingVersion) return
    if (!point) state.typeId = bootstrap.value?.pointTypes.find(x=>x.name==='永久建筑')?.id ?? ''
    if (!point && communities.value.length === 1) state.communityId = communities.value[0]!.value
    if (point) {
      const fieldIds = new Set(extraFields.value.filter(field => field.type === 'image')
        .map(field => String(state.extraValues[field.id] ?? '')).filter(Boolean))
      extraPhotoIds.value = [...fieldIds]
      const [map, pointDetail] = await Promise.all([
        $fetch<MapState>('/api/map'),
        $fetch<PointDetailDto>(`/api/points/${point.id}`),
      ])
      if (version !== openingVersion) return
      const feature = map.features.find(x => x.kind === 'point' && x.id === point.id)
      mapShape.value = feature?.shape ?? null
      shapeRevision.value = feature?.revision ?? 0
      state.photos = pointDetail.photos
        ?.filter(photo => !fieldIds.has(photo.id))
        .map(photo => ({ id: photo.id, width: photo.width, height: photo.height })) ?? []
    }
  } catch (value) {
    if (version === openingVersion) error.value = errorMessage(value)
  } finally {
    if (version === openingVersion) { loading.value = false; initialSnapshot.value = snapshot() }
  }
})
function validate(value: PointState): FormError[] {
  const errors: FormError[] = []
  if (!value.name.trim()) errors.push({ name: 'name', message: '请填写场所名称' })
  if (!value.communityId) errors.push({ name: 'communityId', message: '请选择所属网格' })
  if (!value.typeId) errors.push({ name: 'typeId', message: '请选择场所类型' })
  if (!value.address.trim()) errors.push({ name: 'address', message: '请填写详细地址' })
  const coordinates = parseCoordinates(value.coordinates)
  if (coordinates.error) errors.push({ name: 'coordinates', message: coordinates.error })
  return errors
}
async function save() {
  if (loading.value || saving.value || photosBusy.value || extraPhotosBusy.value || usesBusy.value || usesFailed.value || photosFailed.value || extraPhotosFailed.value || validate(state).length) return
  if (inlineUses.value.some(x => !x.name.trim() || !x.data.position.trim() || !x.data.purposeId)) { error.value = '请补全每个用途位置、用途和使用名称，或移除空白项'; return }
  saving.value = true; error.value = ''
  const { longitude, latitude } = parseCoordinates(state.coordinates)
  const body: PointWriteRequest = { extraValues: state.extraValues, color: state.color, name: state.name, communityId: state.communityId, typeId: state.typeId, address: state.address, longitude, latitude, contact: state.contact, phone: state.phone, frequency: props.point?.temporary ? 'irregular' : state.frequency, notes: state.notes, disabled: state.disabled, temporary: props.point?.temporary ?? false, photoIds: [...new Set([...state.photos.map(photo => photo.id), ...extraPhotoIds.value])] }
  body.shape = mapShape.value; body.shapeRevision = shapeRevision.value; body.revision = revision.value
  try { const point = await $fetch<PointDto>(props.point ? `/api/points/${props.point.id}` : '/api/place-dossiers', { method: props.point ? 'PUT' : 'POST', body: props.point ? body : { point: body, uses: inlineUses.value.map(x => ({ name: x.name, parentId: '', data: x.data })) }, retry: 0 }); emit('saved', point); open.value = false }
  catch (value) { error.value = errorMessage(value) }
  finally { saving.value = false }
}
onBeforeUnmount(() => { overlayForm.value = false })
</script>
<template>
  <USlideover :open="open" @update:open="changeOpen" :title="point ? '编辑场所' : '新增场所'" :dismissible="!saving && !photosBusy && !extraPhotosBusy && !usesBusy" :close="!saving && !photosBusy && !extraPhotosBusy && !usesBusy" :ui="{ content: 'w-full max-w-[560px] max-md:max-w-none' }">
    <template #body>
      <UAlert v-if="error" :description="error" color="error" variant="subtle" class="mb-3" />
      <UForm id="point-editor-form" :state="state" :disabled="loading || saving" :validate="validate" class="field-grid" @submit="save">
        <UFormField label="名称" name="name" required class="field-full"><UInput v-model="state.name" maxlength="200" class="w-full" data-testid="point-name" /></UFormField>
        <UFormField label="网格" name="communityId" required><GridTreeSelect v-model="state.communityId" :disabled="saving || loading" /><QuickGridCreate :disabled="saving || loading" @created="grid=>{state.communityId=grid.id}" /></UFormField>
        <UFormField label="场所形态" name="typeId" required><USelect v-model="state.typeId" :items="types" placeholder="请选择" class="w-full" /></UFormField>
        <ExtraFieldValues v-model="state.extraValues" v-model:photo-ids="extraPhotoIds" :fields="extraFields" phase="point" :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="extraPhotosBusy=$event" @failed="extraPhotosFailed=$event" />
         <UFormField label="地图颜色" class="field-full"><MapColorPicker v-model="state.color" :disabled="saving || loading" /></UFormField>
        <UFormField label="详细地址" name="address" required class="field-full"><UInput v-model="state.address" maxlength="1000" class="w-full" /></UFormField>
        <UFormField label="地图位置" name="coordinates" class="field-full"><LocationPicker v-model="mapShape" :disabled="saving" @picked="shape => {state.coordinates=shape.coordinates[0]!.join(',')}" /></UFormField>
        <UFormField label="联系人" name="contact"><NameTagsInput :disabled="loading || saving" v-model="state.contact" maxlength="100" class="w-full" /></UFormField>
        <UFormField label="电话" name="phone"><UInput v-model="state.phone" type="tel" maxlength="100" class="w-full" /></UFormField>
        <details class="field-full" :open="state.frequency!=='irregular'"><summary class="cursor-pointer text-sm text-muted">整体巡查周期</summary><UFormField label="周期" name="frequency" class="mt-3"><USelect v-model="state.frequency" :items="frequencyOptions" :disabled="point?.temporary" class="w-full" /></UFormField></details>
        <UFormField label="备注" name="notes" class="field-full"><UTextarea v-model="state.notes" :rows="3" maxlength="8000" class="w-full" /></UFormField>
        <UFormField label="现场图片" class="field-full"><PhotoUploader v-model="state.photos" phase="point" :max="9" deferred-removal :quality="bootstrap?.settings.jpegQuality" :disabled="saving || loading" @busy="photosBusy = $event" @failed="photosFailed=$event" /></UFormField>
        <section v-if="!point" class="field-full space-y-4 border-t border-default pt-4"><div class="flex items-center justify-between"><h3 class="font-semibold">使用情况</h3><UButton label="添加使用情况" icon="i-lucide-plus" variant="outline" :disabled="saving" @click="inlineUses.push({key:++nextUseKey,name:'',data:emptyUse(),busy:false,failed:false})" /></div><div v-for="(entry,index) in inlineUses" :key="entry.key" class="rounded-lg border border-default p-3 space-y-3"><div class="flex justify-between items-center"><strong>用途位置 {{ index+1 }}</strong><UButton label="移除" size="sm" color="neutral" variant="ghost" :disabled="saving || entry.busy" @click="inlineUses.splice(index,1)" /></div><UseFields v-model="entry.data" v-model:name="entry.name" :disabled="saving" @busy="entry.busy=$event" @failed="entry.failed=$event" /></div></section>
        <UFormField v-if="point" label="状态" name="disabled" class="field-full"><div class="check-row"><UCheckbox v-model="state.disabled" label="停用" /></div></UFormField>
      </UForm>
    </template>
    <template #footer><span v-if="photosBusy || extraPhotosBusy || usesBusy" class="mr-auto text-xs text-muted">请等待照片处理完成</span><span v-else-if="photosFailed || extraPhotosFailed || usesFailed" class="mr-auto text-sm text-error">请重试或移除上传失败的照片</span><UButton label="取消" color="neutral" variant="outline" :disabled="saving || photosBusy || extraPhotosBusy || usesBusy" @click="changeOpen(false)" /><UButton type="submit" form="point-editor-form" label="保存场所" :loading="saving" :disabled="loading || photosBusy || extraPhotosBusy || usesBusy || photosFailed || extraPhotosFailed || usesFailed || !communities.length || !types.length" data-testid="point-save" /></template>
  </USlideover>
</template>
