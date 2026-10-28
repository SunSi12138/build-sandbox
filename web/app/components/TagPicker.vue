<script setup lang="ts">
import { errorMessage } from '~/utils/format'
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ disabled?: boolean; placeholder?: string; mode?: 'edit' | 'filter' }>(), { placeholder: '选择标签', mode: 'edit' })
const { bootstrap, load } = useAppData()
const error = ref('')
const items = computed(() => (bootstrap.value?.tags ?? []).map(tag => ({ label: tag.name + (tag.disabled ? '（已停用）' : ''), value: tag.id, disabled: props.mode === 'edit' && tag.disabled && !model.value.includes(tag.id) })))
onMounted(() => { void load().catch(cause => { error.value = errorMessage(cause) }) })
</script>
<template><div><USelectMenu v-model="model" :items="items" value-key="value" multiple :placeholder="placeholder" :disabled="disabled" :search-input="{ placeholder: '搜索标签' }" class="w-full" :aria-label="mode === 'filter' ? '标签筛选' : '标签'" /><p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p></div></template>
