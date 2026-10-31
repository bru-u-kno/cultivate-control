import { describe, expect, it } from 'vitest'
import { amGeraetText, URTEIL_TEXT, zahl, type ZielGeraet, type ZielUrteil } from './ziel-text'

const g = (zusatz: Partial<ZielGeraet>): ZielGeraet => ({
  rolle: 'entfeuchter', urteil: 'weichtAb', portName: 'Testport Entfeuchter', modus: 'Auto', wertAmGeraet: 50, einheit: '%', planWert: 62, ...zusatz,
})

describe('Worte des Trocknungs-Ziels', () => {
  it('zeigt ganze Zahlen ohne und andere mit einer Nachkommastelle, deutsch', () => {
    expect(zahl(50)).toBe('50')
    expect(zahl(20.5)).toBe('20,5')
  })

  it('nennt den Wert am Gerät mit der Einheit der Rolle', () => {
    expect(amGeraetText(g({}))).toBe('50 %')
    expect(amGeraetText(g({ rolle: 'kuehlung', wertAmGeraet: 21 }))).toBe('21 °C')
    expect(amGeraetText(g({ urteil: 'ausloeserAus', wertAmGeraet: 21, rolle: 'kuehlung' }))).toBe('21 °C (aus)')
  })

  it('sagt bei ausgeschaltetem Gerät, im Dauerbetrieb und ohne Lesung etwas Ehrliches statt einer Zahl', () => {
    expect(amGeraetText(g({ urteil: 'geraetAus', wertAmGeraet: null }))).toBe('Modus aus')
    expect(amGeraetText(g({ urteil: 'ohneAutomatik', modus: 'On', wertAmGeraet: null }))).toBe('Modus On')
    expect(amGeraetText(g({ urteil: 'unbekannt', wertAmGeraet: null }))).toBe('–')
  })

  it('hat zu jedem Urteil des Servers eine Plakette — und kennt keines, das der Server nicht schickt', () => {
    const vomServer: ZielUrteil[] = ['nichtZugeordnet', 'unbekannt', 'geraetAus', 'ohneAutomatik', 'ausloeserAus', 'passt', 'weichtAb']
    expect(Object.keys(URTEIL_TEXT).sort()).toEqual([...vomServer].sort())
    expect(URTEIL_TEXT.weichtAb).toEqual({ text: 'weicht ab', ton: 'warn' })
    expect(URTEIL_TEXT.passt.ton).toBe('ok')
  })
})
