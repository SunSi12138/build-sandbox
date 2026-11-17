<script setup lang="ts">
import type { PageDto, PointDto } from '~/types/api'
import { errorMessage } from '~/utils/format'
interface MergePreview { source: PointDto; target: PointDto; inspections: number; uses: number; units: number; photos: number; campaigns: number; plans: number }
const sourceId = ref(''), targetId = ref(''), reason = ref('')
const points = ref<PointDto[]>([]), merge = ref<MergePreview | null>(null)
const busy = ref(false), error = ref(''), confirm = ref(false)
const pointChoices = computed(() => points.value.map(x => ({ label: `${x.name} · ${x.address}${x.disabled ? '（已停用）' : ''}`, value: x.id })))
watch([sourceId, targetId], () => { merge.value = null; confirm.value = false; error.value = '' })
async function load() {
  try {
    const data: PointDto[] = []
    for (let page = 1;; page++) {
      const result = await $fetch<PageDto<PointDto>>('/api/points', { query: { page, pageSize: 100, disabled: 'all', temporary: 'all' } })
      data.push(...result.items)
      if (page * result.pageSize >= result.total) break
    }
    points.value = data
  } catch (e) { error.value = errorMessage(e) }
}
async function preview() {
  busy.value = true; error.value = ''; merge.value = null; confirm.value = false
  try { merge.value = await $fetch('/api/management/merge', { query: { sourceId: sourceId.value, targetId: targetId.value } }) }
  catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
async function apply() {
  if (!reason.value.trim() || !merge.value) { error.value = '请填写办理原因'; return }
  busy.value = true; error.value = ''
  try {
    const result = await $fetch<PointDto>('/api/management/merge', { method: 'POST', body: { sourceId: sourceId.value, targetId: targetId.value, sourceRevision: merge.value.source.revision, targetRevision: merge.value.target.revision, reason: reason.value }, retry: 0 })
    await navigateTo(`/points/${result.id}`)
  } catch (e) { error.value = errorMessage(e) } finally { busy.value = false }
}
onMounted(load)
</script>
<template>
  <div class="page-stack">
    <PageHeader title="合并重复场所" back="/settings" />
    <section class="panel panel-body space-y-4">
      <div class="field-grid"><UFormField label="重复登记的场所"><USelectMenu v-model="sourceId" :items="pointChoices" value-key="value" placeholder="选择要并走的场所" class="w-full" :disabled="busy" /></UFormField><UFormField label="保留的场所"><USelectMenu v-model="targetId" :items="pointChoices.filter(x => x.value !== sourceId)" value-key="value" placeholder="资料归入这里" class="w-full" :disabled="busy" /></UFormField></div>
      <UButton label="查看涉及的资料" :loading="busy" :disabled="!sourceId || !targetId" @click="preview" />
    </section>
    <UAlert v-if="error" :description="error" color="error" />
    <section v-if="merge" class="panel panel-body space-y-3"><h2 class="section-title">{{ merge.source.name }} → {{ merge.target.name }}</h2><p>转入 {{ merge.inspections }} 次检查、{{ merge.uses }} 条使用记录、{{ merge.units }} 个内部单元、{{ merge.photos }} 张场所照片、{{ merge.campaigns }} 条整治记录。</p><p>同步调整 {{ merge.plans }} 项检查安排。</p><p class="text-sm text-muted">保留场所的名称、地址和地图位置不变。原场所停用，历史检查中的原名称保留。</p></section>
    <section v-if="merge" class="panel panel-body space-y-4"><UFormField label="办理原因" required><UTextarea v-model="reason" class="w-full" :disabled="busy" /></UFormField><UCheckbox v-model="confirm" label="已核对，确认是同一处场所" :disabled="busy" /><UButton label="确认合并" :disabled="busy || !confirm || !reason.trim()" :loading="busy" @click="apply" /></section>
  </div>
</template>