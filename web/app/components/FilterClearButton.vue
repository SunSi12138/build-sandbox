<script setup lang="ts">
import type { RouteLocationRaw } from 'vue-router'

defineProps<{ active: boolean; to: RouteLocationRaw }>()
</script>
<template>
  <UButton
    :to="to"
    type="button"
    icon="i-lucide-filter-x"
    aria-label="清空筛选"
    title="清空筛选"
    color="neutral"
    variant="outline"
    class="filter-clear-action"
    :disabled="!active"
  />
</template>
