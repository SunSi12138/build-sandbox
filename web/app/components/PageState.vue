<script setup lang="ts">
defineProps<{ loading?: boolean; error?: string | null }>()
defineEmits<{ retry: [] }>()
</script>

<template>
  <div v-if="loading" role="status" aria-live="polite" aria-label="正在加载" class="space-y-4">
    <USkeleton class="h-24 w-full rounded-2xl" />
    <USkeleton class="h-56 w-full rounded-2xl" />
    <span class="sr-only">正在加载，请稍候</span>
  </div>
  <UAlert v-else-if="error" color="error" variant="subtle" icon="i-lucide-circle-alert" title="暂时无法加载" :description="error">
    <template #actions><UButton color="error" variant="soft" icon="i-lucide-refresh-cw" label="重试" @click="$emit('retry')" /></template>
  </UAlert>
  <slot v-else />
</template>
