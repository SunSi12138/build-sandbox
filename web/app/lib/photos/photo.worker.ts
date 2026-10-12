/// <reference path="../../../node_modules/@jsquash/jpeg/emscripten-types.d.ts" />
import encodeJpeg, { init as initJpeg } from '@jsquash/jpeg/encode.js'
import resize, { initResize } from '@jsquash/resize'
import jpegWasmUrl from '@jsquash/jpeg/codec/enc/mozjpeg_enc.wasm?url'
import resizeWasmUrl from '@jsquash/resize/lib/resize/pkg/squoosh_resize_bg.wasm?url'
import { MAX_PHOTO_EDGE, type WorkerReply, type WorkerRequest } from './types'

interface CompressionWorkerScope {
  onmessage: ((event: MessageEvent<WorkerRequest>) => void) | null
  postMessage(message: WorkerReply, transfer?: Transferable[]): void
}

const scope = self as unknown as CompressionWorkerScope

function report(percent: number, message: string) {
  scope.postMessage({ kind: 'progress', percent, message })
}

scope.onmessage = async (event) => {
  let bitmap: ImageBitmap | undefined
  let canvas: OffscreenCanvas | undefined
  try {
    report(10, '读取照片')
    // The browser applies EXIF orientation here, including mirrored orientations.
    bitmap = await createImageBitmap(event.data.file)
    canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
    const context = canvas.getContext('2d', { willReadFrequently: true })
    if (!context) throw new Error('此浏览器无法在后台处理照片。')
    context.fillStyle = 'white'
    context.fillRect(0, 0, canvas.width, canvas.height)
    context.drawImage(bitmap, 0, 0)
    bitmap.close()
    bitmap = undefined
    let pixels = context.getImageData(0, 0, canvas.width, canvas.height)
    const ratio = Math.min(1, MAX_PHOTO_EDGE / Math.max(canvas.width, canvas.height))
    const width = Math.max(1, Math.round(canvas.width * ratio))
    const height = Math.max(1, Math.round(canvas.height * ratio))
    canvas.width = canvas.height = 1

    report(25, '准备压缩')
    // Explicit Vite URLs keep both single-threaded WASM modules on this server.
    await initResize(resizeWasmUrl)
    if (pixels.width !== width || pixels.height !== height) {
      report(40, '调整照片尺寸')
      pixels = await resize(pixels, { width, height, method: 'lanczos3', fitMethod: 'stretch' })
    }
    report(70, '压缩照片')
    await initJpeg({ locateFile: () => jpegWasmUrl })
    // Encoding fresh pixels strips EXIF and produces Word-compatible JPEG.
    const jpeg = await encodeJpeg(pixels, { quality: event.data.quality })
    let thumbnail: ArrayBuffer | undefined
    try {
      const thumbRatio = Math.min(1, 360 / Math.max(width, height))
      const thumbPixels = await resize(pixels, { width: Math.max(1, Math.round(width * thumbRatio)), height: Math.max(1, Math.round(height * thumbRatio)), method: 'lanczos3', fitMethod: 'stretch' })
      thumbnail = await encodeJpeg(thumbPixels, { quality: 70 })
    } catch { /* A thumbnail is optional; the original JPEG remains valid. */ }
    scope.postMessage({ kind: 'done', jpeg, width, height, thumbnail }, thumbnail ? [jpeg, thumbnail] : [jpeg])
  } catch (error) {
    scope.postMessage({ kind: 'fallback', reason: error instanceof Error ? error.message : String(error) })
  } finally {
    bitmap?.close()
    if (canvas) canvas.width = canvas.height = 1
  }
}
