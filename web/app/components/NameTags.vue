<script setup lang="ts">
import { splitNames } from '~/utils/names'
const props = withDefaults(defineProps<{ value?: string | null; empty?: string; limit?: number }>(), { empty: '—', limit: 0 })
const names = computed(() => splitNames(props.value ?? ''))
const visible = computed(() => props.limit ? names.value.slice(0, props.limit) : names.value)
const remaining = computed(() => names.value.length - visible.value.length)
</script>

<template>
  <span v-if="names.length" class="name-text" data-testid="name-tags" :title="names.join('、')">
    {{ visible.join('、') }}<span v-if="remaining" class="text-muted"> 等 {{ names.length }} 人</span>
  </span>
  <span v-else>{{ empty }}</span>
</template>
