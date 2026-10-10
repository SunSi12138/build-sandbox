import type { PhotoPhase, PhotoUploadDto } from '~/types/api'
import { cancelledPhotoOperation, MAX_UPLOAD_BYTES } from './types'

export interface UploadOptions {
  signal?: AbortSignal
  onProgress?: (percent: number) => void
}

export function uploadPhoto(blob: Blob, phase: PhotoPhase, options: UploadOptions = {}): Promise<PhotoUploadDto> {
  return new Promise((resolve, reject) => {
    if (options.signal?.aborted) { reject(cancelledPhotoOperation()); return }
    if (blob.size > MAX_UPLOAD_BYTES) { reject(new Error('压缩后的照片仍超过 10 MB，请在设置里调低 JPEG 质量后重试。')); return }
    const request = new XMLHttpRequest()
    let settled = false
    const abort = () => request.abort()
    function finish(error?: Error, photo?: PhotoUploadDto) {
      if (settled) return
      settled = true
      options.signal?.removeEventListener('abort', abort)
      if (error) reject(error)
      else if (photo) resolve(photo)
    }
    request.open('PUT', `/api/photos?phase=${encodeURIComponent(phase)}`)
    request.setRequestHeader('Content-Type', 'image/jpeg')
    request.timeout = 120_000
    request.upload.onprogress = (event) => {
      if (event.lengthComputable) options.onProgress?.(Math.round(event.loaded / event.total * 100))
    }
    request.onload = () => {
      let value: unknown
      try { value = JSON.parse(request.responseText) }
      catch { finish(new Error('无法确认照片是否上传成功，请重试。')); return }
      if (request.status < 200 || request.status >= 300) {
        const message = value && typeof value === 'object' && 'message' in value && typeof value.message === 'string'
          ? value.message : '照片上传失败，请稍后重试。'
        finish(new Error(message))
        return
      }
      if (!value || typeof value !== 'object' || !('id' in value) || !('width' in value) || !('height' in value)
        || typeof value.id !== 'string' || !/^[0-9a-f]{32}$/.test(value.id)
        || typeof value.width !== 'number' || value.width <= 0 || typeof value.height !== 'number' || value.height <= 0) {
        finish(new Error('照片上传结果不完整，请重试。'))
        return
      }
      options.onProgress?.(100)
      finish(undefined, { id: value.id, width: value.width, height: value.height })
    }
    request.onerror = () => finish(new Error('照片未能上传，请确认手机能连接办公室电脑后重试。'))
    request.ontimeout = () => finish(new Error('照片上传超时，请检查网络后重试。'))
    request.onabort = () => finish(cancelledPhotoOperation())
    options.signal?.addEventListener('abort', abort, { once: true })
    request.send(blob)
  })
}
