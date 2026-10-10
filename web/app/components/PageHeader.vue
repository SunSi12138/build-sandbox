<script setup lang="ts">
defineProps<{ title: string; description?: string; eyebrow?: string; back?: string }>()
</script>

<template>
  <header class="page-heading">
    <div class="min-w-0">
      <UButton v-if="back" :to="back" icon="i-lucide-arrow-left" label="返回" color="neutral" variant="link" class="mb-1 -ml-2" />
      <p v-if="eyebrow" class="mb-2 text-xs font-semibold tracking-wider text-primary">{{ eyebrow }}</p>
      <h1 class="text-2xl font-semibold tracking-tight text-highlighted md:text-3xl">{{ title }}</h1>
      <p v-if="description" class="mt-2 max-w-2xl text-sm leading-6 text-muted">{{ description }}</p>
    </div>
    <div v-if="$slots.actions" class="page-actions"><slot name="actions" /></div>
  </header>
</template>
