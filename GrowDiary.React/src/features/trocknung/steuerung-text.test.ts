import { describe, expect, it } from 'vitest'
import { geraetHinweis, plakette, wuerdeSchreiben, type SteuerungGeraet } from './steuerung-text'

const g = (teil: Partial<SteuerungGeraet>): SteuerungGeraet => ({
  rolle: 'kuehlung', portName: 'Port K', folgt: true, modus: 'Auto', wertAmGeraet: 24, einheit: '°C', planWert: 21,
  urteil: 'weichtAb', vorher: null, zuletztGesetzt: null, ...teil,
})

describe('Trocknung: Steuerung', () => {
  it('Plakette: ausgeschaltet bleibt der Wert des Nutzers, eingeschaltet kommt es auf die Betriebsart an', () => {
    expect(plakette(g({ folgt: false }), 'melden').text).toBe('bleibt')
    expect(plakette(g({ folgt: false }), 'regeln').text).toBe('eigener Wert')
    expect(plakette(g({}), 'melden').text).toBe('würde ändern')
    expect(plakette(g({}), 'regeln').text).toBe('wird nachgezogen')
    expect(plakette(g({ urteil: 'passt' }), 'regeln')).toEqual({ text: 'folgt dem Plan', ton: 'ok' })
    expect(plakette(g({ urteil: 'nichtZugeordnet' }), 'regeln').text).toBe('nicht zugeordnet')
    expect(plakette(g({ urteil: 'unbekannt' }), 'regeln').ton).toBe('warn')
  })

  it('Was geschrieben würde: nur Geräte, die folgen und abweichen', () => {
    const geraete = [g({ rolle: 'entfeuchter', wertAmGeraet: 50, planWert: 62 }), g({})]
    expect(wuerdeSchreiben(geraete, { entfeuchter: false, kuehlung: true })).toEqual([
      { rolle: 'kuehlung', portName: 'Port K', feld: 'Temperatur-Obergrenze', von: 24, nach: 21, einheit: '°C' },
    ])
    expect(wuerdeSchreiben(geraete, { entfeuchter: false, kuehlung: false })).toEqual([])
    expect(wuerdeSchreiben([g({ urteil: 'passt' })], { entfeuchter: false, kuehlung: true })).toEqual([])
  })

  it('Hinweis: nennt den Modus, wenn der Port nicht auf Auto steht, und sagt, dass der Modus nie geändert wird', () => {
    expect(geraetHinweis(g({ folgt: false }))).toContain('bleibt unangetastet')
    expect(geraetHinweis(g({ modus: 'Off' }))).toContain('Der Modus bleibt Aus')
    expect(geraetHinweis(g({ modus: 'Auto' }))).toContain('nie den Modus')
  })
})
