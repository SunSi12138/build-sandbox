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
  // Persist the unfinished name too, so neither saving nor refreshing relies on blur.
  published = normalizeNames([...tags.value, text.value].join(' '))
  model.value = published
}
watch(model, value => {
  if (value === published) return
  tags.value = splitNames(normalizeNames(value))
  text.value = ''
  published = value
  if (input.value?.inputRef) input.value.inputRef.value = ''
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
    const pieces = raw.split(/[，,、；;\s]+/u)
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
  if (event.key === 'Enter') { event.preventDefault(); event.stopImmediatePropagation(); commit(); if (input.value?.inputRef) input.value.inputRef.value = '' }
  else if (event.key === 'Backspace' && !text.value && tags.value.length) {
    event.preventDefault()
    event.stopImmediatePropagation()
    text.value = tags.value.pop() ?? ''
    if (input.value?.inputRef) input.value.inputRef.value = text.value
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
function updateTags(value: string[]) {
  tags.value = value
  error.value = ''
  publish()
}
function nativeInput(event: Event) { update((event.target as HTMLInputElement).value) }
function compositionEnd(event: CompositionEvent) {
  composing = false
  update((event.target as HTMLInputElement).value)
}
// Delimiters and Enter commit tags. Blur must not change the editor height between
// pointerdown and click on the next control; the unfinished token is already saved.
</script>

<template>
  <div class="names-field">
    <UInputTags ref="input" v-bind="$attrs" :model-value="tags" :disabled="disabled"
      :max-length="remaining" delimiter="" :add-on-blur="false" :duplicate="true"
      :ui="{ root: 'names-editor', item: 'name-edit-tag', itemText: 'name-tag-text', itemDelete: 'name-remove', input: 'names-input', itemDeleteIcon: 'size-3.5' }"
      @update:model-value="updateTags" @input="nativeInput" @keydown.capture="keydown"
      @paste.capture="paste" @compositionstart="composing = true" @compositionend="compositionEnd">
      <template #item-delete="{ item }"><UIcon name="i-lucide-x" class="size-3.5" /><span class="sr-only">移除 {{ item }}</span></template>
    </UInputTags>
    <p v-if="error" class="text-xs text-error" role="alert">{{ error }}</p>
  </div>
</template>
