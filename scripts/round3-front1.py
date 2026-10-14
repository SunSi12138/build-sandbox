from pathlib import Path
p=Path('.')
def put(name,content): (p/name).write_text(content)
def replace(name,old,new):
 f=p/name; s=f.read_text(); assert old in s, (name,old[:100]); f.write_text(s.replace(old,new))
put('web/app/utils/names.ts', '''// These are string fields in the API. Only the editor normalizes delimiters.
export function splitNames(value: string): string[] {
  return value.trim() ? value.trim().split(/\\s+/u) : []
}

export function normalizeNames(value: string): string {
  return value.split(/[，,。.\\s]+/u).filter(Boolean).join(' ')
}
''')
put('web/app/components/NameTags.vue','''<script setup lang="ts">
import { splitNames } from '~/utils/names'
const props = withDefaults(defineProps<{ value?: string | null; empty?: string }>(), { empty: '—' })
const names = computed(() => splitNames(props.value ?? ''))
</script>

<template>
  <span v-if="names.length" class="name-tags" data-testid="name-tags">
    <template v-for="(name, index) in names" :key="index"><span v-if="index" class="sr-only"> </span><UBadge color="neutral" variant="subtle">{{ name }}</UBadge></template>
  </span>
  <span v-else>{{ empty }}</span>
</template>
''')
put('web/app/components/NameTagsInput.vue','''<script setup lang="ts">
import { normalizeNames, splitNames } from '~/utils/names'

defineOptions({ inheritAttrs: false })
const model = defineModel<string>({ default: '' })
const props = withDefaults(defineProps<{ disabled?: boolean; maxlength?: number | string }>(), { maxlength: 100 })
const tags = ref(splitNames(normalizeNames(model.value)))
const text = ref('')
const input = ref<{ inputRef?: HTMLInputElement } | null>(null)
const error = ref('')
const limit = computed(() => Number(props.maxlength))
const remaining = computed(() => Math.max(0, limit.value - tags.value.join(' ').length - (tags.value.length ? 1 : 0)))
let composing = false
let published = model.value

function publish() {
  // Persist the unfinished name too, before blur/Enter, so a refresh cannot lose it.
  published = normalizeNames([...tags.value, text.value].join(' '))
  model.value = published
}
watch(model, value => {
  if (value === published) return
  tags.value = splitNames(normalizeNames(value))
  text.value = ''
  published = value
  error.value = ''
})
function update(value: string | number | undefined) {
  const raw = String(value ?? '')
  if (normalizeNames([...tags.value, raw].join(' ')).length > limit.value) {
    error.value = `最多 ${limit.value} 字`
    if (input.value?.inputRef) input.value.inputRef.value = text.value
    return
  }
  error.value = ''
  if (composing) text.value = raw
  else {
    const pieces = raw.split(/[，,。.\\s]+/u)
    text.value = pieces.pop() ?? ''
    tags.value.push(...pieces.filter(Boolean))
    if (input.value?.inputRef) input.value.inputRef.value = text.value
  }
  publish()
}
function commit() {
  if (composing || props.disabled) return
  tags.value.push(...splitNames(normalizeNames(text.value)))
  text.value = ''
  publish()
}
function keydown(event: KeyboardEvent) {
  if (props.disabled || event.isComposing || composing || event.keyCode === 229) return
  if (event.key === 'Enter') { event.preventDefault(); commit() }
  else if (event.key === 'Backspace' && !text.value && tags.value.length) {
    event.preventDefault()
    text.value = tags.value.pop() ?? ''
    publish()
  }
}
function paste(event: ClipboardEvent) {
  if (props.disabled || !event.clipboardData) return
  event.preventDefault()
  const element = input.value?.inputRef
  const start = element?.selectionStart ?? text.value.length
  const end = element?.selectionEnd ?? start
  update(text.value.slice(0, start) + event.clipboardData.getData('text/plain') + text.value.slice(end))
}
function remove(index: number) {
  if (props.disabled) return
  tags.value.splice(index, 1)
  error.value = ''
  publish()
  input.value?.inputRef?.focus()
}
function compositionEnd(event: CompositionEvent) {
  composing = false
  update((event.target as HTMLInputElement).value)
}
</script>

<template>
  <div class="names-field">
    <div class="names-editor" :class="{ 'names-disabled': disabled }">
      <span v-for="(tag, index) in tags" :key="index" class="name-edit-tag">
        <UBadge color="neutral" variant="subtle">{{ tag }}</UBadge>
        <UButton type="button" icon="i-lucide-x" color="neutral" variant="ghost" class="name-remove" :aria-label="`移除 ${tag}`" :disabled="disabled" @mousedown.prevent @click="remove(index)" />
      </span>
      <UInput ref="input" v-bind="$attrs" :model-value="text" :disabled="disabled" :maxlength="remaining" variant="none" class="names-input" :ui="{ base: 'ring-0 focus:ring-0 shadow-none' }" @update:model-value="update" @keydown="keydown" @paste="paste" @blur="commit" @compositionstart="composing = true" @compositionend="compositionEnd" />
    </div>
    <p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p>
  </div>
</template>
''')
put('web/app/components/CoordinateLabel.vue','''<template>
  <span class="coordinate-label">经纬度 <UButton to="https://ditu.gaode.com/ssr/coordinate-picker" target="_blank" rel="noopener noreferrer" external :prefetch="false" type="button" color="primary" variant="link" class="coordinate-picker" label="选取" @click.stop /></span>
</template>
''')
put('web/app/components/RecordPointPicker.vue','''<script setup lang="ts">
import type { PageDto, PointDetailDto, PointDto, NewPointDraft } from '~/types/api'
import { errorMessage } from '~/utils/format'
const mode = defineModel<'regular' | 'temporary'>('mode', { required: true })
const pointId = defineModel<string>('pointId', { required: true })
const newPoint = defineModel<NewPointDraft>('newPoint', { required: true })
const props = withDefaults(defineProps<{ disabled?: boolean; prefix?: string }>(), { prefix: 'campaign' })
const emit = defineEmits<{ selected: [point: PointDto | null]; busy: [value: boolean] }>()
const { bootstrap } = useAppData()
const selected = ref<PointDto | null>(null)
const expanded = ref(true)
const search = ref('')
const points = ref<PointDto[]>([])
const total = ref(0)
const page = ref(0)
const searching = ref(false)
const resolving = ref(false)
const error = ref('')
let timer: ReturnType<typeof setTimeout> | undefined
let controller: AbortController | undefined
let sequence = 0
let selectedSequence = 0
const communities = computed(() => (bootstrap.value?.communities ?? []).map(p => ({ label: p.name, value: p.id })))
const types = computed(() => (bootstrap.value?.pointTypes ?? []).map(p => ({ label: p.name, value: p.id })))
watch(resolving, value => emit('busy', value), { immediate: true })
async function list(reset = true) {
  controller?.abort()
  controller = new AbortController()
  const version = ++sequence
  searching.value = true
  error.value = ''
  try {
    const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { q: search.value.trim() || undefined, recent: !search.value.trim(), temporary: 'false', page: reset ? 1 : page.value + 1, pageSize: 8 }, signal: controller.signal })
    if (version !== sequence) return
    points.value = reset ? result.items : [...points.value, ...result.items]
    total.value = result.total
    page.value = result.page
  } catch (cause) { if (version === sequence && !controller.signal.aborted) error.value = errorMessage(cause) }
  finally { if (version === sequence) searching.value = false }
}
function choose(point: PointDto) {
  if (props.disabled) return
  selected.value = point
  pointId.value = point.id
  expanded.value = false
  error.value = ''
  emit('selected', point)
}
watch(pointId, async id => {
  const version = ++selectedSequence
  if (!id) { selected.value = null; expanded.value = true; resolving.value = false; emit('selected', null); return }
  if (selected.value?.id === id) return
  resolving.value = true
  error.value = ''
  try {
    const value = await $fetch<PointDetailDto>(`/api/points/${encodeURIComponent(id)}`)
    if (version !== selectedSequence) return
    selected.value = value.point
    expanded.value = false
    emit('selected', value.point)
    if (value.point.disabled) error.value = '该点位已停用'
  } catch (cause) {
    if (version === selectedSequence) { error.value = errorMessage(cause); selected.value = null; expanded.value = true; emit('selected', null) }
  } finally { if (version === selectedSequence) resolving.value = false }
}, { immediate: true })
watch(search, () => { clearTimeout(timer); timer = setTimeout(() => { void list() }, 250) })
onMounted(() => { void list() })
onBeforeUnmount(() => { clearTimeout(timer); controller?.abort(); selectedSequence++; emit('busy', false) })
</script>

<template>
  <section class="form-section">
    <h2>点位</h2>
    <div class="chip-row mb-2">
      <UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="mode === 'regular'" :disabled="disabled" @click="mode = 'regular'">常态点位</UButton>
      <UButton type="button" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="mode === 'temporary'" :disabled="disabled" :data-testid="`${prefix}-temporary-mode`" @click="mode = 'temporary'">临时点位</UButton>
    </div>
    <template v-if="mode === 'regular'">
      <UFormField label="常态点位" name="pointId" required>
        <div v-if="selected && !expanded" class="flex min-w-0 items-center gap-2" :data-testid="`${prefix}-selected-point`">
          <span class="min-w-0 flex-1 truncate text-sm">{{ selected.name }} · {{ selected.communityName }} · {{ selected.typeName }}</span>
          <UButton label="更换" color="neutral" variant="link" :disabled="disabled" @click="expanded = true" />
        </div>
        <div v-else>
          <UInput v-model="search" type="search" icon="i-lucide-search" placeholder="搜索点位" aria-label="搜索常态点位" :data-testid="`${prefix}-point-search`" class="mb-2 w-full" :disabled="disabled" />
          <UButton v-for="point in points" :key="point.id" type="button" color="neutral" variant="ghost" class="h-14 w-full justify-start border-b border-[var(--divider)] text-left" :aria-pressed="pointId === point.id" :disabled="disabled" :data-testid="`${prefix}-point-option`" @click="choose(point)">
            <span class="min-w-0"><span class="block truncate font-medium">{{ point.name }}</span><span class="block truncate text-xs text-muted">{{ point.communityName }} · {{ point.typeName }}</span></span>
          </UButton>
          <p v-if="!searching && !points.length" class="py-3 text-sm text-muted">暂无常态点位</p>
          <UButton v-if="points.length < total" type="button" label="加载更多" color="neutral" variant="link" :disabled="disabled" :loading="searching" @click="list(false)" />
        </div>
      </UFormField>
      <p v-if="error" class="text-sm text-error" role="alert">{{ error }}</p>
    </template>
    <div v-else class="field-grid">
      <UFormField label="名称" name="newPoint.name" required class="field-full"><UInput v-model="newPoint.name" :maxlength="200" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-name`" /></UFormField>
      <UFormField label="社区" name="newPoint.communityId" required><USelect v-model="newPoint.communityId" :items="communities" placeholder="请选择" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-community`" /></UFormField>
      <UFormField label="类型" name="newPoint.typeId" required><USelect v-model="newPoint.typeId" :items="types" placeholder="请选择" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-type`" /></UFormField>
      <UFormField label="详细地址" name="newPoint.address" required class="field-full"><UInput v-model="newPoint.address" :maxlength="1000" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-address`" /></UFormField>
      <UFormField label="联系人" name="newPoint.contact"><NameTagsInput v-model="newPoint.contact" :maxlength="100" :disabled="disabled" :data-testid="`${prefix}-temporary-contact`" /></UFormField>
      <UFormField label="电话" name="newPoint.phone"><UInput v-model="newPoint.phone" type="tel" :maxlength="100" class="w-full" :disabled="disabled" /></UFormField>
      <UFormField label="经纬度" name="newPoint.coordinates" class="field-full"><template #label><CoordinateLabel /></template><UInput v-model="newPoint.coordinates" placeholder="88.893426,29.274331" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-coordinates`" /></UFormField>
    </div>
  </section>
</template>
''')
replace('web/app/types/api.ts','photos?: PhotoDto[] }','photos?: PhotoDto[]; campaigns?: CampaignDto[] }')
replace('web/app/types/api.ts',"export interface CampaignWriteRequest {\n  name: string; typeId: string; communityId: string; location: string; date: string; content: string", "export interface NewPointDraft { name: string; communityId: string; typeId: string; address: string; contact: string; phone: string; coordinates: string }\nexport interface CampaignWriteRequest {\n  name: string; typeId: string; pointId?: string | null; newPoint?: PointWriteRequest | null; date: string; content: string")
replace('web/app/types/api.ts','id: string; name: string; typeId: string; typeName: string; communityId: string; communityName: string; location: string; date: string', 'id: string; name: string; pointId: string; pointName: string; pointTemporary: boolean; typeId: string; typeName: string; communityId: string; communityName: string; location: string; date: string')
replace('web/app/types/api.ts','export interface CampaignQuery { q?: string;', 'export interface CampaignQuery { pointId?: string; q?: string;')
f=p/'web/app/assets/css/main.css'
f.write_text(f.read_text()+'''\n/* Person/unit inputs keep API strings while presenting editable tags. */
.name-tags { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px; max-width: 100%; vertical-align: middle; }
.name-tags [data-slot="base"] { max-width: 100%; overflow-wrap: anywhere; height: auto; min-height: 20px; white-space: normal; }
.names-field { min-width: 0; width: 100%; }
.names-editor { display: flex; flex-wrap: wrap; gap: 2px; align-items: center; min-height: var(--control-h); border: 1px solid var(--border-strong); border-radius: 6px; padding: 0 4px; background: var(--surface); }
.names-editor:focus-within { outline: 2px solid var(--brand-600); outline-offset: -1px; }
.names-disabled { opacity: .65; }
.names-input { flex: 1; min-width: 72px; }
.names-input input { width: 100%; padding-left: 4px; }
.name-edit-tag { display: inline-flex; align-items: center; max-width: 100%; }
.name-edit-tag [data-slot="base"] { overflow-wrap: anywhere; height: auto; white-space: normal; }
.name-remove { min-width: 26px; min-height: 28px; padding: 4px; }
.coordinate-label { display: inline-flex; align-items: center; white-space: nowrap; }
.coordinate-picker { min-height: 28px; padding: 0 4px; margin-left: 4px; }
.deadline-label { display: inline-flex; align-items: baseline; white-space: nowrap; gap: 6px; }
@media (max-width:767px) {
  .name-remove { min-width: 40px; min-height: 40px; }
  .coordinate-picker { min-width: 40px; min-height: 40px; margin-top: -8px; margin-bottom: -8px; }
}
''')
