<script setup lang="ts">
import type { Frequency } from '~/types/api'
import { errorMessage, frequencyOptions } from '~/utils/format'
const open = defineModel<boolean>('open', { default: false })
const props = defineProps<{ pointId: string }>()
const emit = defineEmits<{ saved: [] }>()
const frequency = ref<Frequency>('irregular'), saving = ref(false), error = ref('')
watch(open, value => { if (value) { frequency.value = 'irregular'; error.value = '' } })
async function save() { saving.value = true; error.value = ''; try { await $fetch(`/api/points/${props.pointId}/promote`, { method: 'POST', body: { frequency: frequency.value }, retry: 0 }); open.value = false; emit('saved') } catch (value) { error.value = errorMessage(value) } finally { saving.value = false } }
</script>
<template><UModal v-model:open="open" title="转为常态点位" :dismissible="!saving" :close="!saving"><template #body><UFormField label="排查频次"><USelect v-model="frequency" :items="frequencyOptions" class="w-full" /></UFormField><UAlert v-if="error" :description="error" color="error" variant="subtle" class="mt-3" /></template><template #footer><UButton label="取消" color="neutral" variant="outline" :disabled="saving" @click="open = false" /><UButton label="转为常态" :loading="saving" data-testid="promote-confirm" @click="save" /></template></UModal></template>
