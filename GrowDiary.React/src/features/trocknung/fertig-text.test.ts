import { describe, expect, it } from 'vitest'
import { ueberschrift, verlaufSatz } from './fertig-text'
import type { FertigStand } from './FertigErkennung'

const stand = (zusatz: Partial<FertigStand>): FertigStand => ({
  trocknet: true, growId: 1, tag: 8, stufe: 'sinkt', bezugFrischgewicht: false, zielProzent: null, referenzG: 100,
  chargeWiegbar: false, aenderungPunkte: -11, naechsteWaegung: '2026-11-08', waegungFaellig: false, punkte: [], ...zusatz,
})

describe('Worte der Fertig-Erkennung', () => {
  it('sagt „weniger“, wenn das Gewicht fällt', () => {
    expect(verlaufSatz(stand({ aenderungPunkte: -11 }), '55')).toBe('Die Probe wiegt 55 % ihrer ersten Wägung — 11 Prozentpunkte weniger als bei der Wägung davor.')
    expect(ueberschrift(stand({ aenderungPunkte: -11 }))).toBe('Gewicht sinkt noch')
  })

  it('sagt „mehr“ und nicht „sinkt“, wenn das Gewicht steigt', () => {
    const s = stand({ aenderungPunkte: 2.5 })
    expect(verlaufSatz(s, '82')).toContain('2,5 Prozentpunkte mehr')
    expect(verlaufSatz(s, '82')).not.toContain('weniger')
    expect(ueberschrift(s)).toBe('Gewicht ist gestiegen')
  })

  it('nennt bei der ganzen Charge das Frischgewicht als Bezug', () => {
    expect(verlaufSatz(stand({ bezugFrischgewicht: true, aenderungPunkte: -7.3 }), '24')).toBe('Die Charge wiegt 24 % des Frischgewichts — 7,3 Prozentpunkte weniger als bei der Wägung davor.')
  })

  it('kommt ohne Änderung ohne Vergleichssatz aus', () => {
    expect(verlaufSatz(stand({ aenderungPunkte: null }), '100')).toBe('Die Probe wiegt 100 % ihrer ersten Wägung.')
  })
})
