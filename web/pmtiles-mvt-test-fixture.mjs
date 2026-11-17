// Minimal valid PMTiles v3 with one unlabelled MVT polygon. Used to verify
// the real PBF rendering path, not just the archive header or raster tiles.
function varint(input) {
  const bytes = []
  let value = input
  do {
    const part = value % 128
    value = Math.floor(value / 128)
    bytes.push(part | (value ? 128 : 0))
  } while (value)
  return Buffer.from(bytes)
}
function fieldVarint(tag, value) {
  return Buffer.concat([varint(tag * 8), varint(value)])
}
function fieldBytes(tag, value) {
  return Buffer.concat([varint(tag * 8 + 2), varint(value.length), value])
}
function zigzag(value) { return value < 0 ? -2 * value - 1 : 2 * value }

export function createMvtPmtilesFixture() {
  // A large polygon over the center of a z=0 world tile.
  // There are deliberately NO name/label properties: generic vector geometry
  // must remain visible even when a tile is not a place-label layer.
  const geometry = Buffer.concat([
    varint(9), varint(zigzag(100)), varint(zigzag(100)),
    varint(26), varint(zigzag(3896)), varint(0),
    varint(0), varint(zigzag(3896)),
    varint(zigzag(-3896)), varint(0),
    varint(15),
  ])
  const feature = Buffer.concat([fieldVarint(3, 3), fieldBytes(4, geometry)])
  const layer = Buffer.concat([
    fieldBytes(1, Buffer.from('water')),
    fieldBytes(2, feature),
    fieldVarint(5, 4096),
    fieldVarint(15, 2),
  ])
  const tile = fieldBytes(3, layer)
  // One directory entry for z=0, x=0, y=0 (Hilbert tile ID 0).
  const directory = Buffer.concat([varint(1), varint(0), varint(1), varint(tile.length), varint(1)])
  const metadata = Buffer.from(JSON.stringify({
    vector_layers: [{ id: 'water', fields: {}, minzoom: 0, maxzoom: 0 }],
  }))
  const rootOffset = 127
  const metadataOffset = rootOffset + directory.length
  const tileOffset = metadataOffset + metadata.length
  const archive = Buffer.alloc(16384)
  archive.write('PMTiles', 0, 'ascii')
  archive[7] = 3
  const u64 = (offset, value) => archive.writeBigUInt64LE(BigInt(value), offset)
  u64(8, rootOffset)
  u64(16, directory.length)
  u64(24, metadataOffset)
  u64(32, metadata.length)
  u64(40, tileOffset)
  u64(48, 0)
  u64(56, tileOffset)
  u64(64, tile.length)
  u64(72, 1)
  u64(80, 1)
  u64(88, 1)
  archive[96] = 1 // clustered
  archive[97] = 1 // no directory compression
  archive[98] = 1 // no tile compression
  archive[99] = 1 // MVT/PBF
  archive[100] = 0
  archive[101] = 0
  archive.writeInt32LE(-1800000000, 102)
  archive.writeInt32LE(-850000000, 106)
  archive.writeInt32LE(1800000000, 110)
  archive.writeInt32LE(850000000, 114)
  archive[118] = 0
  directory.copy(archive, rootOffset)
  metadata.copy(archive, metadataOffset)
  tile.copy(archive, tileOffset)
  return { archive, tile }
}
