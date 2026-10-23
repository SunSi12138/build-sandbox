// These are string fields in the API. Only the editor normalizes delimiters.
export function splitNames(value: string): string[] {
  return value.trim() ? value.trim().split(/\s+/u) : []
}

export function normalizeNames(value: string): string {
  return value.split(/[，,、；;\s]+/u).filter(Boolean).join(' ')
}
