/** Download a same-origin Word file while keeping API failures inside the current page. */
export async function downloadWord(url: string, fallbackName: string): Promise<void> {
  const response = await fetch(url, { headers: { Accept: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' } })
  if (!response.ok) {
    let message = '导出失败，请重试'
    try { const error = await response.json() as { message?: string }; if (error.message) message = error.message } catch { /* Preserve the operation-specific error. */ }
    throw new Error(message)
  }
  const blob = await response.blob()
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const encoded = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1]
  let filename = fallbackName
  if (encoded) { try { filename = decodeURIComponent(encoded) } catch { /* A filename must not prevent downloading. */ } }
  const objectUrl = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = objectUrl; anchor.download = filename
  document.body.appendChild(anchor); anchor.click(); anchor.remove()
  setTimeout(() => URL.revokeObjectURL(objectUrl), 1000)
}
