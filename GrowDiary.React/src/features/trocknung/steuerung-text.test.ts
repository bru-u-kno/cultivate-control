import { describe, expect, it } from 'vitest'
import { geraetHinweis, plakette, schalterVon, wuerdeSchreiben, type SteuerungGeraet } from './steuerung-text'

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
    expect(geraetHinweis(g({ modus: 'Off', urteil: 'geraetAus' }))).toContain('der Fork schreibt nichts')
    expect(geraetHinweis(g({ modus: 'Off', urteil: 'geraetAus' }))).toContain('Sobald du den Port auf Auto stellst')
    expect(geraetHinweis(g({ modus: 'Auto' }))).toContain('nie den Modus')
    expect(plakette(g({ urteil: 'geraetAus' }), 'regeln').text).toBe('Gerät aus')
  })

  it('Abluft: ein Schalter für beide Grenzen, jede Zeile erscheint in der Rückfrage', () => {
    expect(schalterVon('abluft-feuchte')).toBe('abluft')
    expect(schalterVon('abluft-temp')).toBe('abluft')
    expect(schalterVon('kuehlung')).toBe('kuehlung')
    const geraete = [
      g({ rolle: 'abluft-feuchte', portName: 'Port A', wertAmGeraet: 55, planWert: 62, einheit: '%' }),
      g({ rolle: 'abluft-temp', portName: 'Port A', wertAmGeraet: 24, planWert: 21 }),
    ]
    expect(wuerdeSchreiben(geraete, { abluft: true })).toEqual([
      { rolle: 'abluft-feuchte', portName: 'Port A', feld: 'Luftfeuchte-Obergrenze', von: 55, nach: 62, einheit: '%' },
      { rolle: 'abluft-temp', portName: 'Port A', feld: 'Temperatur-Obergrenze', von: 24, nach: 21, einheit: '°C' },
    ])
    expect(wuerdeSchreiben(geraete, { entfeuchter: true, kuehlung: true })).toEqual([])
  })
})
