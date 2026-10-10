import { compressPhoto, getCompressionStats } from './compress'

const status = document.querySelector<HTMLPreElement>('#status')!
const photos = document.querySelector<HTMLDivElement>('#photos')!
let lastArtifacts: { input: Blob; output: Blob } | undefined
const colors = ['#eb3026', '#19b34e', '#254bdd', '#efcc18']
const expected: Record<number, number[]> = {
  1: [0, 1, 2, 3], 2: [1, 0, 3, 2], 3: [3, 2, 1, 0], 4: [2, 3, 0, 1],
  5: [0, 2, 1, 3], 6: [2, 0, 3, 1], 7: [3, 1, 2, 0], 8: [1, 3, 0, 2],
}
const referenceRgb = colors.map(color => [1, 3, 5].map(start => Number.parseInt(color.slice(start, start + 2), 16)))

async function fixture(width: number, height: number): Promise<Blob> {
  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const ctx = canvas.getContext('2d')!
  const data = ctx.createImageData(width, height)
  let seed = 123456789
  // Deterministic detail/noise to exercise compression, not a supplied real phone photo.
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      seed = (Math.imul(seed, 1664525) + 1013904223) | 0
      const noise = ((seed >>> 16) & 255) - 128
      const offset = (y * width + x) * 4
      data.data[offset] = 125 + 70 * Math.sin(x / 21) + noise * 0.5
      data.data[offset + 1] = 135 + 60 * Math.sin(y / 27) + noise * 0.5
      data.data[offset + 2] = 100 + 40 * Math.cos((x + y) / 16) + noise * 0.5
      data.data[offset + 3] = 255
    }
  }
  ctx.putImageData(data, 0, 0)
  colors.forEach((color, index) => {
    ctx.fillStyle = color
    ctx.fillRect(index % 2 ? width * 0.78 : 0, index >= 2 ? height * 0.78 : 0, width * 0.22, height * 0.22)
  })
  const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(new Error('测试图片生成失败')), 'image/jpeg', 0.96))
  canvas.width = canvas.height = 1
  return blob
}

async function withExif(blob: Blob, orientation: number): Promise<File> {
  const source = new Uint8Array(await blob.arrayBuffer())
  const app1 = new Uint8Array([
    255, 225, 0, 34, 69, 120, 105, 102, 0, 0,
    73, 73, 42, 0, 8, 0, 0, 0, 1, 0,
    18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0,
  ])
  return new File([source.slice(0, 2), app1, source.slice(2)], `orientation-${orientation}.jpg`, { type: 'image/jpeg' })
}

function showPhoto(blob: Blob) {
  const image = document.createElement('img')
  const url = URL.createObjectURL(blob)
  image.onload = () => URL.revokeObjectURL(url)
  image.src = url
  photos.appendChild(image)
}

async function inspect(blob: Blob) {
  const bitmap = await createImageBitmap(blob)
  const canvas = document.createElement('canvas')
  canvas.width = bitmap.width
  canvas.height = bitmap.height
  const ctx = canvas.getContext('2d', { willReadFrequently: true })!
  ctx.drawImage(bitmap, 0, 0)
  const cornerColors = [[0.1, 0.1], [0.9, 0.1], [0.1, 0.9], [0.9, 0.9]].map(([x, y]) => {
    const sample = ctx.getImageData(Math.floor(bitmap.width * x), Math.floor(bitmap.height * y), 1, 1).data
    const distances = referenceRgb.map(rgb => rgb.reduce((sum, value, index) => sum + (value - sample[index]) ** 2, 0))
    return distances.indexOf(Math.min(...distances))
  })
  const data = new Uint8Array(await blob.arrayBuffer())
  const hasExif = data.some((_, index) => data[index] === 69 && data[index + 1] === 120 && data[index + 2] === 105 && data[index + 3] === 102 && data[index + 4] === 0 && data[index + 5] === 0)
  const result = { width: bitmap.width, height: bitmap.height, cornerColors, hasExif, jpegMagic: data[0] === 255 && data[1] === 216 }
  bitmap.close()
  canvas.width = canvas.height = 1
  return result
}

async function runSpike(forceFallback = false) {
  status.textContent = '生成合成图片并测试八种 EXIF 方向…'
  photos.replaceChildren()
  const started = performance.now()
  const source = await fixture(800, 600)
  const orientations = await Promise.all(Array.from({ length: 8 }, async (_, index) => {
    const orientation = index + 1
    const file = await withExif(source, orientation)
    const result = await compressPhoto(file, { forceFallback })
    const actual = await inspect(result.blob)
    const width = orientation <= 4 ? 800 : 600
    const height = orientation <= 4 ? 600 : 800
    return { orientation, engine: result.engine, ...actual, passed: actual.width === width && actual.height === height && actual.cornerColors.join() === expected[orientation].join() && !actual.hasExif && actual.jpegMagic }
  }))
  status.textContent = '方向检查完成，压缩 4032 × 3024（约 1200 万像素）合成照片…'
  const largeSource = await fixture(4032, 3024)
  const largeFile = await withExif(largeSource, 6)
  const largeResult = await compressPhoto(largeFile, { forceFallback })
  const largeActual = await inspect(largeResult.blob)
  lastArtifacts = { input: largeFile, output: largeResult.blob }
  showPhoto(largeResult.blob)
  const large = {
    inputBytes: largeFile.size, outputBytes: largeResult.blob.size,
    compressionRatio: +(largeResult.blob.size / largeFile.size).toFixed(4),
    engine: largeResult.engine, ...largeActual,
    passed: largeActual.width === 1200 && largeActual.height === 1600 && largeActual.cornerColors.join() === expected[6].join() && !largeActual.hasExif && largeActual.jpegMagic,
  }
  const result = {
    passed: orientations.every(item => item.passed) && large.passed && getCompressionStats().observedMax <= 2,
    userAgent: navigator.userAgent, origin: location.origin, secureContext: isSecureContext,
    crossOriginIsolated, sharedArrayBufferAvailable: typeof SharedArrayBuffer !== 'undefined',
    forceFallback, elapsedMs: Math.round(performance.now() - started),
    orientations, large, concurrency: getCompressionStats(),
  }
  status.textContent = JSON.stringify(result, null, 2)
  return result
}

document.querySelector<HTMLInputElement>('#files')!.addEventListener('change', async event => {
  const files = Array.from((event.target as HTMLInputElement).files ?? [])
  photos.replaceChildren()
  const progress = files.map(file => `${file.name}：等待处理`)
  await Promise.all(files.map(async (file, index) => {
    try {
      const result = await compressPhoto(file, { onProgress: (percent, step) => { progress[index] = `${file.name}：${percent}% ${step}`; status.textContent = progress.join('\n') } })
      progress[index] = `${file.name}：${Math.round(file.size / 1024)} KB → ${Math.round(result.blob.size / 1024)} KB，${result.width}×${result.height}，${result.engine}`
      showPhoto(result.blob)
    } catch (error) { progress[index] = `${file.name}：${error instanceof Error ? error.message : '处理失败'}` }
    status.textContent = progress.join('\n')
  }))
})

document.querySelector('#test')!.addEventListener('click', () => { void runSpike().catch(error => { status.textContent = String(error) }) })
document.querySelector('#fallback')!.addEventListener('click', () => { void runSpike(true).catch(error => { status.textContent = String(error) }) })
async function exportSpikeImages() {
  if (!lastArtifacts) throw new Error('请先完成测试')
  const toBase64 = (blob: Blob) => new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).split(',')[1])
    reader.onerror = () => reject(new Error('无法导出测试图片'))
    reader.readAsDataURL(blob)
  })
  return { input: await toBase64(lastArtifacts.input), output: await toBase64(lastArtifacts.output) }
}
Object.assign(window, { runSpike, exportSpikeImages })
