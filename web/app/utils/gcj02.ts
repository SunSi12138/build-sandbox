/**
 * API/database coordinates are GCJ-02 (the format used by Amap).
 * OpenLayers EPSG:3857 tile geometry is projected from WGS84 coordinates.
 * Convert only at that rendering/editing boundary; never transform saved data.
 */
const PI = Math.PI
const A = 6378245.0
const EE = 0.006693421622965943

function outsideChina(longitude: number, latitude: number): boolean {
  return longitude < 72.004 || longitude > 137.8347 || latitude < 0.8293 || latitude > 55.8271
}

function latitudeOffset(x: number, y: number): number {
  let value = -100 + 2 * x + 3 * y + 0.2 * y * y + 0.1 * x * y + 0.2 * Math.sqrt(Math.abs(x))
  value += (20 * Math.sin(6 * x * PI) + 20 * Math.sin(2 * x * PI)) * 2 / 3
  value += (20 * Math.sin(y * PI) + 40 * Math.sin(y / 3 * PI)) * 2 / 3
  value += (160 * Math.sin(y / 12 * PI) + 320 * Math.sin(y * PI / 30)) * 2 / 3
  return value
}

function longitudeOffset(x: number, y: number): number {
  let value = 300 + x + 2 * y + 0.1 * x * x + 0.1 * x * y + 0.1 * Math.sqrt(Math.abs(x))
  value += (20 * Math.sin(6 * x * PI) + 20 * Math.sin(2 * x * PI)) * 2 / 3
  value += (20 * Math.sin(x * PI) + 40 * Math.sin(x / 3 * PI)) * 2 / 3
  value += (150 * Math.sin(x / 12 * PI) + 300 * Math.sin(x / 30 * PI)) * 2 / 3
  return value
}

export function wgs84ToGcj02(coordinate: readonly number[]): [number, number] {
  const longitude = coordinate[0]!, latitude = coordinate[1]!
  if (outsideChina(longitude, latitude)) return [longitude, latitude]
  const radian = latitude / 180 * PI
  const sin = Math.sin(radian)
  const magic = 1 - EE * sin * sin
  const sqrt = Math.sqrt(magic)
  const deltaLatitude = latitudeOffset(longitude - 105, latitude - 35) * 180 / ((A * (1 - EE)) / (magic * sqrt) * PI)
  const deltaLongitude = longitudeOffset(longitude - 105, latitude - 35) * 180 / (A / sqrt * Math.cos(radian) * PI)
  return [longitude + deltaLongitude, latitude + deltaLatitude]
}

export function gcj02ToWgs84(coordinate: readonly number[]): [number, number] {
  const longitude = coordinate[0]!, latitude = coordinate[1]!
  if (outsideChina(longitude, latitude)) return [longitude, latitude]
  // Iteratively undo the nonlinear forward shift instead of subtracting it once.
  let wgsLongitude = longitude, wgsLatitude = latitude
  for (let attempt = 0; attempt < 6; attempt++) {
    const [shiftedLongitude, shiftedLatitude] = wgs84ToGcj02([wgsLongitude, wgsLatitude])
    const deltaLongitude = shiftedLongitude - longitude
    const deltaLatitude = shiftedLatitude - latitude
    wgsLongitude -= deltaLongitude
    wgsLatitude -= deltaLatitude
    if (Math.abs(deltaLongitude) < 1e-9 && Math.abs(deltaLatitude) < 1e-9) break
  }
  return [wgsLongitude, wgsLatitude]
}
