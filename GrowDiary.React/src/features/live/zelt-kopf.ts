import type { MetricPayload } from '../../types'
import { urteilFuerMetrik } from './metric-tile-model'

/**
 * Was der zugeklappte Kopf eines Zelts auf der Live-Seite zeigt.
 *
 * <b>Dieselbe Lesart wie die Kachel.</b> Ton und Zahl kommen aus denselben
 * Messwerten und aus `urteilFuerMetrik` — dem Urteil, das auch die Kachel und
 * der Score der Seite benutzen. Ein eigener Schwellenvergleich im Kopf könnte
 * zugeklappt „im Ziel" sagen, wo die Kachel darunter gelb ist.
 *
 * <b>Nur, was es gibt.</b> Die Nährlösungs-Zeile erscheint, wenn das Zelt
 * Hydro-Werte meldet — nicht, weil es „ein RDWC-Zelt" ist. Ein Trockenzelt
 * hat keine, ein Zelt ohne zugeordnete Sonden auch nicht.
 */

export type KopfTon = 'ok' | 'warn' | 'bad' | ''

export type KopfWert = {
  key: string
  label: string
  wert: string
  einheit: string
  ton: KopfTon
}

export const KLIMA_KOPF = [
  { key: 'temperature', label: 'Luft' },
  { key: 'humidity', label: 'RLF' },
  { key: 'vpd', label: 'VPD' },
] as const

export const HYDRO_KOPF = [
  { key: 'reservoir-ph', label: 'pH' },
  { key: 'reservoir-ec', label: 'EC' },
  { key: 'reservoir-temp', label: 'Wasser' },
] as const

function hatWert(metric: MetricPayload): boolean {
  return Boolean(metric.value) && metric.value !== '–'
}

function tonFuer(metric: MetricPayload): KopfTon {
  const urteil = urteilFuerMetrik(metric)
  if (urteil === 'ok') return 'ok'
  if (urteil === 'warn') return 'warn'
  if (urteil === 'crit') return 'bad'
  return ''
}

export function kopfWerte(
  metriken: MetricPayload[],
  welche: ReadonlyArray<{ key: string; label: string }>,
): KopfWert[] {
  const ergebnis: KopfWert[] = []
  for (const { key, label } of welche) {
    const metric = metriken.find((eintrag) => eintrag.key === key)
    if (!metric || !hatWert(metric)) continue
    ergebnis.push({ key, label, wert: metric.value, einheit: metric.unit ?? '', ton: tonFuer(metric) })
  }
  return ergebnis
}

/**
 * Die Zeile unter dem Namen: wo das Zelt steht.
 *
 * Ein Zelt mit laufendem Grow nennt Phase und Plan-Woche. Ein Trockenzelt hat
 * keinen laufenden Grow — dort steht, was der Trocknungsplan an den Zielwerten
 * vermerkt („Trocknung · Tag 3 · Hauptphase"). Sonst bleibt die Zeile leer.
 */
export function zeltUnterzeile(
  stageLine: string | null,
  weekLine: string | null | undefined,
  metriken: MetricPayload[],
): string | null {
  const ausGrow = [stageLine, weekLine].filter(Boolean).join(' · ')
  if (ausGrow) return ausGrow
  const vermerk = metriken.find((metric) => metric.targetNote && !metric.targetDerived)?.targetNote
  return vermerk ?? null
}
