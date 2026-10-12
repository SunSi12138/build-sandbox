<script setup lang="ts">
defineProps<{ title: string; description?: string; eyebrow?: string; back?: string }>()
</script>
<template>
  <header class="page-header">
    <div class="page-title-wrap" :class="{ 'has-back': back }">
      <UButton v-if="back" :to="back" icon="i-lucide-chevron-left" color="neutral" variant="link" class="page-back" aria-label="返回"><span class="page-back-label">返回</span></UButton>
      <h1>{{ title }}</h1><slot name="badge" />
    </div>
    <div v-if="$slots.actions" class="page-actions"><slot name="actions" /></div>
  </header>
</template>
