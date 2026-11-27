import { setWorkerUrl, type StyleSpecification } from 'maplibre-gl'
import { MapLibreLayer } from '@geoblocks/ol-maplibre-layer'
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?url'
import 'maplibre-gl/dist/maplibre-gl.css'

setWorkerUrl(workerUrl)

export async function loadOfflineVectorStyle(url: string, signal: AbortSignal): Promise<StyleSpecification> {
  const response = await fetch(url, { signal, cache: 'no-store' })
  if (!response.ok) throw new Error('Style HTTP ' + response.status)
  const style = await response.json() as StyleSpecification
  if (style.version !== 8 || !style.sources || !Array.isArray(style.layers)) throw new Error('Invalid vector style')
  if (signal.aborted) throw new DOMException('Aborted', 'AbortError')
  return style
}

export function createStyledVectorLayer(style: StyleSpecification, forExport = false): MapLibreLayer {
  return new MapLibreLayer({
    mapLibreOptions: {
      style, localIdeographFontFamily: false,
      ...(forExport ? { pixelRatio: 1, fadeDuration: 0, canvasContextAttributes: { preserveDrawingBuffer: true } } : {}),
      transformRequest: url => {
        const resolved = new URL(url, window.location.href)
        if (resolved.origin !== window.location.origin || !resolved.pathname.startsWith('/api/map/'))
          throw new Error('Offline map styles must use local map resources')
        return { url: resolved.href }
      },
    },
  })
}

export async function externalVectorStyle(styleUrl: string, signal: AbortSignal, forExport = false) {
  const style = await loadOfflineVectorStyle(styleUrl, signal)
  return { layer: createStyledVectorLayer(style, forExport), release: () => {} }
}
