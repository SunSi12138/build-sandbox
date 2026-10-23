<script setup lang="ts">
import type { FormError } from '@nuxt/ui'
import type { CommunityDto, PointTypeDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

const props = defineProps<{ kind: 'communities' | 'point-types' | 'remediation-types' | 'issue-types' | 'activity-types'; items: CommunityDto[] | PointTypeDto[] }>()
const emit = defineEmits<{ changed: [] }>()
const toast = useToast()
const label = computed(() => props.kind === 'communities' ? '社区' : props.kind === 'point-types' ? '点位类型' : props.kind === 'remediation-types' ? '整治类别' : props.kind === 'issue-types' ? '问题类型' : '活动类型')
const editOpen = ref(false)
const deleteOpen = ref(false)
const saving = ref(false)
const error = ref('')
const editingId = ref<string | null>(null)
const deleting = ref<CommunityDto | PointTypeDto | null>(null)
const search = ref('')
const filtered = computed(() => props.items.filter(item => `${item.name} ${'title' in item ? item.title : ''}`.toLocaleLowerCase().includes(search.value.trim().toLocaleLowerCase())))
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
    <UInput v-if="items.length > 5" v-model="search" type="search" :placeholder="`搜索${label}`" :aria-label="`搜索${label}`" class="w-full mb-3" icon="i-lucide-search" />
    <ul v-if="filtered.length" class="group-list">
      <li v-for="item in filtered" :key="item.id" class="list-row">
        <div class="row-body"><p class="text-sm font-medium break-words">{{ item.name }}</p><p v-if="'title' in item" class="text-xs text-muted break-words">{{ item.title }}</p></div>
        <div class="flex shrink-0 items-center gap-1"><UButton icon="i-lucide-pencil" :aria-label="`编辑${item.name}`" color="neutral" variant="ghost" @click="beginEdit(item)" /><UButton icon="i-lucide-trash-2" :aria-label="`删除${item.name}`" :disabled="'builtIn' in item && !!item.builtIn" color="error" variant="ghost" @click="beginDelete(item)" /></div>
      </li>
    </ul>
    <EmptyState v-else :title="search ? '没有匹配结果' : `暂无${label}`" />
  </section>
  <UModal v-model:open="editOpen" :title="`${editingId ? '编辑' : '新增'}${label}`" :dismissible="!saving" :close="!saving">
    <template #body><UForm :id="`dictionary-${kind}-form`" :state="form" :validate="validate" class="space-y-3" @submit="save">
      <UAlert v-if="error" :description="error" color="error" variant="subtle" />
      <UFormField :label="`${label}名称`" name="name" required><UInput v-model="form.name" :maxlength="100" class="w-full" :disabled="saving" /></UFormField>
      <UFormField v-if="kind === 'communities'" label="通知单抬头全称" name="title" required><UInput v-model="form.title" :maxlength="150" class="w-full" :disabled="saving" /></UFormField>
    </UForm></template>
    <template #footer><UButton color="neutral" variant="outline" :disabled="saving" @click="editOpen = false">取消</UButton><UButton :form="`dictionary-${kind}-form`" type="submit" :loading="saving">保存</UButton></template>
  </UModal>
  <UModal v-model:open="deleteOpen" :title="`删除${label}「${deleting?.name ?? ''}」？`" description="删除后无法恢复" :dismissible="!saving" :close="!saving">
    <template #body><UAlert v-if="error" :description="error" color="error" variant="subtle" /></template>
    <template #footer><UButton color="neutral" variant="outline" :disabled="saving" @click="deleteOpen = false">取消</UButton><UButton color="error" :loading="saving" @click="remove">删除</UButton></template>
  </UModal>
</template>
