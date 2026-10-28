import { queryText } from '~/utils/format'

export function useQueryFilters() {
  const route = useRoute()
  const router = useRouter()
  const pending = reactive<Record<string, string>>({})
  let updating = 0
  let timer: ReturnType<typeof setTimeout> | undefined
  function replace(values: Record<string, string | string[] | undefined>, resetPage = true) {
    clearTimeout(timer)
    const query = { ...route.query, ...Object.fromEntries(Object.entries(pending).map(([key, value]) => [key, value || undefined])), ...values }
    if (resetPage) query.page = undefined
    updating++
    return router.replace({ path: route.path, query }).finally(() => {
      updating--
      for (const key of Object.keys(pending)) if ((query[key] || '') === pending[key]) delete pending[key]
    })
  }
  function set(name: string, value: string, resetPage = true) { replace({ [name]: value || undefined }, resetPage && name !== 'page') }
  function field(name: string, fallback = '') {
    return computed({
      get: () => pending[name] ?? (queryText(route.query[name]) || fallback),
      set: (value: string) => {
        if (name !== 'q') { set(name, value === fallback ? '' : value); return }
        pending[name] = value === fallback ? '' : value
        clearTimeout(timer)
        timer = setTimeout(() => replace({}), 250)
      }
    })
  }
  onBeforeRouteUpdate(() => { if (!updating) { clearTimeout(timer); for (const key of Object.keys(pending)) delete pending[key] } })
  watch(() => route.path, () => { clearTimeout(timer); for (const key of Object.keys(pending)) delete pending[key] })
  onBeforeUnmount(() => clearTimeout(timer))
  const tagIds = computed({ get: () => { const value = route.query.tagId; return (Array.isArray(value) ? value : value ? [value] : []).filter((id): id is string => typeof id === 'string' && !!id) }, set: (value: string[]) => { void replace({ tagId: value.length ? value : undefined }) } })
  const page = computed(() => Math.max(1, Number.parseInt(queryText(route.query.page) || '1', 10) || 1))
  return { field, set, setFilters: replace, page, tagIds, flush: () => Object.keys(pending).length ? replace({}) : Promise.resolve(), setPage: (value: number) => set('page', value === 1 ? '' : String(value), false) }
}
