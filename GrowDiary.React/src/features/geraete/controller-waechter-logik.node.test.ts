import { describe, expect, it } from 'vitest'
import { ablaufText, hatSteckdose } from './controller-waechter-logik'

const basis = { modus: 'neustart' as const, meldenNachMin: 10, neustartNachMin: 15, ausDauerSek: 15, maxNeustartsProTag: 2 }

describe('Controller-Wächter: Steckdose gewählt?', () => {
  it('fehlendes Feld, null und leer heißen „keine“ (das Backend lässt leere Felder weg)', () => {
    expect(hatSteckdose({})).toBe(false)
    expect(hatSteckdose({ steckdoseEntity: null })).toBe(false)
    expect(hatSteckdose({ steckdoseEntity: '' })).toBe(false)
  })

  it('eine Entität heißt „gewählt“', () => {
    expect(hatSteckdose({ steckdoseEntity: 'switch.t_dose' })).toBe(true)
  })
})

describe('Controller-Wächter: was die Seite verspricht', () => {
  it('ohne Steckdose verspricht sie keinen Neustart — auch bei fehlendem Feld', () => {
    for (const steckdoseEntity of [undefined, null, '']) {
      const text = ablaufText({ ...basis, steckdoseEntity })
      expect(text).toContain('meldet nur')
      expect(text).not.toContain('ausgeschaltet')
    }
  })

  it('mit Steckdose und Neustart nennt sie Zeiten und Grenze', () => {
    const text = ablaufText({ ...basis, steckdoseEntity: 'switch.t_dose' })
    expect(text).toContain('nach 15 Min.')
    expect(text).toContain('15 Sek. ausgeschaltet')
    expect(text).toContain('höchstens 2× pro Tag')
  })

  it('im Modus „Nur melden“ ändert der Wächter nichts, auch mit Steckdose', () => {
    expect(ablaufText({ ...basis, modus: 'melden', steckdoseEntity: 'switch.t_dose' })).toContain('ändert nichts')
  })
})
