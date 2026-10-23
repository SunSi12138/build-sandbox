<script setup lang="ts">
import { errorMessage, formatBytes } from '~/utils/format'
interface Preview { token: string; files: number; bytes: number; minZoom: number; maxZoom: number }
interface Job { id: string; state: string; copied: number; total: number; bytesCopied: number; totalBytes: number; error: string; layerId: string | null }
const emit = defineEmits<{ saved: [] }>()
const open = ref(false), source = ref(''), name = ref(''), scheme = ref('xyz'), error = ref(''), busy = ref(false), preview = ref<Preview | null>(null), job = ref<Job | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined
const running = computed(() => job.value?.state === 'copying')
watch(source, () => { preview.value = null })
async function scan() { busy.value = true; error.value = ''; preview.value = null; job.value = null; try { preview.value = await $fetch('/api/map/cache/preview', { method: 'POST', body: { source: source.value }, retry: 0 }) } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
async function poll() {
  if (!job.value) return
  try { job.value = await $fetch(`/api/map/cache/${job.value.id}`); if (job.value?.state === 'copying') timer = setTimeout(poll, 1000); else if (job.value?.state === 'complete') { localStorage.removeItem('community-cache-job'); emit('saved') } else if (job.value) error.value = job.value.error || '导入已取消' }
  catch (e) { error.value = errorMessage(e) }
}
async function start() { if (!preview.value) return; busy.value = true; error.value = ''; try { job.value = await $fetch('/api/map/cache/start', { method: 'POST', body: { token: preview.value.token, name: name.value, scheme: scheme.value }, retry: 0 }); localStorage.setItem('community-cache-job', job.value!.id); void poll() } catch (e) { error.value = errorMessage(e) } finally { busy.value = false } }
async function cancel() { try { if (job.value) await $fetch(`/api/map/cache/${job.value.id}/cancel`, { method: 'POST' }) } catch (e) { error.value = errorMessage(e) } }
onMounted(async () => { const id = localStorage.getItem('community-cache-job'); if (id) { try { job.value = await $fetch(`/api/map/cache/${id}`); if (running.value) { open.value = true; void poll() } } catch { localStorage.removeItem('community-cache-job') } } })
onBeforeUnmount(() => clearTimeout(timer))
</script>
<template><UButton label="导入地图缓存" icon="i-lucide-folder-open" variant="outline" @click="open = true" /><UModal v-model:open="open" title="导入地图缓存" :dismissible="!busy && !running" :close="!busy && !running"><template #body><div class="space-y-4"><UFormField label="电脑上的缓存文件夹"><UInput v-model="source" placeholder="例如 E:\Map\wwwroot\jlyh" class="w-full" :disabled="busy || running" /></UFormField><UButton label="读取文件夹" :loading="busy" :disabled="running || !source.trim()" @click="scan" /><template v-if="preview"><p>{{ preview.files }} 张 · {{ formatBytes(preview.bytes) }} · {{ preview.minZoom }}—{{ preview.maxZoom }} 级</p><UFormField label="图层名称"><UInput v-model="name" class="w-full" :disabled="busy || running" /></UFormField><UFormField label="缓存格式"><USelect v-model="scheme" :items="[{label:'普通地图（XYZ）',value:'xyz'},{label:'原Map吉林一号（TMS）',value:'tms'}]" class="w-full" :disabled="busy || running" /></UFormField><UButton label="复制并添加图层" :disabled="busy || running || !name.trim() || job?.state === 'complete'" @click="start" /></template><div v-if="job" class="space-y-2"><p>{{ job.state === 'complete' ? '导入完成' : job.state === 'copying' ? '正在复制' : '导入已停止' }} · {{ job.copied }}/{{ job.total }} 张</p><UProgress :model-value="job.copied" :max="job.total" /><UButton v-if="running" label="取消导入" variant="outline" color="neutral" @click="cancel" /><UButton v-if="job.state === 'complete'" to="/map" label="查看地图" /></div><UAlert v-if="error" :description="error" color="error" /></div></template></UModal></template>
