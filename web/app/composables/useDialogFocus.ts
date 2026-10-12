/** Return focus without scrolling the page when a dialog changes or removes its opener. */
export function useDialogFocus(selector?: string) {
  const content = { onCloseAutoFocus: (event: Event) => event.preventDefault() }
  async function restoreFocus() {
    await nextTick()
    await nextTick()
    const target = selector ? document.querySelector<HTMLElement>(selector) : null
    ;(target ?? document.getElementById('main-content'))?.focus({ preventScroll: true })
  }
  return { content, restoreFocus }
}
