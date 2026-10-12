export const MAX_PHOTO_EDGE = 1600
export const DEFAULT_JPEG_QUALITY = 75
export const MAX_PARALLEL_COMPRESSION = 2
export const MAX_UPLOAD_BYTES = 10 * 1024 * 1024

export interface CompressedPhoto {
  blob: Blob
  thumbnail?: Blob
  width: number
  height: number
  engine: 'mozjpeg-worker' | 'canvas-fallback'
}

export interface CompressionOptions {
  quality?: number
  signal?: AbortSignal
  onProgress?: (percent: number, message: string) => void
}

export interface WorkerRequest { file: File; quality: number }

export type WorkerReply =
  | { kind: 'progress'; percent: number; message: string }
  | { kind: 'done'; jpeg: ArrayBuffer; width: number; height: number; thumbnail?: ArrayBuffer }
  | { kind: 'fallback'; reason: string }

export function cancelledPhotoOperation(): DOMException {
  return new DOMException('已取消照片处理。', 'AbortError')
}
