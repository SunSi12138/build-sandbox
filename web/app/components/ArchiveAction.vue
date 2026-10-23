<script setup lang="ts">
import { errorMessage } from '~/utils/format'
const props = defineProps<{ kind: 'inspection' | 'issue'; recordId: string; revision: number; voided: boolean }>()
const emit = defineEmits<{ saved: [] }>()
const open = ref(false), reason = ref(''), busy = ref(false), error = ref(''), version = ref(0)
const label = computed(() => `${props.voided ? '恢复' : '作废'}${props.kind === 'inspection' ? '检查' : '问题'}`)
function show() { reason.value = ''; error.value = ''; version.value = props.revision; open.value = true }
async function save() { if (!reason.value.trim()) return; busy.value = true; error.value = ''; try { await $fetch(`/api/archive/${props.kind}/${props.recordId}`, { method: 'PUT', body: { voided: !props.voided, revision: version.value, reason: reason.value }, retry: 0 }); open.value = false; emit('saved') } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
</script>
<template><UButton :label="label" color="neutral" variant="outline" @click="show" /><UModal v-model:open="open" :title="label" :dismissible="!busy"><template #body><div class="space-y-3"><p class="text-sm">{{ voided ? '恢复后重新纳入统计和待办。' : '原记录和照片保留，可在作废记录中恢复。' }}</p><UFormField :label="voided ? '恢复原因' : '作废原因'" required><UTextarea v-model="reason" class="w-full" :disabled="busy" /></UFormField><UAlert v-if="error" :description="error" color="error" /></div></template><template #footer><UButton label="取消" variant="outline" color="neutral" :disabled="busy" @click="open = false" /><UButton :label="label" :disabled="!reason.trim()" :loading="busy" @click="save" /></template></UModal></template>
