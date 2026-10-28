<script setup lang="ts">
import type { PageDto, MatterLinkDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ matterId: string }>()
const emit = defineEmits<{ changed: [] }>()
const open = ref(false), kind = ref('inspection'), search = ref(''), selected = ref<string>(), busy = ref(false), saving = ref(false), error = ref('')
const options = ref<{ label: string; value: string }[]>([])
const route = computed(() => kind.value === 'activity' ? 'activities' : kind.value + 's')
const requestOwner = useLatestRequest()
let timer: ReturnType<typeof setTimeout> | undefined, ownerVersion = 0
async function load() {
  if (!open.value) return
  const request = requestOwner.start(), path = route.value
  busy.value = true; error.value = ''
  try {
    const result = await $fetch<PageDto<{ id: string; pointName?: string; description?: string; name?: string; date?: string; inspectionDate?: string }>>(`/api/${path}`, { signal: request.signal, query: { q: search.value, pageSize: 30 } })
    if (request.isCurrent()) options.value = result.items.map(item => ({ value: item.id, label: [item.name || item.pointName, item.date || item.inspectionDate, item.description].filter(Boolean).join(' · ') }))
  } catch (cause) { if (request.isCurrent()) error.value = errorMessage(cause) }
  finally { if (request.isCurrent()) busy.value = false }
}
watch([open, kind], () => { requestOwner.cancel(); clearTimeout(timer); selected.value = undefined; options.value = []; error.value = ''; busy.value = false; if (open.value) void load() })
watch(search, () => { requestOwner.cancel(); clearTimeout(timer); if (open.value) timer = setTimeout(load, 250) })
watch(() => props.matterId, () => { ownerVersion++; open.value = false; saving.value = false })
onBeforeUnmount(() => { ownerVersion++; clearTimeout(timer) })
async function save() {
  if (!selected.value || saving.value || busy.value) return
  const path = route.value, recordId = selected.value, recordKind = kind.value, matterId = props.matterId, version = ownerVersion
  saving.value = true; error.value = ''
  try {
    const detail = await $fetch<Record<string, { matters: MatterLinkDto[] }>>(`/api/${path}/${recordId}`)
    const record = detail[recordKind]; if (!record) throw new Error('记录读取失败')
    const matterIds = [...new Set([...record.matters.map(item => item.id), matterId])]
    await $fetch(`/api/${path}/${recordId}/matters`, { method: 'PUT', body: { matterIds }, retry: 0 })
    if (version === ownerVersion) { emit('changed'); open.value = false }
  } catch (cause) { if (version === ownerVersion) error.value = errorMessage(cause) }
  finally { if (version === ownerVersion) saving.value = false }
}
</script>
<template><UButton label="关联已有记录" color="neutral" variant="outline" @click="open = true" /><UModal v-model:open="open" title="关联已有记录" :dismissible="!saving" :close="!saving"><template #body><div class="space-y-3"><UAlert v-if="error" :description="error" color="error" /><UFormField label="记录类别"><USelect v-model="kind" :items="[{ label: '排查', value: 'inspection' }, { label: '整改问题', value: 'issue' }, { label: '活动', value: 'activity' }]" class="w-full" :disabled="saving" /></UFormField><UFormField label="记录"><USelectMenu v-model="selected" v-model:search-term="search" :items="options" value-key="value" ignore-filter :loading="busy" placeholder="搜索并选择记录" class="w-full" :disabled="saving" /></UFormField></div></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="open = false" /><UButton label="关联" :loading="saving" :disabled="!selected || busy" @click="save" /></template></UModal></template>
