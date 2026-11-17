<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { CommunityDto, PointTypeDto, CampaignTypeDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

const props = defineProps<{ kind: 'communities' | 'point-types' | 'campaign-types'; items: CommunityDto[] | PointTypeDto[] | CampaignTypeDto[] }>()
const emit = defineEmits<{ changed: [] }>()
const toast = useToast()
const label = computed(() => props.kind === 'communities' ? '网格' : props.kind === 'point-types' ? '场所形态' : '整治类型')
const editOpen = ref(false)
const deleteOpen = ref(false)
const saving = ref(false)
const error = ref('')
const editingId = ref<string | null>(null)
const deleting = ref<CommunityDto | PointTypeDto | CampaignTypeDto | null>(null)
const form = reactive({ name: '', title: '', parentId: '', color: '#64748b', fields: [] as import('~/types/community').PurposeField[] })

function beginEdit(item?: CommunityDto | PointTypeDto | CampaignTypeDto) {
  editingId.value = item?.id ?? null
  form.color = item && 'color' in item ? item.color : '#64748b'
  form.parentId = item && 'parentId' in item ? item.parentId : ''
  form.name = item?.name ?? ''
  form.title = item && 'title' in item ? item.title : ''
  form.fields = item && 'fields' in item ? structuredClone(toRaw(item.fields ?? [])) : []
  error.value = ''
  editOpen.value = true
}

function beginDelete(item: CommunityDto | PointTypeDto | CampaignTypeDto) {
  deleting.value = item
  error.value = ''
  deleteOpen.value = true
}

function validate(state: typeof form): FormError[] {
  const errors: FormError[] = []
  if (!state.name.trim()) errors.push({ name: 'name', message: `请填写${label.value}名称。` })
  return errors
}

async function save() {
  if (saving.value) return
  saving.value = true
  error.value = ''
  try {
    const body = props.kind === 'communities' ? { name: form.name.trim(), title: form.title.trim() || form.name.trim(), parentId: form.parentId, color: form.color } : { name: form.name.trim(), fields: form.fields }
    await $fetch(`/api/${props.kind}${editingId.value ? `/${editingId.value}` : ''}`, { method: editingId.value ? 'PUT' : 'POST', body })
    editOpen.value = false
    emit('changed')
    toast.add({ title: '已保存', color: 'success', duration: 2000 })
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}

async function remove() {
  if (!deleting.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    await $fetch(`/api/${props.kind}/${deleting.value.id}`, { method: 'DELETE', query: { confirm: 'true' } })
    deleteOpen.value = false
    deleting.value = null
    emit('changed')
    toast.add({ title: '已删除', color: 'success', duration: 2000 })
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
</script>

<template>
  <section>
    <div class="section-heading"><h2 class="section-title">{{ label }}</h2><UButton color="neutral" variant="outline" @click="beginEdit()">新增</UButton></div>
    <ul v-if="items.length" class="group-list">
      <li v-for="item in items" :key="item.id" class="list-row">
        <div class="row-body"><p class="text-sm font-medium break-words">{{ item.name }}</p><p v-if="'title' in item" class="text-xs text-muted break-words">{{ item.title }}</p></div>
        <div class="flex shrink-0 items-center gap-1"><UButton icon="i-lucide-pencil" :aria-label="`编辑${item.name}`" color="neutral" variant="ghost" @click="beginEdit(item)" /><UButton icon="i-lucide-trash-2" :aria-label="`删除${item.name}`" color="error" variant="ghost" @click="beginDelete(item)" /></div>
      </li>
    </ul>
    <EmptyState v-else :title="`暂无${label}`" />
  </section>
  <UModal v-model:open="editOpen" :title="`${editingId ? '编辑' : '新增'}${label}`" :dismissible="!saving" :close="!saving">
    <template #body><UForm :id="`dictionary-${kind}-form`" :state="form" :validate="validate" class="space-y-3" @submit="save">
      <UAlert v-if="error" :description="error" color="error" variant="subtle" />
      <UFormField :label="`${label}名称`" name="name" required><UInput v-model="form.name" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
      <UFormField v-if="kind==='communities'" label="父网格"><GridTreeSelect v-model="form.parentId" allow-root :exclude-id="editingId??undefined" :disabled="saving" /></UFormField>
      <UFormField v-if="kind==='communities'" label="网格颜色"><MapColorPicker v-model="form.color" :disabled="saving" /></UFormField>
      <FieldDefinitionEditor v-if="kind!=='communities'" v-model="form.fields" :disabled="saving" />
    </UForm></template>
    <template #footer><UButton color="neutral" variant="outline" :disabled="saving" @click="editOpen = false">取消</UButton><UButton :form="`dictionary-${kind}-form`" type="submit" :loading="saving">保存</UButton></template>
  </UModal>
  <UModal v-model:open="deleteOpen" :title="`删除${label}「${deleting?.name ?? ''}」？`" description="删除后无法恢复" :dismissible="!saving" :close="!saving">
    <template #body><UAlert v-if="error" :description="error" color="error" variant="subtle" /></template>
    <template #footer><UButton color="neutral" variant="outline" :disabled="saving" @click="deleteOpen = false">取消</UButton><UButton color="error" :loading="saving" @click="remove">删除</UButton></template>
  </UModal>
</template>
