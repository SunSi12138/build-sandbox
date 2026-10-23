export interface GeoShape { type: 'Point' | 'Polygon'; coordinates: number[][] }
export interface MapFeature { id: string; kind: 'point' | 'grid'; name: string; communityId: string; shape: GeoShape; revision: number; openIssues: number; disabled: boolean }
export interface MapLayer { id: string; name: string; folder: string; scheme: 'xyz' | 'tms'; enabled: boolean; isDefault: boolean; opacity: number; minZoom: number; maxZoom: number; sortOrder: number }
export interface MapState { layers: MapLayer[]; features: MapFeature[] }
export interface CommunityRecord<T = Record<string, unknown>> { id: string; kind: string; parentId: string; name: string; data: T; revision: number; updatedAt: string; archived: boolean }
export interface UseData { unitId: string; purposes: string[]; contact: string; phone: string; startDate: string; endDate: string; state: string; notes: string; photoIds: string[] }
export interface UnitData { floor: string; area: string; notes: string }
export interface InspectionScopeValue { inspectionTypeId: string; useId: string; checklist: { name: string; result: string; notes: string }[]; incompleteReason: string }
