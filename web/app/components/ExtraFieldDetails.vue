<script setup lang="ts">
import type { PurposeField } from '~/types/community'
defineProps<{ fields?: PurposeField[]; values?: Record<string, string | number> }>()
</script>
<template>
  <template v-for="field in (fields ?? [])" :key="field.id">
    <div v-if="values?.[field.id] !== undefined && values?.[field.id] !== ''">
      <dt>{{ field.label }}</dt>
      <dd v-if="field.type==='image'"><a :href="`/photos/${values![field.id]}`" target="_blank" rel="noopener"><img :src="`/photos/${values![field.id]}?size=thumb`" class="max-h-36 rounded" :alt="field.label" /></a></dd>
      <dd v-else-if="field.type==='tags'"><NameTags :value="String(values?.[field.id]??'')" /></dd>
      <dd v-else :class="{ 'whitespace-pre-wrap': field.multiline }">{{ values?.[field.id] }}</dd>
    </div>
  </template>
</template>