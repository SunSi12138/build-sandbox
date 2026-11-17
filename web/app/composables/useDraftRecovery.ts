/**
 * Shared local-only draft storage for point, inspection and activity editors.
 * Keep each form's existing key, version and payload field to preserve old drafts.
 * Validation of individual form fields remains the responsibility of the caller.
 */
export function useDraftRecovery(key: string, version: number, field: 'draft' | 'data' = 'data') {
  const savedAt = ref<number | null>(null)
  const error = ref('')
  let timer: ReturnType<typeof setTimeout> | undefined

  function read<T>(): T | null {
    try {
      const raw = localStorage.getItem(key)
      if (!raw) return null
      const envelope: unknown = JSON.parse(raw)
      if (!envelope || typeof envelope !== 'object') throw new Error('草稿格式错误')
      const record = envelope as Record<string, unknown>
      if (record.version !== version) {
        // Do not interpret an incompatible older version as current form data.
        clear()
        return null
      }
      if (!record[field] || typeof record[field] !== 'object') throw new Error('草稿内容无效')
      savedAt.value = typeof record.savedAt === 'number' ? record.savedAt : null
      error.value = ''
      return record[field] as T
    } catch {
      error.value = '草稿无法读取，请清空草稿后重新填写'
      throw new Error(error.value)
    }
  }

  function write(value: unknown): boolean {
    try {
      const timestamp = Date.now()
      localStorage.setItem(key, JSON.stringify({ version, savedAt: timestamp, [field]: value }))
      savedAt.value = timestamp
      error.value = ''
      return true
    } catch {
      error.value = '当前浏览器无法保存草稿'
      return false
    }
  }

  function schedule(value: () => unknown, delay = 250) {
    clearTimeout(timer)
    timer = setTimeout(() => { write(value()) }, delay)
  }

  function flush(value: () => unknown) {
    clearTimeout(timer)
    return write(value())
  }

  function clear() {
    clearTimeout(timer)
    try {
      localStorage.removeItem(key)
      savedAt.value = null
      error.value = ''
    } catch {
      error.value = '无法清空草稿'
    }
  }

  function stop() { clearTimeout(timer) }
  onBeforeUnmount(stop)
  return { read, write, schedule, flush, clear, stop, savedAt, error }
}
