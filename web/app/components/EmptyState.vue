<script setup lang="ts">
withDefaults(defineProps<{ title: string; description?: string; icon?: string }>(), { icon: 'i-lucide-clipboard-list' })
</script>

<template>
  <div class="empty-state">
    <span class="mb-4 flex size-14 items-center justify-center rounded-2xl bg-elevated text-dimmed"><UIcon :name="icon" class="size-7" /></span>
    <h3 class="font-medium text-highlighted">{{ title }}</h3>
    <p v-if="description" class="mt-2 max-w-sm text-sm leading-6 text-muted">{{ description }}</p>
    <div v-if="$slots.default" class="mt-5"><slot /></div>
  </div>
</template>
