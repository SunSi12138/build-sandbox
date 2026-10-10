export const MAX_EDGE = 1600
export const JPEG_QUALITY = 75
export const MAX_PARALLEL = 2

export interface CompressedPhoto {
  blob: Blob
  width: number
  height: number
  engine: 'mozjpeg-worker' | 'canvas-fallback'
}

export type WorkerReply =
  | { kind: 'progress'; percent: number; step: string }
  | { kind: 'done'; jpeg: ArrayBuffer; width: number; height: number }
  | { kind: 'fallback'; reason: string }

export interface CompressionOptions {
  quality?: number
  onProgress?: (percent: number, step: string) => void
  /** Only used to exercise the fallback in the spike. */
  forceFallback?: boolean
}
