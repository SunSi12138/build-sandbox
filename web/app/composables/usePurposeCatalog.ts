import type { CommunityRecord, PurposeData, UseData } from '~/types/community'
export function usePurposeCatalog() {
  const purposes = useState<CommunityRecord<PurposeData>[]>('purpose-catalog', () => [])
  async function loadPurposes() { purposes.value = await $fetch('/api/records/purpose-type', { query: { all: true } }) }
  function purpose(id: string) { return purposes.value.find(x => x.id === id) }
  return { purposes, loadPurposes, purpose }
}
export function emptyUse(): UseData { return { position: '', purposeId: '', contact: '', phone: '', startDate: '', endDate: '', state: '使用中', notes: '', photoIds: [], business: '', owner: '', ownerPhone: '', tenantCount: '', storedGoods: '', area: '' } }
