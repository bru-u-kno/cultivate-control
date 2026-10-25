import { describe, expect, it } from 'vitest'
import { anzeigeLive, entwurfAbgleichen, modusZielOffen, stufeAusFeld, stufeBasis, stufeSchritt } from './licht-bedienung'

describe('stufeAusFeld', () => {
  it('liest ganze Zahlen von 1 bis 10', () => {
    expect(stufeAusFeld('1')).toBe(1)
    expect(stufeAusFeld('10')).toBe(10)
    expect(stufeAusFeld(' 7 ')).toBe(7)
  })
  it('alles andere ist keine Stufe', () => {
    expect(stufeAusFeld('')).toBeNull()
    expect(stufeAusFeld('0')).toBeNull()
    expect(stufeAusFeld('11')).toBeNull()
    expect(stufeAusFeld('2.5')).toBeNull()
    expect(stufeAusFeld('abc')).toBeNull()
  })
})

describe('entwurfAbgleichen', () => {
  const alt = { veggieEin: '05:00', veggieAus: '23:00', stufe: 7, verifySekunden: 20 }

  it('behält eine ungespeicherte Änderung, wenn ein Befehl die Stufe ändert', () => {
    const entwurf = { ...alt, veggieEin: '06:00' }
    const neu = { ...alt, stufe: 8 }
    expect(entwurfAbgleichen(entwurf, alt, neu)).toEqual({ ...alt, veggieEin: '06:00', stufe: 8 })
  })

  it('ohne Änderung gilt die Antwort des Backends', () => {
    const neu = { ...alt, stufe: 3, veggieAus: '22:00' }
    expect(entwurfAbgleichen({ ...alt }, alt, neu)).toEqual(neu)
  })

  it('ein geleertes Zahlenfeld (NaN) bleibt leer', () => {
    const entwurf = { ...alt, verifySekunden: Number.NaN }
    expect(entwurfAbgleichen(entwurf, alt, { ...alt, stufe: 2 }).verifySekunden).toBeNaN()
  })
})

describe('stufeBasis / stufeSchritt (− und + hinter dem Livewert)', () => {
  it('ohne gemerktes Ziel zählt der Livewert, sonst der Entwurf', () => {
    expect(stufeBasis(6, null, 4, false)).toBe(6)
    expect(stufeBasis(null, null, 4, false)).toBe(4)
  })
  it('solange der Livewert noch auf dem alten Stand steht, zählt das gesendete Ziel', () => {
    // 6 → „+" sendet 7; Home Assistant meldet noch 6.
    expect(stufeBasis(6, { ziel: 7, basis: 6 }, 6, false)).toBe(7)
    // Der zweite Klick muss 8 senden, nicht noch einmal 7.
    expect(stufeSchritt(stufeBasis(6, { ziel: 7, basis: 6 }, 6, false), +1)).toBe(8)
  })
  it('hat der Livewert sich bewegt, gilt er wieder', () => {
    expect(stufeBasis(7, { ziel: 7, basis: 6 }, 6, false)).toBe(7)
    expect(stufeBasis(3, { ziel: 7, basis: 6 }, 6, false)).toBe(3)
  })
  it('ein gescheiterter Befehl hält das Ziel nicht fest', () => {
    expect(stufeBasis(6, { ziel: 7, basis: 6 }, 6, true)).toBe(6)
  })
  it('bleibt zwischen 1 und 10', () => {
    expect(stufeSchritt(1, -1)).toBe(1)
    expect(stufeSchritt(10, +1)).toBe(10)
  })
})

describe('anzeigeLive', () => {
  const live = { modus: 'Off', lichtAn: false, fehlgeschlagen: [] as string[] }
  const ziel = { ziel: 'On', basis: 'Off', seitMs: 1000 }

  it('zeigt den gesendeten Modus, solange der Livewert noch der alte ist', () => {
    expect(anzeigeLive(live, ziel)).toEqual({ modus: 'On', lichtAn: true, fehlgeschlagen: [] })
  })
  it('traut dem Livewert, sobald er sich bewegt hat', () => {
    const neu = { ...live, modus: 'On', lichtAn: true }
    expect(anzeigeLive(neu, ziel)).toBe(neu)
    expect(modusZielOffen(neu, ziel, 5000)).toBe(false)
  })
  it('die Frist läuft ab (Nachladen löscht das Ziel) und ein gescheiterter Befehl zählt nicht', () => {
    expect(modusZielOffen(live, ziel, 40000)).toBe(false)
    const kaputt = { ...live, fehlgeschlagen: ['x'] }
    expect(anzeigeLive(kaputt, ziel)).toBe(kaputt)
  })
  it('ohne Ziel bleibt alles beim Livewert', () => {
    expect(anzeigeLive(live, null)).toBe(live)
  })
})
