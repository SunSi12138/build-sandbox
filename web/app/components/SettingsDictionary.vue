<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { CommunityDto, PointTypeDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

const props = defineProps<{ kind: 'communities' | 'point-types'; items: CommunityDto[] | PointTypeDto[] }>()
const emit = defineEmits<{ changed: [] }>()
const toast = useToast()
const label = computed(() => props.kind === 'communities' ? '社区' : '点位类型')
const editOpen = ref(false)
const deleteOpen = ref(false)
const saving = ref(false)
const error = ref('')
const editingId = ref<string | null>(null)
const deleting = ref<CommunityDto | PointTypeDto | null>(null)
const form = reactive({ name: '', title: '' })

function beginEdit(item?: CommunityDto | PointTypeDto) {
  editingId.value = item?.id ?? null
  form.name = item?.name ?? ''
  form.title = item && 'title' in item ? item.title : ''
  error.value = ''
  editOpen.value = true
}

function beginDelete(item: CommunityDto | PointTypeDto) {
  deleting.value = item
  error.value = ''
  deleteOpen.value = true
}

function validate(state: typeof form): FormError[] {
  const errors: FormError[] = []
  if (!state.name.trim()) errors.push({ name: 'name', message: `请填写${label.value}名称。` })
  if (props.kind === 'communities' && !state.title.trim()) errors.push({ name: 'title', message: '请填写通知单抬头全称。' })
  return errors
}

async function save() {
  if (saving.value) return
  saving.value = true
  error.value = ''
  try {
    const body = props.kind === 'communities' ? { name: form.name.trim(), title: form.title.trim() } : { name: form.name.trim() }
    await $fetch(`/api/${props.kind}${editingId.value ? `/${editingId.value}` : ''}`, { method: editingId.value ? 'PUT' : 'POST', body })
    editOpen.value = false
    emit('changed')
    toast.add({ title: `${label.value}已保存`, color: 'success' })
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
    toast.add({ title: `${label.value}已删除`, color: 'success' })
  } catch (cause) { error.value = errorMessage(cause) }
  finally { saving.value = false }
}
</script>

<template>
  <UCard>
    <template #header>
      <div class="flex items-center justify-between gap-3"><div><h2 class="font-semibold text-highlighted">{{ label }}管理</h2><p class="mt-1 text-sm text-muted">{{ kind === 'communities' ? '通知单会使用社区的抬头全称。' : '维护现场排查时使用的分类。' }}</p></div><UButton icon="i-lucide-plus" variant="soft" class="min-h-12 shrink-0" @click="beginEdit()">新增</UButton></div>
    </template>
    <ul v-if="items.length" class="divide-y divide-default">
      <li v-for="item in items" :key="item.id" class="flex items-center justify-between gap-3 py-3 first:pt-0 last:pb-0">
        <div class="min-w-0"><p class="break-words font-medium text-highlighted">{{ item.name }}</p><p v-if="'title' in item" class="mt-1 break-words text-xs leading-5 text-muted">{{ item.title }}</p></div>
        <div class="flex shrink-0 items-center gap-1"><UButton icon="i-lucide-pencil" :aria-label="`编辑${item.name}`" color="neutral" variant="ghost" class="min-h-12 min-w-12 justify-center" @click="beginEdit(item)" /><UButton icon="i-lucide-trash-2" :aria-label="`删除${item.name}`" color="error" variant="ghost" class="min-h-12 min-w-12 justify-center" @click="beginDelete(item)" /></div>
      </li>
    </ul>
    <div v-else class="py-7 text-center text-sm text-muted"><p>还没有{{ label }}。</p><UButton variant="ghost" class="mt-3 min-h-12" @click="beginEdit()">添加第一个{{ label }}</UButton></div>
  </UCard>

  <UModal v-model:open="editOpen" :title="`${editingId ? '编辑' : '新增'}${label}`" :description="kind === 'communities' ? '名称用于日常筛选，抬头全称用于整改通知单。' : '保存后可在点位管理中选择此类型。'" :dismissible="!saving" :close="!saving">
    <template #body><UForm :id="`dictionary-${kind}-form`" :state="form" :validate="validate" class="space-y-5" @submit="save">
      <UAlert v-if="error" :description="error" color="error" variant="subtle" />
      <UFormField :label="`${label}名称`" name="name" required><UInput v-model="form.name" :maxlength="100" :placeholder="kind === 'communities' ? '例如：东苑社区' : '例如：餐饮油烟'" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
      <UFormField v-if="kind === 'communities'" label="通知单抬头全称" name="title" required><UInput v-model="form.title" :maxlength="150" placeholder="例如：东苑社区居民委员会" size="xl" class="w-full" :ui="{ base: 'min-h-12' }" :disabled="saving" /></UFormField>
    </UForm></template>
    <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving" @click="editOpen = false">取消</UButton><UButton :form="`dictionary-${kind}-form`" type="submit" size="xl" class="min-h-12" :loading="saving">保存</UButton></div></template>
  </UModal>

  <UModal v-model:open="deleteOpen" :title="`确认删除${label}？`" :description="`将删除“${deleting?.name ?? ''}”。已被点位或历史记录使用的${label}会受到保护，无法删除。`" :dismissible="!saving" :close="!saving">
    <template #body><UAlert v-if="error" :description="error" color="error" variant="subtle" /><p v-else class="text-sm leading-6 text-muted">请核对名称后再确认。删除成功后，这个选项将从列表中移除。</p></template>
    <template #footer><div class="flex w-full justify-end gap-3"><UButton color="neutral" variant="outline" size="xl" class="min-h-12" :disabled="saving" @click="deleteOpen = false">取消</UButton><UButton color="error" size="xl" class="min-h-12" :loading="saving" @click="remove">确认删除</UButton></div></template>
  </UModal>
</template>
