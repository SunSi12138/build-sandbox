<script setup lang="ts">
import { normalizeNames, splitNames } from '~/utils/names'

defineOptions({ inheritAttrs: false })
const model = defineModel<string>({ default: '' })
const props = withDefaults(defineProps<{ disabled?: boolean; maxlength?: number | string }>(), { maxlength: 100 })
const tags = ref(splitNames(normalizeNames(model.value)))
const text = ref('')
const input = ref<{ inputRef?: HTMLInputElement } | null>(null)
const error = ref('')
const limit = computed(() => Number(props.maxlength))
const remaining = computed(() => Math.max(0, limit.value - tags.value.join(' ').length - (tags.value.length ? 1 : 0)))
let composing = false
let published = model.value

function publish() {
  // Persist the unfinished name too, before blur/Enter, so a refresh cannot lose it.
  published = normalizeNames([...tags.value, text.value].join(' '))
  model.value = published
}
watch(model, value => {
  if (value === published) return
  tags.value = splitNames(normalizeNames(value))
  text.value = ''
  published = value
  error.value = ''
})
function update(value: string | number | undefined) {
  const raw = String(value ?? '')
  if (normalizeNames([...tags.value, raw].join(' ')).length > limit.value) {
    error.value = `最多 ${limit.value} 字`
    if (input.value?.inputRef) input.value.inputRef.value = text.value
    return
  }
  error.value = ''
  if (composing) text.value = raw
  else {
    const pieces = raw.split(/[，,。.\s]+/u)
    text.value = pieces.pop() ?? ''
    tags.value.push(...pieces.filter(Boolean))
    if (input.value?.inputRef) input.value.inputRef.value = text.value
  }
  publish()
}
function commit() {
  if (composing || props.disabled) return
  tags.value.push(...splitNames(normalizeNames(text.value)))
  text.value = ''
  publish()
}
function keydown(event: KeyboardEvent) {
  if (props.disabled || event.isComposing || composing || event.keyCode === 229) return
  if (event.key === 'Enter') { event.preventDefault(); commit() }
  else if (event.key === 'Backspace' && !text.value && tags.value.length) {
    event.preventDefault()
    text.value = tags.value.pop() ?? ''
    publish()
  }
}
function paste(event: ClipboardEvent) {
  if (props.disabled || !event.clipboardData) return
  event.preventDefault()
  const element = input.value?.inputRef
  const start = element?.selectionStart ?? text.value.length
  const end = element?.selectionEnd ?? start
  update(text.value.slice(0, start) + event.clipboardData.getData('text/plain') + text.value.slice(end))
}
function remove(index: number) {
  if (props.disabled) return
  tags.value.splice(index, 1)
  error.value = ''
  publish()
  input.value?.inputRef?.focus()
}
function compositionEnd(event: CompositionEvent) {
  composing = false
  update((event.target as HTMLInputElement).value)
}
</script>

<template>
  <div class="names-field">
    <div class="names-editor" :class="{ 'names-disabled': disabled }">
      <span v-for="(tag, index) in tags" :key="index" class="name-edit-tag">
        <UBadge color="neutral" variant="subtle">{{ tag }}</UBadge>
        <UButton type="button" icon="i-lucide-x" color="neutral" variant="ghost" class="name-remove" :aria-label="`移除 ${tag}`" :disabled="disabled" @mousedown.prevent @click="remove(index)" />
      </span>
      <UInput ref="input" v-bind="$attrs" :model-value="text" :disabled="disabled" :maxlength="remaining" variant="none" class="names-input" :ui="{ base: 'ring-0 focus:ring-0 shadow-none' }" @update:model-value="update" @keydown="keydown" @paste="paste" @blur="commit" @compositionstart="composing = true" @compositionend="compositionEnd" />
    </div>
    <p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p>
  </div>
</template>
