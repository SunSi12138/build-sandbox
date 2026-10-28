/** One owner for a changing read: cancelled or superseded responses cannot update its view. */
export function useLatestRequest() {
  let active: AbortController | undefined
  function cancel() { active?.abort(); active = undefined }
  function start() {
    cancel()
    const controller = new AbortController()
    active = controller
    return { signal: controller.signal, isCurrent: () => active === controller && !controller.signal.aborted }
  }
  onBeforeUnmount(cancel)
  return { start, cancel }
}
