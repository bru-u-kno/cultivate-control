import { useMemo, useState } from 'react'

import { inZeichenflaeche, punktBeiX, zeitpunktText } from '../features/verlauf/diagramm-auswahl'

export type HistoryPoint = { t: string; v: number; min?: number | null; max?: number | null }

export type HistorySeries = {
  metricKey: string
  label: string
  unit?: string | null
  points: HistoryPoint[]
}

export type TentHistory = {
  tentId: number
  resolution: 'daily' | 'raw'
  fromUtc: string
  toUtc: string
  series: HistorySeries[]
}

type Scale = { x: (t: number) => number; y: (v: number) => number }

// A fixed viewBox keeps strokes and dots undistorted while the SVG scales with its
// container (width: 100%). No charting library — smaller bundle, and it inherits the
// app's own colours instead of looking like a foreign widget.
const W = 600
const H = 170
const PAD = { top: 12, right: 10, bottom: 20, left: 38 }

function buildScale(points: HistoryPoint[], target?: { min?: number | null; max?: number | null }): Scale | null {
  if (points.length === 0) return null

  const times = points.map((point) => new Date(point.t).getTime())
  const lows = points.map((point) => (point.min ?? point.v))
  const highs = points.map((point) => (point.max ?? point.v))
  if (target?.min != null) lows.push(target.min)
  if (target?.max != null) highs.push(target.max)

  const tMin = Math.min(...times)
  const tMax = Math.max(...times)
  let vMin = Math.min(...lows)
  let vMax = Math.max(...highs)

  // Never draw a flat line on the axis: give a constant series some breathing room.
  if (vMax - vMin < 1e-9) { vMin -= 0.5; vMax += 0.5 }
  const headroom = (vMax - vMin) * 0.12
  vMin -= headroom
  vMax += headroom

  const spanT = tMax - tMin || 1
  const innerW = W - PAD.left - PAD.right
  const innerH = H - PAD.top - PAD.bottom

  return {
    x: (t) => PAD.left + ((t - tMin) / spanT) * innerW,
    y: (v) => PAD.top + (1 - (v - vMin) / (vMax - vMin)) * innerH,
  }
}

function linePath(points: HistoryPoint[], scale: Scale): string {
  return points
    .map((point, index) => `${index === 0 ? 'M' : 'L'}${scale.x(new Date(point.t).getTime()).toFixed(1)},${scale.y(point.v).toFixed(1)}`)
    .join(' ')
}

function bandPath(points: HistoryPoint[], scale: Scale): string | null {
  if (!points.some((point) => point.min != null && point.max != null)) return null
  const top = points.map((point) => `${scale.x(new Date(point.t).getTime()).toFixed(1)},${scale.y(point.max ?? point.v).toFixed(1)}`)
  const bottom = [...points].reverse().map((point) => `${scale.x(new Date(point.t).getTime()).toFixed(1)},${scale.y(point.min ?? point.v).toFixed(1)}`)
  return `M${top.join(' L')} L${bottom.join(' L')} Z`
}

/**
 * Eine Zahl an der Achse — mit deutschem Komma.
 *
 * `toFixed` und `String` schreiben IMMER mit Punkt. An den Achsen stand
 * deshalb „5.80" und „1.24" mitten in einer deutschen Oberflaeche — dieselbe
 * Falle, die im Container schon einmal 80 Saetze mit englischem Dezimalpunkt
 * ausgeliefert hat.
 */
/**
 * Ein Messwert, wie er unter dem Diagramm steht.
 *
 * <b>Gleich viele Nachkommastellen fuer die ganze Reihe.</b> Vorher entschied
 * jeder Wert fuer sich: neben „5,90" stand dann „6" statt „6,00", weil 6 zufaellig
 * eine ganze Zahl ist. Beim Antippen wandern die Werte durch die Reihe, und der
 * Sprung von zwei Stellen auf keine sieht aus wie ein anderer Messwert.
 */
function formatValue(value: number): string {
  const rounded = Math.round(value * 100) / 100
  const stellen = Math.abs(rounded) < 10 ? 2 : 1

  return new Intl.NumberFormat('de-DE', {
    minimumFractionDigits: stellen,
    maximumFractionDigits: stellen,
  }).format(rounded)
}

function formatDay(value: string): string {
  return new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit' }).format(new Date(value))
}

/**
 * One metric over time: the min/max band (daily resolution), the median/value line, the
 * target range if known, and the latest reading called out.
 */
export function SensorChart({
  series,
  target,
  height = 170,
  resolution = 'raw',
}: {
  series: HistorySeries
  target?: { min?: number | null; max?: number | null }
  height?: number
  /** Für den Zeitpunkt unter dem Diagramm — Tageswerte brauchen keine Uhrzeit. */
  resolution?: 'daily' | 'raw'
}) {
  const { points, label, unit } = series
  const scale = useMemo(() => buildScale(points, target), [points, target])

  /* Der angetippte Punkt.
     Am Handy gibt es kein Hover: ohne diese Auswahl ist das Diagramm dort
     stumm — man sieht eine Kurve und erfährt keine Zahl. Der Wert bleibt
     stehen, bis woanders hingetippt wird. */
  const [gewaehlt, setGewaehlt] = useState<number | null>(null)

  if (!scale || points.length === 0) {
    return (
      <div className="rc2-measurement-note" style={{ padding: '18px 0', textAlign: 'center' }}>
        Noch keine Verlaufsdaten für {label}.
      </div>
    )
  }

  const band = bandPath(points, scale)
  const last = points[points.length - 1]

  function waehleBei(klientX: number, kasten: DOMRect) {
    const treffer = punktBeiX(points, inZeichenflaeche(klientX, kasten.left, kasten.width, W), scale!.x)
    setGewaehlt(treffer ? treffer.index : null)
  }

  const zeiger = gewaehlt != null && gewaehlt < points.length ? points[gewaehlt] : null
  const values = points.map((point) => point.v)
  const lowest = Math.min(...values)
  const highest = Math.max(...values)
  const targetTop = target?.max != null ? scale.y(target.max) : null
  const targetBottom = target?.min != null ? scale.y(target.min) : null

  return (
    <figure style={{ margin: 0 }}>
      <svg
        viewBox={`0 0 ${W} ${H}`}
        style={{ width: '100%', height: 'auto', maxHeight: height, display: 'block', cursor: 'pointer', touchAction: 'manipulation' }}
        role="img"
        tabIndex={0}
        aria-label={`Verlauf ${label}: zuletzt ${formatValue(last.v)}${unit ? ` ${unit}` : ''}. Antippen zeigt den Wert an dieser Stelle.`}
        onPointerDown={(event) => waehleBei(event.clientX, event.currentTarget.getBoundingClientRect())}
        onKeyDown={(event) => {
          /* Mit der Tastatur durch die Reihe: ein Diagramm, das nur auf
             Tippen hört, ist mit der Tastatur unbedienbar. */
          if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
            event.preventDefault()
            const schritt = event.key === 'ArrowRight' ? 1 : -1
            const jetzt = gewaehlt ?? points.length - 1
            setGewaehlt(Math.min(points.length - 1, Math.max(0, jetzt + schritt)))
          }
          if (event.key === 'Escape') setGewaehlt(null)
        }}>
        {/* target range */}
        {targetTop != null && targetBottom != null && (
          <rect x={PAD.left} y={Math.min(targetTop, targetBottom)} width={W - PAD.left - PAD.right}
            height={Math.abs(targetBottom - targetTop)} fill="rgba(67, 212, 90, 0.10)" />
        )}
        {/* min/max band */}
        {band && <path d={band} fill="rgba(67, 212, 90, 0.14)" />}
        {/* axis frame */}
        <line x1={PAD.left} y1={H - PAD.bottom} x2={W - PAD.right} y2={H - PAD.bottom} stroke="var(--v1-line)" strokeWidth="1" />
        {/* value line */}
        <path d={linePath(points, scale)} fill="none" stroke="var(--v1-green)" strokeWidth="2"
          strokeLinejoin="round" strokeLinecap="round" />
        {/* latest reading */}
        <circle cx={scale.x(new Date(last.t).getTime())} cy={scale.y(last.v)} r="3.5" fill="var(--v1-green)" />
        {/* der angetippte Punkt — Linie und Ring, damit er nicht nur an der
            Farbe zu erkennen ist */}
        {zeiger && (
          <>
            <line
              x1={scale.x(new Date(zeiger.t).getTime())} y1={PAD.top}
              x2={scale.x(new Date(zeiger.t).getTime())} y2={H - PAD.bottom}
              stroke="var(--v1-muted)" strokeWidth="1" strokeDasharray="3 3" />
            <circle
              cx={scale.x(new Date(zeiger.t).getTime())} cy={scale.y(zeiger.v)}
              r="5" fill="none" stroke="var(--v1-text)" strokeWidth="2" />
          </>
        )}
        {/* scale labels */}
        <text x={PAD.left - 6} y={scale.y(highest) + 4} textAnchor="end" fontSize="11" fill="var(--v1-muted)">{formatValue(highest)}</text>
        <text x={PAD.left - 6} y={scale.y(lowest) + 4} textAnchor="end" fontSize="11" fill="var(--v1-muted)">{formatValue(lowest)}</text>
        <text x={PAD.left} y={H - 6} fontSize="11" fill="var(--v1-muted)">{formatDay(points[0].t)}</text>
        <text x={W - PAD.right} y={H - 6} textAnchor="end" fontSize="11" fill="var(--v1-muted)">{formatDay(last.t)}</text>
      </svg>
      <figcaption style={{ display: 'flex', justifyContent: 'space-between', gap: 10, marginTop: 4 }}>
        <span className="rc2-measurement-note">{label}{unit ? ` (${unit})` : ''}</span>
        {zeiger ? (
          <span className="rc2-measurement-note">
            {zeitpunktText(zeiger.t, resolution)}{' '}
            <strong style={{ color: 'var(--v1-text)' }}>{formatValue(zeiger.v)}</strong>{unit ? ` ${unit}` : ''}
          </span>
        ) : (
          <span className="rc2-measurement-note">zuletzt <strong style={{ color: 'var(--v1-text)' }}>{formatValue(last.v)}</strong>{unit ? ` ${unit}` : ''}</span>
        )}
      </figcaption>
    </figure>
  )
}

/**
 * A tiny inline curve for dashboard tiles — line only, no axes.
 * `hoeheCss` setzt die angezeigte Höhe aus einem Token (z. B. `var(--kurve-hoehe)`);
 * `height` bleibt die Rechenhöhe des viewBox — die Linie wird gestreckt, nicht neu gerechnet.
 */
export function Sparkline({ points, width = 120, height = 30, hoeheCss }: { points: HistoryPoint[]; width?: number; height?: number; hoeheCss?: string }) {
  const path = useMemo(() => {
    if (points.length < 2) return null
    const times = points.map((point) => new Date(point.t).getTime())
    const values = points.map((point) => point.v)
    const tMin = Math.min(...times)
    const spanT = (Math.max(...times) - tMin) || 1
    const vMin = Math.min(...values)
    const spanV = (Math.max(...values) - vMin) || 1
    return points
      .map((point, index) => {
        const x = ((times[index] - tMin) / spanT) * width
        const y = height - 2 - ((point.v - vMin) / spanV) * (height - 4)
        return `${index === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`
      })
      .join(' ')
  }, [points, width, height])

  if (!path) return null

  return (
    <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" aria-hidden="true"
      style={{ display: 'block', width: '100%', height: hoeheCss ?? height, opacity: 0.9 }}>
      {/* non-scaling-stroke keeps the line crisp when the viewBox is stretched to fit */}
      <path d={path} fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinejoin="round"
        strokeLinecap="round" vectorEffect="non-scaling-stroke" />
    </svg>
  )
}
