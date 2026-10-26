<script setup lang="ts">
import type { CommunityRecord, UseData } from '~/types/community'
import { errorMessage } from '~/utils/format'
const props = defineProps<{ pointId: string }>()
const uses = ref<CommunityRecord<UseData>[]>([]), error = ref(''), showEnded = ref(false), open = ref(false), current = ref<CommunityRecord<UseData> | null>(null)
const { purpose, loadPurposes } = usePurposeCatalog()
const shown = computed(() => uses.value.filter(x => showEnded.value || (!x.archived && x.data.state !== '已结束')))
async function load() { try { await loadPurposes(); uses.value = await $fetch('/api/records/use', { query: { parentId: props.pointId, all: true } }) } catch(e) { error.value=errorMessage(e) } }
function choose(record: CommunityRecord<UseData> | null) { current.value=record; open.value=true }
watch(() => props.pointId,load,{immediate:true})
</script>
<template>
  <section class="panel"><div class="panel-head"><h2 class="section-title">使用情况</h2><UButton label="添加用途位置" icon="i-lucide-plus" variant="outline" @click="choose(null)" /></div><div class="panel-body"><UCheckbox v-model="showEnded" label="包括已结束" /><UAlert v-if="error" :description="error" color="error" /><button v-for="item in shown" :key="item.id" class="use-row" @click="choose(item)"><span class="text-xl">{{ purpose(item.data.purposeId)?.data.icon }}</span><span class="flex-1 text-left"><strong>{{ item.name }}</strong><span class="block text-sm text-muted">{{ item.data.position }} · {{ purpose(item.data.purposeId)?.name }} · {{ item.data.state }}</span></span><UIcon name="i-lucide-chevron-right" /></button><p v-if="!shown.length" class="py-4 text-muted">尚未登记使用情况，可按位置添加商店、仓库、出租房等。</p></div></section>
  <USlideover v-model:open="open" :title="current ? '使用情况' : '添加用途位置'" :ui="{content:'w-full max-w-[500px]'}"><template #body><UseDetailPanel v-if="open" :point-id="pointId" :record="current" @saved="open=false;load()" @cancel="open=false" /></template></USlideover>
</template>
<style scoped>.use-row{display:flex;align-items:center;gap:12px;width:100%;padding:14px 0;border-bottom:1px solid var(--border)}</style>
