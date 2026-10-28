<script setup lang="ts">
const model = defineModel<string[]>({ default: () => [] })
const props = withDefaults(defineProps<{ disabled?: boolean; placeholder?: string }>(), { placeholder: '选择标签' })
const { bootstrap, load } = useAppData()
const items = computed(() => (bootstrap.value?.tags ?? []).map(tag => ({ label: tag.name + (tag.disabled ? '（已停用）' : ''), value: tag.id, disabled: tag.disabled && !model.value.includes(tag.id) })))
onMounted(() => { void load() })
</script>
<template><USelectMenu v-model="model" :items="items" value-key="value" multiple :placeholder="placeholder" :disabled="disabled" :search-input="{ placeholder: '搜索标签' }" class="w-full" aria-label="标签" /></template>
