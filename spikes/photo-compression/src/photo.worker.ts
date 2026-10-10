import encodeJpeg, { init as initJpeg } from '@jsquash/jpeg/encode.js'
import resize, { initResize } from '@jsquash/resize'
import jpegWasmUrl from '@jsquash/jpeg/codec/enc/mozjpeg_enc.wasm?url'
import resizeWasmUrl from '@jsquash/resize/lib/resize/pkg/squoosh_resize_bg.wasm?url'
import { MAX_EDGE, type WorkerReply } from './types'

const scope = self as unknown as DedicatedWorkerGlobalScope

function report(percent: number, step: string) {
  scope.postMessage({ kind: 'progress', percent, step } satisfies WorkerReply)
}

scope.onmessage = async (event: MessageEvent<{ file: File; quality: number; forceFallback: boolean }>) => {
  let bitmap: ImageBitmap | undefined
  let canvas: OffscreenCanvas | undefined
  try {
    if (event.data.forceFallback) throw new Error('测试：模拟 WASM 初始化失败')
    report(10, '解码照片并处理方向')
    bitmap = await createImageBitmap(event.data.file)
    canvas = new OffscreenCanvas(bitmap.width, bitmap.height)
    const ctx = canvas.getContext('2d', { willReadFrequently: true })
    if (!ctx) throw new Error('此浏览器不支持 Worker 画布')
    // JPEG does not support alpha. Transparent input becomes white, not black.
    ctx.fillStyle = '#ffffff'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(bitmap, 0, 0)
    bitmap.close()
    bitmap = undefined
    let pixels = ctx.getImageData(0, 0, canvas.width, canvas.height)
    const ratio = Math.min(1, MAX_EDGE / Math.max(canvas.width, canvas.height))
    const width = Math.max(1, Math.round(canvas.width * ratio))
    const height = Math.max(1, Math.round(canvas.height * ratio))
    canvas.width = canvas.height = 1
    report(25, '初始化本地压缩器')
    await initResize(resizeWasmUrl)
    if (pixels.width !== width || pixels.height !== height) {
      report(40, '缩小照片')
      // No contain crop and no offset TypedArray views: avoid the upstream crop mutation path.
      pixels = await resize(pixels, { width, height, method: 'lanczos3', fitMethod: 'stretch' })
    }
    report(70, '编码 JPEG')
    // Explicit local URL is emitted by Vite; no dynamic CDN or nested worker.
    await initJpeg({ locateFile: () => jpegWasmUrl })
    const jpeg = await encodeJpeg(pixels, { quality: event.data.quality })
    scope.postMessage({ kind: 'done', jpeg, width, height } satisfies WorkerReply, [jpeg])
  } catch (error) {
    scope.postMessage({ kind: 'fallback', reason: error instanceof Error ? error.message : String(error) } satisfies WorkerReply)
  } finally {
    bitmap?.close()
    if (canvas) canvas.width = canvas.height = 1
  }
}
