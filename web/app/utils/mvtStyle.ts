import type { FeatureLike } from 'ol/Feature'
import { Fill, Stroke, Style, Circle, Text } from 'ol/style'

type Paint = Record<string, unknown>
type StyleEntry = { type?: string; 'source-layer'?: string; paint?: Paint; layout?: Paint }
const stringValue = (value: unknown, fallback: string) => typeof value === 'string' ? value : fallback
const numberValue = (value: unknown, fallback: number) => typeof value === 'number' && Number.isFinite(value) ? value : fallback

/** Supports plain colors, numeric widths and text fields in local MVT layer styles. */
export function createMvtStyle(json: string) {
  let rules: StyleEntry[] = []
  try {
    const parsed = JSON.parse(json || '{}') as { layers?: StyleEntry[]; [key: string]: unknown }
    if (Array.isArray(parsed.layers)) rules = parsed.layers
    else rules = Object.entries(parsed).filter(([, item]) => item && typeof item === 'object').map(([name, item]) => {
      const props = item as Record<string, unknown>
      return { 'source-layer': name, type: stringValue(props.type, 'fill'), paint: {
        'fill-color': props.fill, 'fill-outline-color': props.stroke, 'line-color': props.stroke,
        'line-width': props.width, 'circle-color': props.circle, 'text-color': props.text,
      }, layout: { 'text-field': props.label } }
    })
  } catch { /* Server validates the JSON before saving. */ }
  const style = (feature: FeatureLike): Style[] => {
    const layerName = String(feature.get('layer') ?? '')
    const type = feature.getGeometry()?.getType() ?? ''
    const matches = rules.filter(rule => !rule['source-layer'] || rule['source-layer'] === layerName)
    const selected = matches.length ? matches : [{ type: type.includes('Point') ? 'circle' : type.includes('Line') ? 'line' : 'fill', paint: {} }]
    return selected.flatMap(rule => {
      const paint = rule.paint ?? {}, layout = rule.layout ?? {}
      const kind = rule.type ?? 'fill'
      if (kind === 'background') return []
      if (kind === 'line') return [new Style({ stroke: new Stroke({ color: stringValue(paint['line-color'], '#64748b'), width: numberValue(paint['line-width'], 1.5) }) })]
      if (kind === 'circle') return [new Style({ image: new Circle({ radius: numberValue(paint['circle-radius'], 3), fill: new Fill({ color: stringValue(paint['circle-color'], '#64748b') }), stroke: new Stroke({ color: stringValue(paint['circle-stroke-color'], '#fff'), width: numberValue(paint['circle-stroke-width'], 0) }) }) })]
      if (kind === 'symbol') {
        const field = stringValue(layout['text-field'], '{name}')
        const text = field.replace(/\{([^}]+)\}/g, (_, key: string) => String(feature.get(key) ?? ''))
        if (!text) return []
        return [new Style({ text: new Text({ text, font: '12px sans-serif', fill: new Fill({ color: stringValue(paint['text-color'], '#1f2937') }), stroke: new Stroke({ color: stringValue(paint['text-halo-color'], '#fff'), width: numberValue(paint['text-halo-width'], 2) }) }) })]
      }
      return [new Style({ fill: new Fill({ color: stringValue(paint['fill-color'], '#d9e3e2') }), stroke: new Stroke({ color: stringValue(paint['fill-outline-color'], '#94a3b8'), width: 0.6 }) })]
    })
  }
  return (feature: FeatureLike) => style(feature)
}
