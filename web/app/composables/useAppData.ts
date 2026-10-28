import type { BootstrapDto } from '~/types/api'
import { todayLocal } from '~/utils/format'

let pending: Promise<BootstrapDto> | null = null

/** Only the small settings/dictionary payload is cached; records stay on the server. */
export function useAppData() {
  const bootstrap = useState<BootstrapDto | null>('street-eco-bootstrap', () => null)
  const loadedDay = useState<string>('street-eco-bootstrap-day', () => '')
  async function load(force = false): Promise<BootstrapDto> {
    // A refresh after a write must not reuse a snapshot requested before it.
    if (force && pending) { try { await pending } catch { /* The fresh request below reports its own error. */ } }
    if (pending) return pending
    if (bootstrap.value && loadedDay.value === todayLocal() && !force) return bootstrap.value
    if (!pending) {
      pending = $fetch<BootstrapDto>('/api/bootstrap')
        .then((data) => { bootstrap.value = data; loadedDay.value = todayLocal(); return data })
        .finally(() => { pending = null })
    }
    return pending
  }
  return { bootstrap, load, refresh: () => load(true) }
}
