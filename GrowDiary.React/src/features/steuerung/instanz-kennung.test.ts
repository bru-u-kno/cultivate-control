import { describe, expect, it } from 'vitest'
import { INSTANZ_BEZEICHNUNG, INSTANZ_WEIBLICH, istChillerInstanz, istEntfeuchterInstanz, istZuluftInstanz } from './steuerung-typen'

describe('Kennungen weiterer Instanzen', () => {
  it('erkennt weitere Entfeuchter, aber weder den ersten noch den Zusatz-Entfeuchter noch Wasserkühler', () => {
    expect(istEntfeuchterInstanz('entfeuchter-i2')).toBe(true)
    expect(istEntfeuchterInstanz('entfeuchter-i17')).toBe(true)
    for (const nein of ['entfeuchter', 'entfeuchter-zusatz', 'entfeuchter-zusatz-i2', 'entfeuchter-i1', 'entfeuchter-i', 'entfeuchtung', 'chiller-i2', undefined]) {
      expect(istEntfeuchterInstanz(nein)).toBe(false)
    }
  })

  it('verwechselt die zwei Arten nicht', () => {
    expect(istChillerInstanz('entfeuchter-i2')).toBe(false)
    expect(istChillerInstanz('chiller-i2')).toBe(true)
    expect(istChillerInstanz('chiller-i12')).toBe(true) // ab Nummer 10 wie das Backend
    expect(istChillerInstanz('chiller-i1')).toBe(false)
  })

  it('nennt jede Art mit ihrem Wort', () => {
    expect(INSTANZ_BEZEICHNUNG).toEqual({ chiller: 'Wasserkühler', entfeuchter: 'Entfeuchter', zuluft: 'Zuluft' })
    expect(INSTANZ_WEIBLICH).toEqual({ chiller: false, entfeuchter: false, zuluft: true })
  })

  it('erkennt weitere Zuluft, aber weder die erste noch andere Arten', () => {
    expect(istZuluftInstanz('zuluft-i2')).toBe(true)
    expect(istZuluftInstanz('zuluft-i12')).toBe(true)
    for (const nein of ['zuluft', 'zuluft-i1', 'zuluft-i', 'zuluft-neu', 'chiller-i2', 'entfeuchter-i2', undefined]) {
      expect(istZuluftInstanz(nein)).toBe(false)
    }
    expect(istChillerInstanz('zuluft-i2')).toBe(false)
    expect(istEntfeuchterInstanz('zuluft-i2')).toBe(false)
  })
})
