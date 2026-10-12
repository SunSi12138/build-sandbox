<script setup lang="ts">
withDefaults(defineProps<{ tone?: 'warning' | 'info' | 'success' | 'danger' | 'neutral' | 'outline' }>(), { tone: 'neutral' })
</script>
<template><UBadge color="neutral" variant="soft" class="status-badge" :data-tone="tone"><slot /></UBadge></template>
