import { describe, expect, it } from 'vitest'
import { baueZeltAnsicht } from './zelt-ansicht'
import { initialLiveState, type LiveState } from './live-model'
import type { GrowSummary, MetricPayload, TentDto, TentLivePayload } from '../../types'

const zelt = (id: number) => ({ id, name: `Zelt ${id}` }) as TentDto
function metrik(key: string, value: string, extra: Partial<MetricPayload> = {}): MetricPayload {
  return { key, label: key, value, unit: null, tone: 'default', hint: null, numericValue: Number(value.replace(',', '.')), ...extra } as MetricPayload
}
function zustand(metriken: MetricPayload[], grows: GrowSummary[] = []): LiveState {
  return { ...initialLiveState, tents: [zelt(2)], grows, liveByTentId: { 2: { tentId: 2, metrics: metriken } as TentLivePayload } }
}

describe('Ansicht eines Zelts', () => {
  it('ein Zelt ohne Nährlösung zeigt kein Hydro-Band voller Platzhalter', () => {
    const a = baueZeltAnsicht(zelt(2), zustand([metrik('temperature', '18,9'), metrik('humidity', '50')]), [])
    expect(a.hydroMetrics).toEqual([])
  })

  it('meldet das Zelt Hydro-Werte, bleibt das Band', () => {
    const a = baueZeltAnsicht(zelt(2), zustand([metrik('reservoir-ph', '5,9'), metrik('temperature', '24')]), [])
    expect(a.hydroMetrics.length).toBeGreaterThan(0)
  })

  it('läuft ein Hydro-Grow im Zelt, bleiben die Platzhalter — dort heißt fehlend: etwas fehlt', () => {
    const grow = { id: 5, tentId: 2, status: 'Running', hydroStyle: 'RDWC' } as GrowSummary
    const a = baueZeltAnsicht(zelt(2), zustand([metrik('temperature', '24')], [grow]), [])
    expect(a.hydroMetrics.length).toBeGreaterThan(0)
  })

  it('der Score ist derselbe wie auf der Zeltseite (buildScore), Sensoren werden gezählt', () => {
    const a = baueZeltAnsicht(zelt(2), zustand([metrik('temperature', '18,9'), metrik('humidity', '–', { numericValue: null })]), [])
    expect(a.sensorsLive).toBe(1)
    expect(a.score.tone).toBeDefined()
  })
})
