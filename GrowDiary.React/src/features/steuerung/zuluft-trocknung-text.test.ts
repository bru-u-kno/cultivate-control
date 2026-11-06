import { describe, expect, it } from 'vitest'
import { zuluftErklaerung, zuluftPlakette, type ZuluftTrocknung } from './zuluft-trocknung-text'

const basis: ZuluftTrocknung = {
  trockenzelt: true, tentId: 2, zeltName: 'Zelt-Trocknung', trocknet: true, folgt: true, regelt: true, hoechststufe: 4, abstandProzent: 3,
  zeltTempC: 17.1, zeltRhProzent: 64, zeltAbsolutGm3: 9.3, aussenAbsolutGm3: 6.7, planRhProzent: 60, planTempC: 18, tag: 8, tageGeplant: 10,
  zustand: 'saugt', stufe: 3, differenz: 2.6, laeuftStufe: 3, gescheitert: false,
}

describe('zuluft-trocknung-text', () => {
  it('nicht eingebunden: grau, Regelung wie bisher', () => {
    const s = { ...basis, folgt: false }
    expect(zuluftPlakette(s)).toEqual({ text: 'nicht eingebunden', ton: 'grau' })
    expect(zuluftErklaerung(s)).toContain('wie bisher nach der Kellerluft')
  })

  it('eingebunden, aber nichts trocknet: wartet', () => {
    const s = { ...basis, trocknet: false }
    expect(zuluftPlakette(s).text).toBe('wartet auf Trocknung')
    expect(zuluftErklaerung(s)).toContain('trocknet gerade nichts')
  })

  it('saugt: Zahlen von jetzt im Satz', () => {
    expect(zuluftPlakette(basis)).toEqual({ text: 'saugt', ton: 'ok' })
    const t = zuluftErklaerung(basis)
    expect(t).toContain('64')
    expect(t).toContain('60')
    expect(t).toContain('2,6')
    expect(t).toContain('Stufe 3')
  })

  it('jeder Zustand hat Plakette und Satz (Zählung über die Grundmenge)', () => {
    const alle = ['saugt', 'zeltZuKalt', 'frost', 'zeltZuTrocken', 'zeltImPlan', 'aussenFeuchter', 'keinMesswert'] as const
    expect(alle.length).toBe(7)
    const saetze = new Set<string>()
    for (const zustand of alle) {
      const s = { ...basis, zustand }
      expect(zuluftPlakette(s).text.length).toBeGreaterThan(2)
      saetze.add(zuluftErklaerung(s))
    }
    expect(saetze.size).toBe(7)
  })

  it('ein gescheiterter Schaltvorgang geht vor dem Zustand', () => {
    expect(zuluftPlakette({ ...basis, gescheitert: true })).toEqual({ text: 'schaltet nicht', ton: 'warn' })
  })

  it('Zu kalt nennt die Grenze und die Rückkehr bei plus 1 °C', () => {
    expect(zuluftErklaerung({ ...basis, zustand: 'zeltZuKalt', zeltTempC: 14.2 })).toContain('14,2')
  })
})
