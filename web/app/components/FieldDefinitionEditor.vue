<script setup lang="ts">
import type { PurposeField } from '~/types/community'
import { clientId } from '~/utils/clientId'
const fields = defineModel<PurposeField[]>({ required: true })
defineProps<{ disabled?: boolean }>()
function addField() {
  fields.value.push({ id: clientId('field-'), label: '', type: 'text', required: false, multiline: false })
}
function move(index: number, offset: number) {
  const target = index + offset
  if (target < 0 || target >= fields.value.length) return
  const updated = [...fields.value]
  ;[updated[index], updated[target]] = [updated[target]!, updated[index]!]
  fields.value = updated
}
</script>
<template>
  <div class="space-y-3">
    <div class="flex justify-between items-center"><h3 class="font-semibold">扩展字段</h3><UButton label="添加字段" icon="i-lucide-plus" variant="outline" :disabled="disabled || fields.length >= 40" @click="addField" /></div>
    <div v-for="(field,index) in fields" :key="field.id" class="field-definition">
      <UInput v-model="field.label" placeholder="字段名称" aria-label="字段名称" class="min-w-0" :maxlength="60" :disabled="disabled" />
      <USelect v-model="field.type" :items="[{label:'文本',value:'text'},{label:'数字',value:'number'},{label:'图片',value:'image'},{label:'标签',value:'tags'}]" class="min-w-0" aria-label="字段类型" :disabled="disabled" @update:model-value="v=>{if(v!=='text')field.multiline=false}" />
      <UCheckbox v-if="field.type==='text'" v-model="field.multiline" label="多行" :disabled="disabled" />
      <UCheckbox v-model="field.required" label="必填" :disabled="disabled" />
      <div class="flex"><UButton icon="i-lucide-arrow-up" aria-label="字段上移" size="sm" color="neutral" variant="ghost" :disabled="disabled||index===0" @click="move(index,-1)" /><UButton icon="i-lucide-arrow-down" aria-label="字段下移" size="sm" color="neutral" variant="ghost" :disabled="disabled||index===fields.length-1" @click="move(index,1)" /><UButton icon="i-lucide-trash-2" aria-label="删除字段" size="sm" color="error" variant="ghost" :disabled="disabled" @click="fields.splice(index,1)" /></div>
    </div>
  </div>
</template>
<style scoped>
.field-definition{display:grid;grid-template-columns:minmax(120px,1fr) 105px auto auto auto;gap:8px;align-items:center}
@media(max-width:650px){.field-definition{grid-template-columns:minmax(0,1fr) 110px;}.field-definition>div:nth-child(n+3){justify-self:start}}
</style>
