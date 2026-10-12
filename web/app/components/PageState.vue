<script setup lang="ts">
defineProps<{ loading?: boolean; error?: string | null }>()
defineEmits<{ retry: [] }>()
</script>
<template>
  <div v-if="loading" class="group-list" role="status" aria-label="正在加载"><div v-for="row in 5" :key="row" class="list-row"><USkeleton class="h-10 w-full rounded" /></div></div>
  <UAlert v-else-if="error" :description="error" color="error" variant="subtle" icon="i-lucide-circle-alert"><template #actions><UButton label="重试" color="error" variant="outline" @click="$emit('retry')" /></template></UAlert>
  <slot v-else />
</template>
