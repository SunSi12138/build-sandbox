import { queryText } from '~/utils/format'

export function useQueryFilters() {
  const route = useRoute()
  const router = useRouter()
  function set(name: string, value: string, resetPage = true) {
    const query = { ...route.query, [name]: value || undefined }
    if (resetPage && name !== 'page') query.page = undefined
    void router.replace({ path: route.path, query })
  }
  function field(name: string, fallback = '') {
    return computed({ get: () => queryText(route.query[name]) || fallback, set: (value: string) => set(name, value === fallback ? '' : value) })
  }
  const page = computed(() => Math.max(1, Number.parseInt(queryText(route.query.page) || '1', 10) || 1))
  return { field, set, page, setPage: (value: number) => set('page', value === 1 ? '' : String(value), false) }
}
