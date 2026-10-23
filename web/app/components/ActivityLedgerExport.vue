<script setup lang="ts">
import { downloadWord } from '~/utils/wordDownload'
import { errorMessage, todayLocal } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ communityId?: string; typeId?: string }>()
const { bootstrap, load } = useAppData()
const from = ref('')
const to = ref('')
const community = ref('all')
const type = ref('all')
const includePhotos = ref(false)
const exporting = ref(false)
const error = ref('')
const communities = computed(() => [{ label: '全部社区', value: 'all' }, ...(bootstrap.value?.communities ?? []).map(c => ({ label: c.name, value: c.id }))])
const types = computed(() => [{ label: '全部类型', value: 'all' }, ...(bootstrap.value?.activityTypes ?? []).map(t => ({ label: t.name, value: t.id }))])
watch(open, async value => {
  if (!value) return
  error.value = ''
  community.value = props.communityId || 'all'
  type.value = props.typeId || 'all'
  includePhotos.value = false
  try {
    await load()
    const today = bootstrap.value?.today || todayLocal()
    from.value = today.slice(0, 7) + '-01'
    to.value = today
  } catch (cause) { error.value = errorMessage(cause) }
})
async function exportWord() {
  if (exporting.value) return
  if (!from.value || !to.value || from.value > to.value) { error.value = '请选择正确的日期范围'; return }
  exporting.value = true
  error.value = ''
  const query = new URLSearchParams({ from: from.value, to: to.value, photos: String(includePhotos.value) })
  if (community.value !== 'all') query.set('communityId', community.value)
  if (type.value !== 'all') query.set('typeId', type.value)
  try { await downloadWord(`/api/exports/activity-ledger?${query}`, '活动开展情况台账.docx'); open.value = false }
  catch (cause) { error.value = errorMessage(cause) }
  finally { exporting.value = false }
}
</script>
<template>
  <UModal v-model:open="open" title="导出活动台账" :dismissible="!exporting" :close="!exporting" :ui="{ content: 'mobile-dialog max-w-[560px]' }">
    <template #body>
      <div class="space-y-3">
        <UAlert v-if="error" :description="error" color="error" variant="subtle" />
        <div class="field-grid">
          <UFormField label="开始日期"><UInput v-model="from" type="date" :max="to || undefined" class="w-full" :disabled="exporting" /></UFormField>
          <UFormField label="结束日期"><UInput v-model="to" type="date" :min="from || undefined" class="w-full" :disabled="exporting" /></UFormField>
          <UFormField label="所属社区"><USelect v-model="community" :items="communities" class="w-full" :disabled="exporting" /></UFormField>
          <UFormField label="活动类型"><USelect v-model="type" :items="types" class="w-full" :disabled="exporting" /></UFormField>
        </div>
        <UCheckbox v-model="includePhotos" label="附活动照片" :disabled="exporting" />
      </div>
    </template>
    <template #footer>
      <UButton label="取消" color="neutral" variant="outline" :disabled="exporting" @click="open = false" />
      <UButton label="导出 DOCX" :loading="exporting" data-testid="activity-ledger-export" @click="exportWord" />
    </template>
  </UModal>
</template>
