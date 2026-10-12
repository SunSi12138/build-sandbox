<script setup lang="ts">
import type { InspectionCreateRequest, InspectionDetailDto, InspectionResult, PageDto, PhotoUploadDto, PointDetailDto, PointDto } from '~/types/api'
import { errorMessage, queryText, todayLocal, parseCoordinates } from '~/utils/format'

interface IssueDraft {
  key: number
  description: string
  requirement: string
  deadline: string
  responsibleUnit: string
  photos: PhotoUploadDto[]
}
interface NewPointDraft { name: string; communityId: string; typeId: string; address: string; contact: string; phone: string; coordinates: string }
interface InspectionDraft {
  pointMode: 'regular' | 'temporary'
  newPoint: NewPointDraft
  pointId: string
  date: string
  inspector: string
  result: InspectionResult
  notes: string
  noticeNumber: string
  photos: PhotoUploadDto[]
  issues: IssueDraft[]
}

definePageMeta({ formPage: true })
useHead({ title: '新增排查 · 街道生态环保' })
const DRAFT_KEY = 'street-eco.inspection-draft.v2'
const route = useRoute()
const router = useRouter()
const toast = useToast()
const { bootstrap, load } = useAppData()
const today = computed(() => bootstrap.value?.today ?? todayLocal())
let nextIssueKey = 1
let draftLoaded = false
let saved = false
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchRequest: AbortController | undefined
let searchSequence = 0
let leaveDecision: ((leave: boolean) => void) | undefined

function emptyNewPoint(): NewPointDraft { return { name: '', communityId: '', typeId: '', address: '', contact: '', phone: '', coordinates: '' } }
function emptyDraft(pointId = ''): InspectionDraft {
  return { pointMode: 'regular', newPoint: emptyNewPoint(), pointId, date: today.value, inspector: bootstrap.value?.settings.defaultInspector ?? '', result: 'normal', notes: '', noticeNumber: '', photos: [], issues: [] }
}
const draft = reactive<InspectionDraft>(emptyDraft())
const initializing = ref(true)
const loadError = ref('')
const saving = ref(false)
const saveError = ref('')
const restored = ref(false)
const storageError = ref('')
const unreadableDraft = ref(false)
const lastSavedAt = ref<number | null>(null)
const selectedPoint = ref<PointDto | null>(null)
const pointError = ref('')
const pointPickerOpen = ref(true)
const communities = computed(() => (bootstrap.value?.communities ?? []).map(item => ({ label: item.name, value: item.id })))
const pointTypes = computed(() => (bootstrap.value?.pointTypes ?? []).map(item => ({ label: item.name, value: item.id })))
const selectedCommunity = computed(() => draft.pointMode === 'temporary' ? bootstrap.value?.communities.find(item => item.id === draft.newPoint.communityId)?.name ?? '' : selectedPoint.value?.communityName ?? '')
watch(selectedCommunity, (value, previous) => { for (const issue of draft.issues) if (!issue.responsibleUnit || issue.responsibleUnit === previous) issue.responsibleUnit = value })
const pointSearch = ref('')
const pointOptions = ref<PointDto[]>([])
const pointListError = ref('')
const pointListLoading = ref(false)
const pointPage = ref(1)
const pointTotal = ref(0)
const overallPhotosBusy = ref(false)
const issuePhotosBusy = reactive<Record<number, boolean>>({})
const destructiveOpen = ref(false)
const destructiveAction = ref<'clear' | 'normal' | 'issue'>('clear')
const issueToRemove = ref<number | null>(null)
const conflictOpen = ref(false)
const requestedPoint = ref('')
const leaveOpen = ref(false)

const uploadsBusy = computed(() => overallPhotosBusy.value || Object.values(issuePhotosBusy).some(Boolean))
const photoCount = computed(() => draft.photos.length + draft.issues.reduce((count, issue) => count + issue.photos.length, 0))
const formDisabled = computed(() => saving.value || initializing.value)
const hasDraft = computed(() => Boolean(draft.pointMode === 'temporary' || Object.values(draft.newPoint).some(Boolean) || draft.pointId || draft.notes || draft.noticeNumber || draft.photos.length || draft.issues.length
  || draft.inspector !== (bootstrap.value?.settings.defaultInspector ?? '') || draft.date !== today.value))
const draftSavedTime = computed(() => {
  if (lastSavedAt.value === null) return ''
  const date = new Date(lastSavedAt.value)
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
})
const destructiveTitle = computed(() => destructiveAction.value === 'clear' ? '清空这份排查草稿？'
  : destructiveAction.value === 'normal' ? '改为正常排查？' : '移除这条问题？')
const destructiveDescription = computed(() => destructiveAction.value === 'clear'
  ? '草稿中的文字和照片选择将被清空，无法撤销。尚未保存的照片会自动清理。'
  : destructiveAction.value === 'normal' ? `已登记的 ${draft.issues.length} 条问题及其照片选择将被移除，请确认本次排查没有发现问题。`
    : '这条问题的描述、整改要求、期限和照片选择将被移除，无法撤销。')

function readPhotoList(value: unknown): PhotoUploadDto[] {
  if (!Array.isArray(value)) return []
  const seen = new Set<string>()
  return value.filter((photo): photo is PhotoUploadDto => {
    if (!photo || typeof photo !== 'object') return false
    const item = photo as Partial<PhotoUploadDto>
    if (typeof item.id !== 'string' || !/^[0-9a-f]{32}$/.test(item.id) || seen.has(item.id)
      || typeof item.width !== 'number' || !Number.isInteger(item.width) || item.width < 1
      || typeof item.height !== 'number' || !Number.isInteger(item.height) || item.height < 1) return false
    seen.add(item.id)
    return true
  }).slice(0, 100).map(photo => ({ id: photo.id, width: photo.width, height: photo.height }))
}

function loadLocalDraft() {
  try {
    localStorage.removeItem('street-eco.inspection-draft.v1')
    const raw = localStorage.getItem(DRAFT_KEY)
    if (!raw) return
    const envelope: unknown = JSON.parse(raw)
    if (envelope && typeof envelope === 'object' && 'version' in envelope && envelope.version !== 2) { localStorage.removeItem(DRAFT_KEY); return }
    if (!envelope || typeof envelope !== 'object' || !('version' in envelope) || envelope.version !== 2 || !('draft' in envelope)
      || !envelope.draft || typeof envelope.draft !== 'object') throw new Error('草稿格式无法读取。')
    const value = envelope.draft as Record<string, unknown>
    const text = (key: string) => typeof value[key] === 'string' ? value[key] as string : ''
    const issues = Array.isArray(value.issues) ? value.issues : []
    Object.assign(draft, {
      pointMode: value.pointMode === 'temporary' ? 'temporary' : 'regular',
      newPoint: Object.fromEntries(Object.keys(emptyNewPoint()).map(key => [key, value.newPoint && typeof value.newPoint === 'object' && typeof (value.newPoint as Record<string, unknown>)[key] === 'string' ? (value.newPoint as Record<string, unknown>)[key] : ''])),
      pointId: text('pointId'), date: text('date') || today.value, inspector: text('inspector'),
      result: value.result === 'issues' ? 'issues' : 'normal', notes: text('notes'), noticeNumber: text('noticeNumber'),
      photos: readPhotoList(value.photos),
      issues: issues.filter(issue => issue && typeof issue === 'object').slice(0, 50).map((issue: Record<string, unknown>) => ({
        key: nextIssueKey++, description: typeof issue.description === 'string' ? issue.description : '',
        requirement: typeof issue.requirement === 'string' ? issue.requirement : '', deadline: typeof issue.deadline === 'string' ? issue.deadline : '',
        responsibleUnit: typeof issue.responsibleUnit === 'string' ? issue.responsibleUnit : '', photos: readPhotoList(issue.photos),
      })),
    })
    lastSavedAt.value = 'savedAt' in envelope && typeof envelope.savedAt === 'number' ? envelope.savedAt : null
    restored.value = true
  } catch {
    unreadableDraft.value = true
    storageError.value = '草稿无法读取，请清空草稿'
  }
}

function persistDraft() {
  if (!draftLoaded || saved || unreadableDraft.value) return
  try {
    if (!hasDraft.value) { localStorage.removeItem(DRAFT_KEY); lastSavedAt.value = null; return }
    const savedAt = Date.now()
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ version: 2, savedAt, draft }))
    lastSavedAt.value = savedAt
    storageError.value = ''
  } catch {
    storageError.value = '当前浏览器无法保存草稿'
  }
}
watch(draft, persistDraft, { deep: true, flush: 'sync' })

async function fetchPointOptions(reset = true) {
  const requestedPage = reset ? 1 : pointPage.value + 1
  searchRequest?.abort()
  const controller = new AbortController()
  searchRequest = controller
  const sequence = ++searchSequence
  pointListLoading.value = true
  pointListError.value = ''
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', {
      query: { temporary: 'false', q: pointSearch.value.trim() || undefined, recent: !pointSearch.value.trim(), page: requestedPage, pageSize: 8 },
      signal: controller.signal,
    })
    if (sequence !== searchSequence) return
    pointOptions.value = reset ? result.items : [...pointOptions.value, ...result.items]
    pointPage.value = result.page
    pointTotal.value = result.total
  } catch (error) {
    if (!controller.signal.aborted && sequence === searchSequence) pointListError.value = errorMessage(error)
  } finally { if (sequence === searchSequence) pointListLoading.value = false }
}

watch(pointSearch, () => {
  if (!draftLoaded) return
  clearTimeout(searchTimer)
  searchTimer = setTimeout(() => { void fetchPointOptions() }, 250)
})

function selectPoint(point: PointDto) {
  const previousCommunity = selectedPoint.value?.communityName
  selectedPoint.value = point
  draft.pointMode = 'regular'
  draft.pointId = point.id
  pointError.value = ''
  pointPickerOpen.value = false
  for (const issue of draft.issues)
    if (!issue.responsibleUnit || issue.responsibleUnit === previousCommunity) issue.responsibleUnit = point.communityName
}

async function fetchSelectedPoint(id: string) {
  if (!id) { selectedPoint.value = null; pointPickerOpen.value = true; return }
  pointError.value = ''
  try {
    const result = await $fetch<PointDetailDto>(`/api/points/${encodeURIComponent(id)}`)
    if (draft.pointId === id) selectPoint(result.point)
  } catch (error) {
    if (draft.pointId !== id) return
    pointError.value = errorMessage(error)
    selectedPoint.value = null
    pointPickerOpen.value = true
  }
}

async function initialize() {
  initializing.value = true
  loadError.value = ''
  if (!draftLoaded) loadLocalDraft()
  try {
    const data = await load()
    if (!draftLoaded && !restored.value) { draft.date = data.today; draft.inspector = data.settings.defaultInspector }
    const fromPoint = queryText(route.query.pointId) || queryText(route.query.point)
    if (!draftLoaded && fromPoint) {
      if (restored.value && (draft.pointMode === 'temporary' || (draft.pointId && draft.pointId !== fromPoint))) { requestedPoint.value = fromPoint; conflictOpen.value = true }
      else draft.pointId = fromPoint
    }
    draftLoaded = true
    await Promise.all([fetchPointOptions(), draft.pointMode === 'regular' ? fetchSelectedPoint(draft.pointId) : Promise.resolve()])
    persistDraft()
  } catch (error) { loadError.value = errorMessage(error) }
  finally { initializing.value = false }
}

function dateIsValid(value: string) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false
  const [year = 0, month = 0, day = 0] = value.split('-').map(Number)
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31) return false
  const date = new Date(0)
  date.setFullYear(year, month - 1, day)
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day
}

function defaultDeadline() {
  const [year = 0, month = 0, day = 0] = (dateIsValid(draft.date) ? draft.date : today.value).split('-').map(Number)
  const date = new Date(0)
  date.setHours(12, 0, 0, 0)
  date.setFullYear(year, month - 1, day + 7)
  return `${String(date.getFullYear()).padStart(4, '0')}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

function addIssue() {
  if (draft.issues.length >= 50) return
  draft.issues.push({ key: nextIssueKey++, description: '', requirement: '', deadline: defaultDeadline(), responsibleUnit: selectedCommunity.value, photos: [] })
}

function chooseResult(result: InspectionResult) {
  if (formDisabled.value || uploadsBusy.value || result === draft.result) return
  if (result === 'normal' && draft.issues.length) { destructiveAction.value = 'normal'; destructiveOpen.value = true; return }
  draft.result = result
  if (result === 'issues' && draft.issues.length === 0) addIssue()
}

function clearDraft(pointId = '') {
  unreadableDraft.value = false
  restored.value = false
  storageError.value = ''
  saveError.value = ''
  Object.assign(draft, emptyDraft(pointId))
  selectedPoint.value = null
  pointPickerOpen.value = true
  pointError.value = ''
  persistDraft()
}

function confirmDestructiveAction() {
  if (destructiveAction.value === 'clear') clearDraft()
  else if (destructiveAction.value === 'normal') { draft.result = 'normal'; draft.issues = [] }
  else draft.issues = draft.issues.filter(issue => issue.key !== issueToRemove.value)
  destructiveOpen.value = false
}

async function startRequestedPoint() {
  clearDraft(requestedPoint.value)
  conflictOpen.value = false
  await fetchSelectedPoint(requestedPoint.value)
}

function keepRestoredDraft() {
  conflictOpen.value = false
  void router.replace({ query: { ...route.query, point: undefined, pointId: draft.pointId } })
}

function validate(state: InspectionDraft) {
  const errors: { name: string; message: string }[] = []
  if (state.pointMode === 'temporary') {
    if (!state.newPoint.name.trim()) errors.push({ name: 'newPoint.name', message: '请填写点位名称' })
    if (!state.newPoint.communityId) errors.push({ name: 'newPoint.communityId', message: '请选择所属社区' })
    if (!state.newPoint.typeId) errors.push({ name: 'newPoint.typeId', message: '请选择点位类型' })
    if (!state.newPoint.address.trim()) errors.push({ name: 'newPoint.address', message: '请填写详细地址' })
    const coords = parseCoordinates(state.newPoint.coordinates)
    if (coords.error) errors.push({ name: 'newPoint.coordinates', message: coords.error })
  } else if (!state.pointId || !selectedPoint.value) errors.push({ name: 'pointId', message: '请选择一个排查点位。' })
  else if (selectedPoint.value?.disabled) errors.push({ name: 'pointId', message: '该点位已停用，请选择其他点位或先启用。' })
  if (!dateIsValid(state.date)) errors.push({ name: 'date', message: '请选择有效的排查日期。' })
  else if (state.date > today.value) errors.push({ name: 'date', message: '排查日期不能晚于今天。' })
  if (!state.inspector.trim()) errors.push({ name: 'inspector', message: '请填写排查人。' })
  if (state.result === 'issues') {
    if (!state.issues.length) errors.push({ name: 'result', message: '发现问题时，请至少登记一条问题。' })
    state.issues.forEach((issue, index) => {
      if (!issue.description.trim()) errors.push({ name: `issues.${index}.description`, message: '请描述现场发现的问题。' })
      if (!dateIsValid(issue.deadline)) errors.push({ name: `issues.${index}.deadline`, message: '请选择有效的整改期限。' })
    })
  }
  return errors
}

function validationFailed() {
  saveError.value = '还有必填内容需要检查，请按提示补充后保存。'
  void nextTick(() => {
    const invalid = document.querySelector<HTMLElement>('[aria-invalid="true"]')
    invalid?.focus()
    invalid?.scrollIntoView({ block: 'center', behavior: 'smooth' })
  })
}

async function saveInspection() {
  if (saving.value) return
  saveError.value = ''
  if (uploadsBusy.value) { saveError.value = '还有照片未上传完成，请等待处理，或重试、移除失败照片后保存。'; return }
  if (validate(draft).length) { validationFailed(); return }
  saving.value = true
  persistDraft()
  const body: InspectionCreateRequest = {
    pointId: draft.pointMode === 'regular' ? draft.pointId : null,
    newPoint: draft.pointMode === 'temporary' ? { name: draft.newPoint.name.trim(), communityId: draft.newPoint.communityId, typeId: draft.newPoint.typeId, address: draft.newPoint.address.trim(), contact: draft.newPoint.contact, phone: draft.newPoint.phone, longitude: parseCoordinates(draft.newPoint.coordinates).longitude, latitude: parseCoordinates(draft.newPoint.coordinates).latitude, frequency: 'irregular', disabled: false, temporary: true } : null, date: draft.date, inspector: draft.inspector.trim(), result: draft.result,
    notes: draft.notes, noticeNumber: draft.noticeNumber, photoIds: draft.photos.map(photo => photo.id),
    issues: draft.result === 'issues' ? draft.issues.map(issue => ({
      description: issue.description.trim(), requirement: issue.requirement, deadline: issue.deadline,
      responsibleUnit: issue.responsibleUnit, photoIds: issue.photos.map(photo => photo.id),
    })) : [],
  }
  try {
    const result = await $fetch<InspectionDetailDto>('/api/inspections', { method: 'POST', body, retry: 0 })
    saved = true
    try { localStorage.removeItem(DRAFT_KEY) } catch { /* The server record is already durable. */ }
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
    await navigateTo(`/inspections/${result.inspection.id}`)
  } catch (error) { saveError.value = errorMessage(error) }
  finally { saving.value = false }
}

function leavePage(leave: boolean) {
  leaveOpen.value = false
  leaveDecision?.(leave)
  leaveDecision = undefined
}

onBeforeRouteLeave(() => {
  persistDraft()
  if (saved) return true
  if (saving.value) return false
  if (!uploadsBusy.value) return true
  leaveDecision?.(false)
  leaveOpen.value = true
  return new Promise<boolean>(resolve => { leaveDecision = resolve })
})

function beforeUnload(event: BeforeUnloadEvent) {
  persistDraft()
  if (!saved && (uploadsBusy.value || saving.value)) { event.preventDefault(); event.returnValue = '' }
}
function onVisibilityChange() { if (document.visibilityState === 'hidden') persistDraft() }

onMounted(() => {
  window.addEventListener('beforeunload', beforeUnload)
  window.addEventListener('pagehide', persistDraft)
  document.addEventListener('visibilitychange', onVisibilityChange)
  void initialize()
})
onBeforeUnmount(() => {
  persistDraft()
  clearTimeout(searchTimer)
  searchRequest?.abort()
  leaveDecision?.(false)
  window.removeEventListener('beforeunload', beforeUnload)
  window.removeEventListener('pagehide', persistDraft)
  document.removeEventListener('visibilitychange', onVisibilityChange)
})
</script>

<template>
  <div class="page-stack">
    <PageHeader title="新增排查" back="/inspections"><template #actions><UButton type="button" color="neutral" variant="link" :disabled="saving || uploadsBusy || (!hasDraft && !unreadableDraft)" @click="destructiveAction = 'clear'; destructiveOpen = true">清空</UButton></template></PageHeader>
    <PageState :loading="initializing" :error="loadError" @retry="initialize">
      <UAlert v-if="storageError" :description="storageError" color="warning" variant="subtle" />
      <UForm id="inspection-form" :state="draft" :validate="validate" class="space-y-3" @submit="saveInspection" @error="validationFailed">
        <div class="form-sections">
          <section class="form-section"><h2>点位</h2><div class="chip-row mb-2"><UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="draft.pointMode === 'regular'" :disabled="formDisabled" @click="draft.pointMode = 'regular'">常态点位</UButton><UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="draft.pointMode === 'temporary'" :disabled="formDisabled" data-testid="temporary-point-mode" @click="draft.pointMode = 'temporary'">临时点位</UButton></div>
            <div v-if="draft.pointMode === 'regular'">
              <UFormField name="pointId" label="常态点位" required>
                <div v-if="selectedPoint && !pointPickerOpen" class="flex items-center gap-2"><p class="min-w-0 flex-1 truncate text-sm">{{ selectedPoint.name }} · {{ selectedPoint.communityName }} · {{ selectedPoint.typeName }}</p><UButton type="button" label="更换" color="neutral" variant="link" :disabled="formDisabled" @click="pointPickerOpen = true" /></div>
                <div v-else><UInput v-model="pointSearch" type="search" icon="i-lucide-search" placeholder="搜索点位" aria-label="搜索常态点位" data-testid="inspection-point-search" class="mb-2 w-full" :disabled="formDisabled" /><p v-if="pointListError" class="text-sm text-error">{{ pointListError }}<UButton type="button" label="重试" color="error" variant="link" @click="fetchPointOptions()" /></p><div v-if="pointListLoading && !pointOptions.length"><USkeleton v-for="row in 3" :key="row" class="my-2 h-12 w-full rounded" /></div><button v-for="point in pointOptions" :key="point.id" type="button" class="flex h-14 w-full items-center border-b border-[var(--divider)] py-2 text-left" :disabled="formDisabled" :aria-pressed="draft.pointId === point.id" data-testid="inspection-point-option" @click="selectPoint(point)"><span class="min-w-0 flex-1"><span class="block truncate text-sm font-medium">{{ point.name }}</span><span class="block truncate text-xs text-muted">{{ point.communityName }} · {{ point.typeName }}</span></span><UIcon v-if="draft.pointId === point.id" name="i-lucide-check" class="size-4 text-primary" /></button><p v-if="!pointListLoading && !pointOptions.length" class="py-3 text-sm text-muted">暂无常态点位</p><UButton v-if="pointOptions.length < pointTotal" type="button" label="加载更多" color="neutral" variant="link" :loading="pointListLoading" @click="fetchPointOptions(false)" /></div>
              </UFormField><p v-if="pointError" class="text-sm text-error">{{ pointError }}</p>
            </div>
            <div v-else class="field-grid">
              <UFormField name="newPoint.name" label="名称" required class="field-full"><UInput v-model="draft.newPoint.name" data-testid="temporary-point-name" :maxlength="200" class="w-full" /></UFormField>
              <UFormField name="newPoint.communityId" label="社区" required><USelect v-model="draft.newPoint.communityId" :items="communities" placeholder="请选择" class="w-full" /></UFormField><UFormField name="newPoint.typeId" label="类型" required><USelect v-model="draft.newPoint.typeId" :items="pointTypes" placeholder="请选择" class="w-full" /></UFormField>
              <UFormField name="newPoint.address" label="详细地址" required class="field-full"><UInput v-model="draft.newPoint.address" :maxlength="1000" class="w-full" /></UFormField><UFormField name="newPoint.contact" label="联系人"><UInput v-model="draft.newPoint.contact" :maxlength="100" class="w-full" /></UFormField><UFormField name="newPoint.phone" label="电话"><UInput v-model="draft.newPoint.phone" type="tel" :maxlength="100" class="w-full" /></UFormField><UFormField name="newPoint.coordinates" label="经纬度" class="field-full"><UInput v-model="draft.newPoint.coordinates" placeholder="88.893426,29.274331" class="w-full" /></UFormField>
            </div>
          </section>
          <section class="form-section"><h2>排查信息</h2><div class="field-grid"><UFormField name="date" label="排查日期" required><UInput v-model="draft.date" type="date" :max="today" class="w-full" :disabled="formDisabled" data-testid="inspection-date" /></UFormField><UFormField name="inspector" label="排查人" required><UInput v-model="draft.inspector" :maxlength="100" class="w-full" :disabled="formDisabled" data-testid="inspection-inspector" /></UFormField><UFormField name="noticeNumber" label="通知单编号" class="field-full"><UInput v-model="draft.noticeNumber" :maxlength="200" class="w-full" :disabled="formDisabled" /></UFormField></div></section>
          <section class="form-section"><h2>排查结果</h2><UFormField name="result"><div class="chip-row"><UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="draft.result === 'normal'" :disabled="formDisabled || uploadsBusy" data-testid="inspection-result-normal" @click="chooseResult('normal')">正常</UButton><UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="draft.result === 'issues'" :disabled="formDisabled || uploadsBusy" data-testid="inspection-result-issues" @click="chooseResult('issues')">发现问题</UButton></div></UFormField>
            <template v-if="draft.result === 'issues'"><div v-for="(issue, index) in draft.issues" :key="issue.key" class="problem-form"><div class="mb-2 flex items-center justify-between"><h3 class="text-sm font-semibold">问题 {{ index + 1 }}</h3><UButton type="button" label="移除" color="error" variant="link" :disabled="formDisabled || uploadsBusy" @click="issueToRemove = issue.key; destructiveAction = 'issue'; destructiveOpen = true" /></div><div class="field-grid"><UFormField :name="`issues.${index}.description`" label="问题描述" required class="field-full"><UTextarea v-model="issue.description" data-testid="inspection-issue-description" :rows="3" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField><UFormField :name="`issues.${index}.requirement`" label="整改要求" class="field-full"><UTextarea v-model="issue.requirement" :rows="2" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField><UFormField :name="`issues.${index}.deadline`" label="整改期限" required description="默认排查日后 7 天"><UInput v-model="issue.deadline" type="date" data-testid="inspection-issue-deadline" class="w-full" :disabled="formDisabled" /></UFormField><UFormField :name="`issues.${index}.responsibleUnit`" label="责任单位"><UInput v-model="issue.responsibleUnit" :maxlength="200" class="w-full" :disabled="formDisabled" /></UFormField><UFormField label="问题照片" class="field-full"><PhotoUploader v-model="issue.photos" phase="site" :quality="bootstrap?.settings.jpegQuality" :disabled="formDisabled" @busy="issuePhotosBusy[issue.key] = $event" /></UFormField></div></div><UButton type="button" label="添加问题" icon="i-lucide-plus" variant="outline" class="mt-3" :disabled="formDisabled || draft.issues.length >= 50" @click="addIssue" /></template>
          </section>
          <section class="form-section"><h2>现场照片</h2><PhotoUploader v-model="draft.photos" phase="site" :quality="bootstrap?.settings.jpegQuality" :disabled="formDisabled" @busy="overallPhotosBusy = $event" /></section>
          <section class="form-section"><UFormField name="notes" label="备注"><UTextarea v-model="draft.notes" :rows="3" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField></section>
        </div>
        <UAlert v-if="saveError" :description="saveError" color="error" variant="subtle" />
        <div class="form-actions"><p class="draft-state" aria-live="polite">{{ uploadsBusy ? '照片上传中' : draftSavedTime ? `草稿已保存 ${draftSavedTime}` : '草稿未保存' }}</p><UButton type="submit" data-testid="inspection-save" label="保存" :loading="saving" :disabled="uploadsBusy || (draft.pointMode === 'regular' && (!selectedPoint || selectedPoint.disabled))" /></div>
      </UForm>
    </PageState>
    <UModal v-model:open="destructiveOpen" :title="destructiveTitle" :description="destructiveDescription"><template #footer><UButton type="button" label="取消" color="neutral" variant="outline" @click="destructiveOpen = false" /><UButton type="button" :label="destructiveAction === 'clear' ? '清空' : destructiveAction === 'normal' ? '改为正常' : '移除'" color="error" @click="confirmDestructiveAction" /></template></UModal>
    <UModal v-model:open="conflictOpen" title="清空已有草稿？" description="清空后无法恢复" :dismissible="false" :close="false"><template #footer><UButton type="button" label="继续草稿" color="neutral" variant="outline" @click="keepRestoredDraft" /><UButton type="button" label="清空并新建" color="error" @click="startRequestedPoint" /></template></UModal>
    <UModal v-model:open="leaveOpen" title="照片尚未上传完成" description="离开后，未上传的照片需重新选择" :dismissible="false" :close="false"><template #footer><UButton type="button" label="离开" color="neutral" variant="outline" @click="leavePage(true)" /><UButton type="button" label="继续等待" @click="leavePage(false)" /></template></UModal>
  </div>
</template>
