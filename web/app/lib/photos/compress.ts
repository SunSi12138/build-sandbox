import {
  cancelledPhotoOperation, DEFAULT_JPEG_QUALITY, MAX_PARALLEL_COMPRESSION, MAX_PHOTO_EDGE,
  type CompressedPhoto, type CompressionOptions, type WorkerReply,
} from './types'

interface WaitingPhoto {
  file: File
  options: CompressionOptions
  resolve: (photo: CompressedPhoto) => void
  reject: (error: unknown) => void
  removeWaitingAbort: () => void
}

// Shared by every PhotoUploader instance, including separate problem cards.
const waiting: WaitingPhoto[] = []
let active = 0

function checkCancelled(signal?: AbortSignal) {
  if (signal?.aborted) throw cancelledPhotoOperation()
}

async function canvasFallback(file: File, quality: number, signal?: AbortSignal): Promise<CompressedPhoto> {
  checkCancelled(signal)
  const bitmap = await createImageBitmap(file)
  const canvas = document.createElement('canvas')
  try {
    checkCancelled(signal)
    const ratio = Math.min(1, MAX_PHOTO_EDGE / Math.max(bitmap.width, bitmap.height))
    canvas.width = Math.max(1, Math.round(bitmap.width * ratio))
    canvas.height = Math.max(1, Math.round(bitmap.height * ratio))
    const context = canvas.getContext('2d')
    if (!context) throw new Error('无法处理这张照片，请重新选择。')
    context.fillStyle = 'white'
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.imageSmoothingEnabled = true
    context.imageSmoothingQuality = 'high'
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height)
    const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(
      value => value ? resolve(value) : reject(new Error('照片压缩失败，请重新选择。')),
      'image/jpeg', quality / 100,
    ))
    checkCancelled(signal)
    let thumbnail: Blob | undefined
    const thumbCanvas = document.createElement('canvas')
    try {
      const thumbRatio = Math.min(1, 360 / Math.max(canvas.width, canvas.height))
      thumbCanvas.width = Math.max(1, Math.round(canvas.width * thumbRatio))
      thumbCanvas.height = Math.max(1, Math.round(canvas.height * thumbRatio))
      thumbCanvas.getContext('2d')?.drawImage(canvas, 0, 0, thumbCanvas.width, thumbCanvas.height)
      thumbnail = await new Promise<Blob | undefined>(resolve => thumbCanvas.toBlob(value => resolve(value ?? undefined), 'image/jpeg', 0.7))
    } catch { /* Thumbnail failure does not prevent saving the original. */ }
    finally { thumbCanvas.width = thumbCanvas.height = 1 }
    return { blob, thumbnail, width: canvas.width, height: canvas.height, engine: 'canvas-fallback' }
  } finally {
    bitmap.close()
    canvas.width = canvas.height = 1
  }
}

function inWorker(file: File, quality: number, options: CompressionOptions): Promise<CompressedPhoto> {
  return new Promise((resolve, reject) => {
    checkCancelled(options.signal)
    const worker = new Worker(new URL('./photo.worker.ts', import.meta.url), { type: 'module' })
    let settled = false
    const timeout = window.setTimeout(() => finish(new Error('照片处理超时。')), 60_000)
    const abort = () => finish(cancelledPhotoOperation())

    function finish(error?: Error, result?: CompressedPhoto) {
      if (settled) return
      settled = true
      window.clearTimeout(timeout)
      options.signal?.removeEventListener('abort', abort)
      worker.terminate()
      if (error) reject(error)
      else if (result) resolve(result)
    }

    options.signal?.addEventListener('abort', abort, { once: true })
    worker.onmessage = (event: MessageEvent<WorkerReply>) => {
      const message = event.data
      if (message.kind === 'progress') { options.onProgress?.(message.percent, message.message); return }
      if (message.kind === 'fallback') { finish(new Error(message.reason)); return }
      finish(undefined, {
        blob: new Blob([message.jpeg], { type: 'image/jpeg' }),
        thumbnail: message.thumbnail ? new Blob([message.thumbnail], { type: 'image/jpeg' }) : undefined,
        width: message.width, height: message.height, engine: 'mozjpeg-worker',
      })
    }
    worker.onerror = () => finish(new Error('照片后台处理暂不可用。'))
    worker.onmessageerror = () => finish(new Error('照片处理结果无法读取。'))
    try { worker.postMessage({ file, quality }) }
    catch (error) { finish(error instanceof Error ? error : new Error(String(error))) }
  })
}

async function processPhoto(task: WaitingPhoto): Promise<CompressedPhoto> {
  const { file, options } = task
  checkCancelled(options.signal)
  const requestedQuality = options.quality ?? DEFAULT_JPEG_QUALITY
  const quality = Number.isFinite(requestedQuality) ? Math.round(Math.min(100, Math.max(1, requestedQuality))) : DEFAULT_JPEG_QUALITY
  let result: CompressedPhoto
  try { result = await inWorker(file, quality, options) }
  catch {
    checkCancelled(options.signal)
    options.onProgress?.(45, '继续处理照片')
    try { result = await canvasFallback(file, quality, options.signal) }
    catch (error) {
      checkCancelled(options.signal)
      throw new Error('这张照片暂时无法读取，请换一张照片，或先转为 JPEG / PNG 后重试。', { cause: error })
    }
  }
  checkCancelled(options.signal)
  options.onProgress?.(100, '压缩完成')
  return result
}

function startWaitingPhotos() {
  while (active < MAX_PARALLEL_COMPRESSION && waiting.length) {
    const task = waiting.shift()!
    task.removeWaitingAbort()
    if (task.options.signal?.aborted) { task.reject(cancelledPhotoOperation()); continue }
    active++
    void processPhoto(task).then(task.resolve, task.reject).finally(() => { active--; startWaitingPhotos() })
  }
}

export function compressPhoto(file: File, options: CompressionOptions = {}): Promise<CompressedPhoto> {
  return new Promise((resolve, reject) => {
    if (options.signal?.aborted) { reject(cancelledPhotoOperation()); return }
    const task: WaitingPhoto = { file, options, resolve, reject, removeWaitingAbort: () => {} }
    const abortWaiting = () => {
      const index = waiting.indexOf(task)
      if (index >= 0) {
        waiting.splice(index, 1)
        task.removeWaitingAbort()
        reject(cancelledPhotoOperation())
      }
    }
    task.removeWaitingAbort = () => options.signal?.removeEventListener('abort', abortWaiting)
    options.signal?.addEventListener('abort', abortWaiting, { once: true })
    options.onProgress?.(0, '等待处理')
    waiting.push(task)
    startWaitingPhotos()
  })
}
