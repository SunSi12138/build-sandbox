<script setup lang="ts">
withDefaults(defineProps<{ title: string; description?: string; icon?: string }>(), { icon: 'i-lucide-clipboard-list' })
</script>
<template><div class="empty-state"><UIcon :name="icon" class="size-8" /><h3>{{ title }}</h3><slot /></div></template>
