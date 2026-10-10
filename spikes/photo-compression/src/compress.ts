import { JPEG_QUALITY, MAX_EDGE, MAX_PARALLEL, type CompressedPhoto, type CompressionOptions, type WorkerReply } from './types'

let active = 0
let observedMax = 0
const waiting: Array<() => void> = []

async function acquire() {
  if (active >= MAX_PARALLEL) await new Promise<void>(resolve => waiting.push(resolve))
  else active++
  observedMax = Math.max(observedMax, active)
}

function release() {
  const next = waiting.shift()
  if (next) next()
  else active--
}

export function getCompressionStats() { return { active, observedMax, queued: waiting.length } }

async function fallback(file: File, quality: number): Promise<CompressedPhoto> {
  const bitmap = await createImageBitmap(file)
  const canvas = document.createElement('canvas')
  try {
    const ratio = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height))
    canvas.width = Math.max(1, Math.round(bitmap.width * ratio))
    canvas.height = Math.max(1, Math.round(bitmap.height * ratio))
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('无法处理照片，请更换浏览器或图片')
    ctx.fillStyle = '#fff'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.imageSmoothingEnabled = true
    ctx.imageSmoothingQuality = 'high'
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(
      value => value ? resolve(value) : reject(new Error('JPEG 压缩失败，请重新选择照片')),
      'image/jpeg', quality / 100,
    ))
    return { blob, width: canvas.width, height: canvas.height, engine: 'canvas-fallback' }
  } finally {
    bitmap.close()
    canvas.width = canvas.height = 1
  }
}

function viaWorker(file: File, quality: number, options: CompressionOptions): Promise<CompressedPhoto> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./photo.worker.ts', import.meta.url), { type: 'module' })
    const timer = window.setTimeout(() => { worker.terminate(); reject(new Error('压缩器响应超时')) }, 60_000)
    function finish() { window.clearTimeout(timer); worker.terminate() }
    worker.onmessage = (event: MessageEvent<WorkerReply>) => {
      const message = event.data
      if (message.kind === 'progress') { options.onProgress?.(message.percent, message.step); return }
      finish()
      if (message.kind === 'fallback') { reject(new Error(message.reason)); return }
      resolve({ blob: new Blob([message.jpeg], { type: 'image/jpeg' }), width: message.width, height: message.height, engine: 'mozjpeg-worker' })
    }
    worker.onerror = () => { finish(); reject(new Error('Worker 初始化失败')) }
    worker.onmessageerror = () => { finish(); reject(new Error('Worker 数据传输失败')) }
    worker.postMessage({ file, quality, forceFallback: options.forceFallback ?? false })
  })
}

export async function compressPhoto(file: File, options: CompressionOptions = {}): Promise<CompressedPhoto> {
  const quality = Math.round(Math.min(95, Math.max(35, options.quality ?? JPEG_QUALITY)))
  options.onProgress?.(0, '等待处理')
  await acquire()
  try {
    let result: CompressedPhoto
    try { result = await viaWorker(file, quality, options) }
    catch { options.onProgress?.(45, '使用浏览器兼容压缩'); result = await fallback(file, quality) }
    options.onProgress?.(100, '压缩完成')
    return result
  } finally { release() }
}
