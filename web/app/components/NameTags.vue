<script setup lang="ts">
import { splitNames } from '~/utils/names'
const props = withDefaults(defineProps<{ value?: string | null; empty?: string }>(), { empty: '—' })
const names = computed(() => splitNames(props.value ?? ''))
</script>

<template>
  <span v-if="names.length" class="name-tags" data-testid="name-tags">
    <template v-for="(name, index) in names" :key="index"><span v-if="index" class="sr-only"> </span><UBadge color="neutral" variant="subtle">{{ name }}</UBadge></template>
  </span>
  <span v-else>{{ empty }}</span>
</template>
