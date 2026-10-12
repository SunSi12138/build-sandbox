<script setup lang="ts">
import type { PhotoDto } from '~/types/api'
import { phaseLabel } from '~/utils/format'
const props = defineProps<{ photos: PhotoDto[]; emptyText?: string; label?: string }>()
const open = ref(false), index = ref(0)
const selected = computed(() => props.photos[index.value])
function preview(position: number) { index.value = position; open.value = true }
</script>
<template>
  <div v-if="photos.length" class="photo-grid"><button v-for="(photo, position) in photos" :key="photo.id" type="button" class="photo-button" :aria-label="`查看${label || phaseLabel(photo.phase)}照片 ${position + 1}`" @click="preview(position)"><img :src="`/photos/${photo.id}?size=thumb`" :alt="`${label || phaseLabel(photo.phase)}照片 ${position + 1}`" :width="photo.width" :height="photo.height" loading="lazy"></button></div>
  <p v-else class="text-xs text-muted">{{ emptyText || '暂无照片' }}</p>
  <UModal v-model:open="open" :title="`照片 ${index + 1}/${photos.length}`" :ui="{ content: 'max-w-4xl' }"><template #body><img v-if="selected" :src="`/photos/${selected.id}`" :alt="`${label || phaseLabel(selected.phase)}照片`" :width="selected.width" :height="selected.height" class="max-h-[70dvh] w-full object-contain"></template><template #footer><UButton icon="i-lucide-chevron-left" color="neutral" variant="outline" aria-label="上一张" :disabled="index === 0" @click="index--" /><UButton icon="i-lucide-chevron-right" color="neutral" variant="outline" aria-label="下一张" :disabled="index >= photos.length - 1" @click="index++" /></template></UModal>
</template>
