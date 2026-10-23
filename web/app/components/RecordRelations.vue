<script setup lang="ts">
import type { MatterLinkDto, TagDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ kind: 'point' | 'inspection' | 'issue' | 'activity' | 'matter'; id: string; tags?: TagDto[]; matters?: MatterLinkDto[] }>()
const emit = defineEmits<{ changed: [] }>()
const open = ref(false), saving = ref(false), error = ref(''), tagIds = ref<string[]>([]), matterIds = ref<string[]>([])
const canTag = computed(() => props.kind !== 'issue')
const canMatter = computed(() => ['inspection', 'issue', 'activity'].includes(props.kind))
watch(open, value => { if (value) { error.value = ''; tagIds.value = props.tags?.map(tag => tag.id) ?? []; matterIds.value = props.matters?.map(matter => matter.id) ?? [] } })
async function save() {
  if (saving.value) return
  saving.value = true; error.value = ''
  const route = props.kind === 'activity' ? 'activities' : props.kind + 's'
  try {
    await $fetch(`/api/${route}/${props.id}/relations`, { method: 'PUT', body: { tagIds: canTag.value ? tagIds.value : undefined, matterIds: canMatter.value ? matterIds.value : undefined }, retry: 0 })
    emit('changed'); open.value = false
  } catch (cause) { error.value = errorMessage(cause); emit('changed') }
  finally { saving.value = false }
}
</script>
<template><section class="panel"><div class="panel-head"><h2 class="section-title">{{ canMatter ? canTag ? '来源与标签' : '关联事项' : '标签' }}</h2><UButton label="编辑关联" color="neutral" variant="link" @click="open = true" /></div><div class="panel-body space-y-2"><TagBadges v-if="canTag" :tags="tags" /><MatterLinks v-if="canMatter" :matters="matters ?? []" /><p v-if="!canMatter && !tags?.length" class="text-sm text-muted">暂无标签</p></div></section>
<UModal v-model:open="open" title="编辑关联" :dismissible="!saving" :close="!saving"><template #body><div class="space-y-3"><UAlert role="alert" v-if="error" :description="error" color="error" /><UFormField v-if="canTag" label="标签"><TagPicker v-model="tagIds" :disabled="saving" /></UFormField><UFormField v-if="canMatter" label="关联事项"><MatterPicker v-model="matterIds" :selected="matters" :disabled="saving" /></UFormField></div></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="open = false" /><UButton label="保存" :loading="saving" @click="save" /></template></UModal></template>
