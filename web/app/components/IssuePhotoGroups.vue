<script setup lang="ts">
import type { PhotoDto } from '~/types/api'
defineProps<{ photos: PhotoDto[] }>()
const active = ref('site')
const phases = [{ value: 'site', label: '整改前' }, { value: 'after', label: '整改后' }, { value: 'review', label: '复查' }]
</script>
<template><div><div class="chip-row mobile-only mb-2"><UButton v-for="phase in phases" :key="phase.value" color="neutral" variant="ghost" class="filter-chip" :aria-pressed="active === phase.value" @click="active = phase.value">{{ phase.label }}</UButton></div><div class="issue-photos"><section v-for="phase in phases" :key="phase.value" :class="{ 'photo-phase-hidden': active !== phase.value }"><h4 class="issue-photo-head desktop-only">{{ phase.label }}</h4><IssuePhotos :photos="photos.filter(photo => photo.phase === phase.value)" /></section></div></div></template>
