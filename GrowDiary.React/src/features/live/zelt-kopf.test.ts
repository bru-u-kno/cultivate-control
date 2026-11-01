import { describe, expect, it } from 'vitest'
import { HYDRO_KOPF, KLIMA_KOPF, kopfWerte, zeltUnterzeile } from './zelt-kopf'
import { urteilFuerMetrik } from './metric-tile-model'
import type { MetricPayload } from '../../types'

function metrik(key: string, value: string, extra: Partial<MetricPayload> = {}): MetricPayload {
  return {
    key, label: key, value, unit: null, tone: 'default', hint: null,
    numericValue: Number(value.replace(',', '.')), ...extra,
  } as MetricPayload
}

describe('Kopfwerte eines Zelts', () => {
  it('Ton kommt aus demselben Urteil wie die Kachel', () => {
    const metriken = [
      metrik('temperature', '25,3', { unit: '°C', targetMin: 23, targetMax: 23, numericValue: 25.3 }),
      metrik('humidity', '45', { unit: '%', targetMin: 40, targetMax: 55, numericValue: 45 }),
      metrik('vpd', '1,2', { unit: 'kPa', numericValue: 1.2 }),
    ]
    const werte = kopfWerte(metriken, KLIMA_KOPF)
    // Der Ton darf nie vom Urteil der Kachel abweichen.
    for (const wert of werte) {
      const metric = metriken.find((m) => m.key === wert.key)!
      const urteil = urteilFuerMetrik(metric)
      expect(wert.ton).toBe(urteil === 'ok' ? 'ok' : urteil === 'warn' ? 'warn' : urteil === 'crit' ? 'bad' : '')
    }
    expect(werte.map((w) => w.label)).toEqual(['Luft', 'RLF', 'VPD'])
    expect(werte.find((w) => w.key === 'humidity')?.ton).toBe('ok')
    expect(werte.find((w) => w.key === 'temperature')?.ton).toBe('warn')
    // Ohne Zielband gibt es kein Urteil — der Wert bleibt neutral.
    expect(werte.find((w) => w.key === 'vpd')?.ton).toBe('')
  })

  it('lässt Werte ohne Messung weg — keine Hydro-Zeile ohne Hydro-Werte', () => {
    const trockenzelt = [metrik('temperature', '18,9', { unit: '°C' }), metrik('humidity', '–', { numericValue: null })]
    expect(kopfWerte(trockenzelt, HYDRO_KOPF)).toEqual([])
    expect(kopfWerte(trockenzelt, KLIMA_KOPF).map((w) => w.key)).toEqual(['temperature'])
  })

  it('Hydro-Zeile nennt pH, EC und Wasser in dieser Reihenfolge', () => {
    const metriken = [
      metrik('reservoir-temp', '19,1', { unit: '°C' }),
      metrik('reservoir-ec', '1,53', { unit: 'mS/cm' }),
      metrik('reservoir-ph', '5,88'),
    ]
    expect(kopfWerte(metriken, HYDRO_KOPF).map((w) => w.label)).toEqual(['pH', 'EC', 'Wasser'])
  })
})

describe('Unterzeile eines Zelts', () => {
  it('nennt Phase und Plan-Woche, wenn ein Grow läuft', () => {
    expect(zeltUnterzeile('Blüte Tag 36', 'Blütewoche 6 von 8–9', [])).toBe('Blüte Tag 36 · Blütewoche 6 von 8–9')
  })

  it('nimmt beim Trockenzelt den Vermerk des Trocknungsplans', () => {
    const metriken = [metrik('temperature', '18,9', { targetNote: 'Trocknung · Tag 3 · Hauptphase' })]
    expect(zeltUnterzeile(null, null, metriken)).toBe('Trocknung · Tag 3 · Hauptphase')
  })

  it('bleibt leer, wenn es nichts zu sagen gibt', () => {
    expect(zeltUnterzeile(null, null, [metrik('temperature', '20')])).toBeNull()
  })
})
