<script setup lang="ts">
import type { TagDto } from '~/types/api'
withDefaults(defineProps<{ tags?: TagDto[]; limit?: number }>(), { tags: () => [], limit: 0 })
</script>
<template><div v-if="tags.length" class="flex flex-wrap gap-1"><NuxtLink v-for="tag in limit ? tags.slice(0, limit) : tags" :key="tag.id" :to="`/tags/${tag.id}`" class="filter-chip" :title="tag.disabled ? '已停用的标签' : undefined">{{ tag.name }}</NuxtLink><span v-if="limit && tags.length > limit" class="self-center text-xs text-muted">另 {{ tags.length - limit }} 个</span></div></template>
