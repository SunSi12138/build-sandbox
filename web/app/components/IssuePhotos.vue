<script setup lang="ts">
import type { PhotoDto } from '~/types/api'
import { phaseLabel } from '~/utils/format'

defineProps<{ photos: PhotoDto[]; emptyText?: string }>()
const open = ref(false)
const selected = ref<PhotoDto | null>(null)
function preview(photo: PhotoDto) { selected.value = photo; open.value = true }
</script>

<template>
  <div v-if="photos.length" class="grid grid-cols-2 gap-2 sm:gap-3">
    <UButton v-for="(photo, index) in photos" :key="photo.id" color="neutral" variant="link" class="block aspect-[4/3] w-full overflow-hidden rounded-xl border border-default bg-elevated/40 p-0" :aria-label="`查看${phaseLabel(photo.phase)}照片 ${index + 1}`" @click="preview(photo)">
      <img :src="photo.url" :alt="`${phaseLabel(photo.phase)}照片 ${index + 1}`" :width="photo.width" :height="photo.height" loading="lazy" class="h-full w-full object-contain">
    </UButton>
  </div>
  <div v-else class="flex min-h-36 flex-col items-center justify-center gap-2 rounded-xl border border-dashed border-default bg-elevated/30 px-4 text-center text-sm text-muted">
    <UIcon name="i-lucide-image" class="size-6 text-dimmed" />
    <p>{{ emptyText || '暂无照片' }}</p>
  </div>
  <UModal v-model:open="open" :title="selected ? `${phaseLabel(selected.phase)}照片` : '照片预览'" description="查看完整照片" :ui="{ content: 'sm:max-w-5xl', body: 'p-2 sm:p-3' }">
    <template #body>
      <img v-if="selected" :src="selected.url" :alt="`${phaseLabel(selected.phase)}完整照片`" :width="selected.width" :height="selected.height" class="max-h-[75vh] w-full object-contain">
    </template>
  </UModal>
</template>
