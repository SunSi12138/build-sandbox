<script setup lang="ts">
import type { PointDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ items: PointDto[] }>()
const emit = defineEmits<{ deleted: [] }>()
const toast = useToast()
const selected = ref<Record<string, PointDto>>({})
const selectedItems = computed(() => Object.values(selected.value))
const dialogOpen = ref(false), busy = ref(false), error = ref('')
const deletable = (point: PointDto) => point.inspectionCount === 0 && point.activityCount === 0 && point.matterCount === 0 && point.attachmentCount === 0
const photoCount = computed(() => selectedItems.value.reduce((total, point) => total + point.photoCount, 0))
function toggle(point: PointDto, value: boolean) {
  if (!deletable(point) || busy.value) return
  if (value) {
    if (selectedItems.value.length < 100) selected.value[point.id] = point
  } else delete selected.value[point.id]
}
function selectPage() {
  for (const point of props.items) {
    if (selectedItems.value.length >= 100) break
    if (deletable(point)) selected.value[point.id] = point
  }
}
async function remove() {
  if (!selectedItems.value.length || busy.value) return
  busy.value = true; error.value = ''
  const ids = selectedItems.value.map(point => point.id)
  try {
    await $fetch('/api/points/batch-delete', { method: 'POST', query: { confirm: true }, body: { ids }, retry: 0 })
    selected.value = {}; dialogOpen.value = false
    toast.add({ title: `已删除 ${ids.length} 个点位`, color: 'success' })
    emit('deleted')
  } catch (cause) { error.value = errorMessage(cause) }
  finally { busy.value = false }
}
</script>
<template>
  <section class="panel space-y-3 p-3" aria-label="点位批量管理">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <span class="text-sm">已选择 {{ selectedItems.length }} 个可删除点位（最多 100 个）</span>
      <div class="flex flex-wrap gap-2">
        <UButton type="button" variant="outline" color="neutral" label="选择本页可删除点位" :disabled="busy" @click="selectPage" />
        <UButton type="button" variant="ghost" color="neutral" label="清空选择" :disabled="busy || !selectedItems.length" @click="selected = {}" />
        <UButton type="button" color="error" label="删除所选" :disabled="busy || !selectedItems.length" @click="error = ''; dialogOpen = true" />
      </div>
    </div>
    <p class="text-xs text-muted">显示当前页点位的关联数据量；可跨页选择。有关联业务记录或附件的点位禁止删除；点位本身的照片会随点位删除。</p>
    <div v-for="point in items" :key="point.id" class="flex items-center gap-3 border-b border-[var(--divider)] py-3">
      <UCheckbox :model-value="Boolean(selected[point.id])" :disabled="busy || !deletable(point)" :aria-label="`选择点位 ${point.name}`" @update:model-value="value => toggle(point, Boolean(value))" />
      <div class="min-w-0 flex-1">
        <NuxtLink :to="`/points/${point.id}`" class="text-sm font-medium text-primary">{{ point.name }}</NuxtLink>
        <p class="text-xs text-muted">{{ point.communityName }} · 排查 {{ point.inspectionCount }} · 问题 {{ point.issueCount }} · 活动 {{ point.activityCount }} · 事项 {{ point.matterCount }} · 附件 {{ point.attachmentCount }} · 点位照片 {{ point.photoCount }}</p>
        <p v-if="!deletable(point)" class="text-xs text-warning">存在关联业务记录或附件，不可直接删除</p>
      </div>
    </div>
    <UModal v-model:open="dialogOpen" :title="`确认删除 ${selectedItems.length} 个点位？`" :description="`所选点位及其 ${photoCount} 张点位照片将永久删除；业务关联点位不可删除。全部检查通过才会执行，任意失败则整批不删除。`" :dismissible="!busy" :close="!busy">
      <template #body><UAlert v-if="error" :description="error" color="error" variant="subtle" /></template>
      <template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="dialogOpen = false" /><UButton label="确认永久删除" color="error" :loading="busy" @click="remove" /></template>
    </UModal>
  </section>
</template>
