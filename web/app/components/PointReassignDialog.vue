<script setup lang="ts">
import type { PointDto } from '~/types/api'
import { errorMessage } from '~/utils/format'

const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{
  kind: 'inspection' | 'activity'
  recordId: string
  currentPointId: string
  currentPointName: string
  linkedIssueCount?: number
}>()
const emit = defineEmits<{ saved: [] }>()
const pointId = ref('')
const selected = ref<PointDto | null>(null)
const saving = ref(false)
const error = ref('')
const canSave = computed(() => pointId.value && selected.value?.id === pointId.value &&
  pointId.value !== props.currentPointId && !selected.value.disabled && !saving.value)

watch(open, value => {
  if (!value) return
  pointId.value = ''
  selected.value = null
  error.value = ''
})

async function save() {
  if (!canSave.value) return
  saving.value = true
  error.value = ''
  try {
    const collection = props.kind === 'inspection' ? 'inspections' : 'activities'
    await $fetch(`/api/${collection}/${props.recordId}/point`, {
      method: 'POST',
      body: { pointId: pointId.value },
      retry: 0,
    })
    open.value = false
    emit('saved')
  } catch (cause) {
    error.value = errorMessage(cause)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UModal v-model:open="open" title="更换关联点位" description="仅调整本条记录所属的点位，不会合并或删除点位。" :dismissible="!saving" :close="!saving" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <div class="text-sm">
          <p class="text-muted">当前点位</p>
          <p class="font-medium">{{ currentPointName }}</p>
        </div>
        <UFormField label="选择正确点位" required>
          <PointSelect v-model="pointId" active-only :disabled="saving" @selected="selected = $event" />
        </UFormField>
        <div v-if="selected && pointId !== currentPointId" class="rounded-lg border border-[var(--divider)] p-3 text-sm">
          <p class="text-muted">更换后</p>
          <p class="font-medium">{{ selected.name }}</p>
          <p class="text-muted">{{ selected.communityName }} · {{ selected.address }}</p>
        </div>
        <p class="text-xs text-muted">
          原有问题、整改反馈、复查、照片、标签和关联事项全部保留。
          记录的点位名称、社区和地址将更新为目标点位；原现场位置不自动改写。
          旧点位不会自动删除。
        </p>
        <p v-if="linkedIssueCount" class="text-xs text-muted">
          此记录涉及 {{ linkedIssueCount }} 项问题。其他记录的关联点位不会随本次操作改变，请按需逐条核对。
        </p>
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
      </div>
    </template>
    <template #footer>
      <UButton type="button" label="取消" color="neutral" variant="outline" :disabled="saving" @click="open = false" />
      <UButton type="button" label="确认更换点位" :loading="saving" :disabled="!canSave" @click="save" />
    </template>
  </UModal>
</template>
