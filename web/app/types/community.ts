export interface GeoShape { type: 'Point' | 'Polygon' | 'MultiPolygon'; coordinates: number[][]; areas?: number[][][] | null }
export interface MapFeature { id: string; kind: 'point' | 'grid'; name: string; communityId: string; shape: GeoShape; revision: number; openIssues: number; disabled: boolean; typeId?: string; color?: string; purposeIds?: string[]; uses?: { id: string; name: string; purposeName: string; icon: string }[] }
export interface MapLayer { id: string; name: string; folder: string; scheme: 'xyz' | 'tms'; enabled: boolean; isDefault: boolean; opacity: number; minZoom: number; maxZoom: number; sortOrder: number; urlTemplate: string; apiKey: string; subdomains: string; updateCache: boolean; annotation: boolean; mapKey: string; cacheVersion: number }
export interface MapState { layers: MapLayer[]; features: MapFeature[] }
export interface CommunityRecord<T = Record<string, unknown>> { id: string; kind: string; parentId: string; name: string; data: T; revision: number; updatedAt: string; archived: boolean }
export interface PurposeField { id: string; label: string; type: 'text' | 'number'; required: boolean }
export interface PurposeData { icon: string; fields: PurposeField[] }
export interface UseData { position: string; purposeId: string; contact: string; phone: string; startDate: string; endDate: string; state: string; notes: string; photoIds: string[]; values: Record<string,string|number>; fieldSnapshot?: PurposeField[] }
export interface UnitData { parentUnitId?: string; floor: string; area: string; notes: string; photoIds?: string[] }
export interface InspectionScopeValue { inspectionTypeId: string; useId: string; checklist: { name: string; result: string; notes: string }[]; incompleteReason: string }
