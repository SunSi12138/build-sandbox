<script setup lang="ts">
import type { InspectionCreateRequest, InspectionDetailDto, InspectionResult, PageDto, PhotoUploadDto, PointDetailDto, PointDto } from '~/types/api'
import { errorMessage, queryText, todayLocal } from '~/utils/format'

interface IssueDraft {
  key: number
  description: string
  requirement: string
  deadline: string
  responsibleUnit: string
  photos: PhotoUploadDto[]
}
interface InspectionDraft {
  pointId: string
  date: string
  inspector: string
  result: InspectionResult
  notes: string
  noticeNumber: string
  photos: PhotoUploadDto[]
  issues: IssueDraft[]
}

const DRAFT_KEY = 'street-eco.inspection-draft.v1'
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

function emptyDraft(pointId = ''): InspectionDraft {
  return { pointId, date: today.value, inspector: bootstrap.value?.settings.defaultInspector ?? '', result: 'normal', notes: '', noticeNumber: '', photos: [], issues: [] }
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
const hasDraft = computed(() => Boolean(draft.pointId || draft.notes || draft.noticeNumber || draft.photos.length || draft.issues.length
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
    const raw = localStorage.getItem(DRAFT_KEY)
    if (!raw) return
    const envelope: unknown = JSON.parse(raw)
    if (!envelope || typeof envelope !== 'object' || !('version' in envelope) || envelope.version !== 1 || !('draft' in envelope)
      || !envelope.draft || typeof envelope.draft !== 'object') throw new Error('草稿格式无法读取。')
    const value = envelope.draft as Record<string, unknown>
    const text = (key: string) => typeof value[key] === 'string' ? value[key] as string : ''
    const issues = Array.isArray(value.issues) ? value.issues : []
    Object.assign(draft, {
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
    storageError.value = '上次草稿暂时无法读取。请先清空旧草稿，再开始自动保存。'
  }
}

function persistDraft() {
  if (!draftLoaded || saved || unreadableDraft.value) return
  try {
    if (!hasDraft.value) { localStorage.removeItem(DRAFT_KEY); lastSavedAt.value = null; return }
    const savedAt = Date.now()
    localStorage.setItem(DRAFT_KEY, JSON.stringify({ version: 1, savedAt, draft }))
    lastSavedAt.value = savedAt
    storageError.value = ''
  } catch {
    storageError.value = '当前浏览器无法保存草稿。请保持此页面打开，完成后及时保存排查记录。'
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
      query: { q: pointSearch.value.trim() || undefined, recent: !pointSearch.value.trim(), page: requestedPage, pageSize: 8 },
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
      if (restored.value && draft.pointId && draft.pointId !== fromPoint) { requestedPoint.value = fromPoint; conflictOpen.value = true }
      else draft.pointId = fromPoint
    }
    draftLoaded = true
    await Promise.all([fetchPointOptions(), fetchSelectedPoint(draft.pointId)])
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
  draft.issues.push({ key: nextIssueKey++, description: '', requirement: '', deadline: defaultDeadline(), responsibleUnit: selectedPoint.value?.communityName ?? '', photos: [] })
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
  if (!state.pointId || !selectedPoint.value) errors.push({ name: 'pointId', message: '请选择一个排查点位。' })
  else if (selectedPoint.value.disabled) errors.push({ name: 'pointId', message: '该点位已停用，请选择其他点位或先启用。' })
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
    pointId: draft.pointId, date: draft.date, inspector: draft.inspector.trim(), result: draft.result,
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
    toast.add({ title: '排查已保存', description: draft.result === 'issues' ? `已登记 ${draft.issues.length} 条问题，可继续跟踪整改。` : '本次正常排查已记录。', color: 'success' })
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
    <PageHeader title="新建排查" description="现场拍照、逐项记录，正常排查也能留下完整记录。" eyebrow="现场工作">
      <template #actions>
        <UButton type="button" icon="i-lucide-eraser" color="neutral" variant="outline" class="min-h-12" :disabled="saving || uploadsBusy || (!hasDraft && !unreadableDraft)" @click="destructiveAction = 'clear'; destructiveOpen = true">清空草稿</UButton>
      </template>
    </PageHeader>

    <PageState :loading="initializing" :error="loadError" @retry="initialize">
      <UAlert v-if="restored" title="已恢复上次未保存的排查" description="文字与已上传照片已保留，可以接着填写。尚未上传完成的照片需要重新选择。" color="info" variant="subtle" icon="i-lucide-history" />
      <UAlert v-if="storageError" title="草稿提醒" :description="storageError" color="warning" variant="subtle" icon="i-lucide-circle-alert" />

      <UForm :state="draft" :validate="validate" class="space-y-6" @submit="saveInspection" @error="validationFailed">
        <div class="grid gap-6 xl:grid-cols-[minmax(0,1.45fr)_minmax(300px,1fr)] xl:items-start">
          <UCard class="min-w-0">
            <template #header>
              <h2 class="flex items-center gap-3 font-semibold text-highlighted"><UBadge variant="soft" color="primary" class="justify-center">1</UBadge>选择排查点位</h2>
            </template>
            <UFormField name="pointId" label="排查点位" required>
              <div v-if="selectedPoint" class="mt-2 rounded-xl border border-primary/20 bg-primary/5 p-4">
                <div class="flex items-start justify-between gap-3">
                  <div class="min-w-0">
                    <p class="font-semibold leading-6 text-highlighted">{{ selectedPoint.name }}</p>
                    <p class="mt-1 text-sm text-muted">{{ selectedPoint.communityName }} · {{ selectedPoint.typeName }}</p>
                    <p class="mt-2 text-sm leading-6">{{ selectedPoint.address }}</p>
                  </div>
                  <UButton type="button" color="primary" variant="ghost" class="min-h-12 shrink-0" :disabled="formDisabled || uploadsBusy" @click="pointPickerOpen = !pointPickerOpen">更换</UButton>
                </div>
                <UAlert v-if="selectedPoint.disabled" title="此点位已停用，请更换点位或在点位管理中启用。" color="warning" variant="subtle" class="mt-3" />
              </div>
              <UAlert v-if="pointError" :description="pointError + ' 请重新选择排查点位。'" color="warning" variant="subtle" class="mt-2" />

              <div v-if="pointPickerOpen" class="mt-3 space-y-3">
                <UInput v-model="pointSearch" data-testid="inspection-point-search" icon="i-lucide-search" placeholder="搜索点位名称、地址或联系人" :maxlength="200" size="xl" class="w-full" :disabled="formDisabled || uploadsBusy" aria-label="搜索排查点位" />
                <p class="text-xs text-muted">{{ pointSearch.trim() ? '搜索结果' : '最近使用与可排查点位' }}</p>
                <UAlert v-if="pointListError" :description="pointListError" color="error" variant="subtle">
                  <template #actions><UButton type="button" variant="soft" @click="fetchPointOptions()">重试</UButton></template>
                </UAlert>
                <div v-else class="overflow-hidden rounded-xl border border-default divide-y divide-default">
                  <UButton v-for="point in pointOptions" :key="point.id" type="button" data-testid="inspection-point-option" color="neutral" variant="ghost" class="min-h-16 w-full justify-between gap-3 rounded-none p-4 text-left" :disabled="formDisabled || uploadsBusy || pointListLoading" :aria-pressed="draft.pointId === point.id" @click="selectPoint(point)">
                    <span class="min-w-0">
                      <span class="block break-words font-medium text-highlighted">{{ point.name }}</span>
                      <span class="mt-1 block text-xs leading-5 text-muted">{{ point.communityName }} · {{ point.typeName }}</span>
                      <span class="mt-1 block text-xs leading-5 text-muted">{{ point.address }}</span>
                    </span>
                    <UIcon name="i-lucide-chevron-right" class="size-4 shrink-0 text-muted" />
                  </UButton>
                  <p v-if="pointListLoading" role="status" class="p-4 text-sm text-muted">正在查找点位…</p>
                  <div v-else-if="!pointOptions.length" class="space-y-3 p-5 text-center">
                    <p class="text-sm text-muted">{{ pointSearch ? '没有找到匹配点位，请换个关键词。' : '还没有可排查的点位。' }}</p>
                    <UButton to="/points" variant="soft" class="min-h-12">打开点位管理</UButton>
                  </div>
                </div>
                <UButton v-if="pointOptions.length < pointTotal" type="button" color="neutral" variant="soft" class="min-h-12 w-full justify-center" :loading="pointListLoading" :disabled="formDisabled || uploadsBusy" @click="fetchPointOptions(false)">显示更多点位</UButton>
              </div>
            </UFormField>
          </UCard>

          <UCard class="min-w-0">
            <template #header>
              <h2 class="flex items-center gap-3 font-semibold text-highlighted"><UBadge variant="soft" color="primary" class="justify-center">2</UBadge>核对排查信息</h2>
            </template>
            <div class="space-y-5">
              <UFormField name="date" label="排查日期" required><UInput v-model="draft.date" data-testid="inspection-date" type="date" :max="today" class="w-full" :disabled="formDisabled" /></UFormField>
              <UFormField name="inspector" label="排查人" required><UInput v-model="draft.inspector" data-testid="inspection-inspector" placeholder="填写排查人姓名" :maxlength="100" size="xl" class="w-full" :disabled="formDisabled" /></UFormField>
              <UFormField name="noticeNumber" label="通知单编号" description="手工填写，可留空。"><UInput v-model="draft.noticeNumber" placeholder="例如：街环整〔2026〕12号" :maxlength="100" size="xl" class="w-full" :disabled="formDisabled" /></UFormField>
            </div>
          </UCard>
        </div>

        <UCard>
          <template #header>
            <h2 class="flex items-center gap-3 font-semibold text-highlighted"><UBadge variant="soft" color="primary" class="justify-center">3</UBadge>记录排查结果</h2>
          </template>
          <div class="space-y-6">
            <UFormField name="result" label="本次排查结果" required>
              <div class="mt-2 grid grid-cols-2 gap-3" role="group" aria-label="排查结果">
                <UButton type="button" data-testid="inspection-result-normal" icon="i-lucide-circle-check" :variant="draft.result === 'normal' ? 'solid' : 'outline'" color="primary" size="xl" class="min-h-14 justify-center" :disabled="formDisabled || uploadsBusy" :aria-pressed="draft.result === 'normal'" @click="chooseResult('normal')">正常</UButton>
                <UButton type="button" data-testid="inspection-result-issues" icon="i-lucide-circle-alert" :variant="draft.result === 'issues' ? 'solid' : 'outline'" color="warning" size="xl" class="min-h-14 justify-center" :disabled="formDisabled || uploadsBusy" :aria-pressed="draft.result === 'issues'" @click="chooseResult('issues')">发现问题</UButton>
              </div>
            </UFormField>

            <div v-if="draft.result === 'issues'" class="space-y-5">
              <div v-for="(issue, index) in draft.issues" :key="issue.key" class="rounded-2xl border border-default bg-elevated/30 p-4 sm:p-5">
                <div class="mb-5 flex items-center justify-between gap-3">
                  <h3 class="font-semibold text-highlighted">问题 {{ index + 1 }}</h3>
                  <UButton type="button" icon="i-lucide-trash-2" color="error" variant="ghost" class="min-h-12" :disabled="formDisabled || uploadsBusy" @click="issueToRemove = issue.key; destructiveAction = 'issue'; destructiveOpen = true">移除问题</UButton>
                </div>
                <div class="grid gap-5 md:grid-cols-2">
                  <UFormField :name="`issues.${index}.description`" label="问题描述" required class="md:col-span-2"><UTextarea v-model="issue.description" data-testid="inspection-issue-description" placeholder="具体位置、发现的情况，例如：店后巷垃圾未及时清运，存在污水外流。" :rows="3" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField>
                  <UFormField :name="`issues.${index}.requirement`" label="整改要求" class="md:col-span-2"><UTextarea v-model="issue.requirement" placeholder="需要采取什么措施、达到什么要求" :rows="2" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField>
                  <UFormField :name="`issues.${index}.deadline`" label="整改期限" required description="默认排查日后 7 天，可按实际情况修改。"><UInput v-model="issue.deadline" data-testid="inspection-issue-deadline" type="date" class="w-full" :disabled="formDisabled" /></UFormField>
                  <UFormField :name="`issues.${index}.responsibleUnit`" label="责任单位" description="默认所属社区，可修改。"><UInput v-model="issue.responsibleUnit" placeholder="所属社区或责任单位" :maxlength="200" size="xl" class="w-full" :disabled="formDisabled" /></UFormField>
                  <div class="space-y-3 md:col-span-2">
                    <p class="text-sm font-medium text-highlighted">这条问题的现场照片</p>
                    <PhotoUploader v-model="issue.photos" phase="site" :quality="bootstrap?.settings.jpegQuality" :disabled="formDisabled" @busy="issuePhotosBusy[issue.key] = $event" />
                  </div>
                </div>
              </div>
              <UButton type="button" icon="i-lucide-plus" variant="outline" class="min-h-12 w-full justify-center" :disabled="formDisabled || draft.issues.length >= 50" @click="addIssue">再登记一条问题</UButton>
            </div>
            <UAlert v-else title="本次未发现问题" description="保存后会生成一条正常排查记录，计入点位排查周期。" color="success" variant="subtle" icon="i-lucide-circle-check" />

            <div class="space-y-3 border-t border-default pt-5">
              <p class="text-sm font-medium text-highlighted">{{ draft.result === 'normal' ? '现场照片（可选）' : '点位整体现场照片（可选）' }}</p>
              <PhotoUploader v-model="draft.photos" phase="site" :quality="bootstrap?.settings.jpegQuality" :disabled="formDisabled" @busy="overallPhotosBusy = $event" />
            </div>
            <UFormField name="notes" label="排查备注"><UTextarea v-model="draft.notes" placeholder="补充现场情况、沟通内容或其他需要留痕的事项" :rows="3" :maxlength="8000" autoresize class="w-full" :disabled="formDisabled" /></UFormField>
          </div>
        </UCard>

        <UCard>
          <template #header>
            <h2 class="flex items-center gap-3 font-semibold text-highlighted"><UBadge variant="soft" color="primary" class="justify-center">4</UBadge>确认并保存</h2>
          </template>
          <div class="flex flex-col gap-5 md:flex-row md:items-center md:justify-between">
            <div class="min-w-0 space-y-2">
              <p class="font-medium text-highlighted">{{ selectedPoint?.name || '请先选择排查点位' }}</p>
              <p class="text-sm leading-6 text-muted">{{ draft.date }} · {{ draft.inspector || '尚未填写排查人' }} · {{ draft.result === 'normal' ? '正常排查' : `${draft.issues.length} 条问题` }} · {{ photoCount }} 张照片</p>
              <p v-if="uploadsBusy" role="status" class="text-sm text-warning">照片尚未全部上传，请等待完成，或重试、移除失败照片。</p>
              <p v-else-if="draftSavedTime" class="text-xs text-muted">草稿已自动保存 · {{ draftSavedTime }}</p>
            </div>
            <UButton type="submit" data-testid="inspection-save" icon="i-lucide-check" size="xl" class="min-h-14 justify-center md:min-w-44" :loading="saving" :disabled="uploadsBusy || !selectedPoint || selectedPoint.disabled">保存排查记录</UButton>
          </div>
          <UAlert v-if="saveError" title="排查暂未完成保存" :description="saveError" color="error" variant="subtle" icon="i-lucide-circle-alert" class="mt-5" />
        </UCard>
      </UForm>
    </PageState>

    <UModal v-model:open="destructiveOpen" :title="destructiveTitle" :description="destructiveDescription" :ui="{ footer: 'justify-end' }">
      <template #body><p class="text-sm leading-6 text-muted">请确认当前选择，尚未保存的内容无法撤销。</p></template>
      <template #footer>
        <UButton type="button" color="neutral" variant="outline" class="min-h-12" @click="destructiveOpen = false">取消</UButton>
        <UButton type="button" color="error" class="min-h-12" @click="confirmDestructiveAction">确认{{ destructiveAction === 'clear' ? '清空' : destructiveAction === 'normal' ? '改为正常' : '移除' }}</UButton>
      </template>
    </UModal>

    <UModal v-model:open="conflictOpen" title="还有一份未完成的排查" description="你正在从另一个点位发起排查。可以继续原有草稿，或清空草稿后开始这次排查。" :dismissible="false" :close="false" :ui="{ footer: 'flex-col sm:flex-row sm:justify-end' }">
      <template #body><p class="text-sm leading-6 text-muted">选择清空后，原草稿的文字和照片选择将被移除，无法撤销。</p></template>
      <template #footer>
        <UButton type="button" color="neutral" variant="outline" class="min-h-12 w-full sm:w-auto" @click="keepRestoredDraft">继续原有草稿</UButton>
        <UButton type="button" color="error" class="min-h-12 w-full sm:w-auto" @click="startRequestedPoint">清空并新建</UButton>
      </template>
    </UModal>

    <UModal v-model:open="leaveOpen" title="还有照片未上传完成" description="已填写文字和已上传照片会保留在草稿中。现在离开，未上传完成的照片需要重新选择。" :dismissible="false" :close="false" :ui="{ footer: 'justify-end' }">
      <template #footer>
        <UButton type="button" color="neutral" variant="outline" class="min-h-12" @click="leavePage(true)">离开页面</UButton>
        <UButton type="button" class="min-h-12" @click="leavePage(false)">继续等待</UButton>
      </template>
    </UModal>
  </div>
</template>
