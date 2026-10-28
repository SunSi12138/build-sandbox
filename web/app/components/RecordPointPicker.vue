<script setup lang="ts">
import type { PageDto, PointDetailDto, PointDto, NewPointDraft } from '~/types/api'
import { errorMessage } from '~/utils/format'
const mode = defineModel<'regular' | 'temporary'>('mode', { required: true })
const pointId = defineModel<string>('pointId', { required: true })
const newPoint = defineModel<NewPointDraft>('newPoint', { required: true })
const props = withDefaults(defineProps<{ disabled?: boolean; prefix?: string }>(), { prefix: 'activity' })
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
      <UFormField label="经纬度" name="newPoint.coordinates" class="field-full"><template #label><CoordinateLabel /></template><UInput v-model="newPoint.coordinates" aria-label="经纬度" placeholder="88.893426,29.274331" class="w-full" :disabled="disabled" :data-testid="`${prefix}-temporary-coordinates`" /></UFormField>
    </div>
  </section>
</template>
