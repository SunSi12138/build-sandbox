import type { CommunityDto } from '~/types/api'
export function gridDescendants(grids: CommunityDto[], id: string) {
  const ids = new Set<string>([id])
  let changed = true
  while (changed) { changed = false; for (const grid of grids) if (grid.parentId && ids.has(grid.parentId) && !ids.has(grid.id)) { ids.add(grid.id); changed = true } }
  return ids
}
