import { describe, expect, it } from 'vitest'
import {
  NAME_MAX, ergebnisText, hatRaster, kopieName, nameFehler, programmZeile, speichernAnfrage, speichernText, wirktAufGrows,
  type ProgrammGespeichert, type ProgrammKurz,
} from './programm-plan'

const skx: ProgrammKurz = { id: 'skx-canna-aqua', name: 'SKX Canna Aqua', hersteller: 'SKX', eigen: false, wochen: 14, mitKlima: true }
const athena: ProgrammKurz = { ...skx, id: 'athena-blended', name: 'Athena Blended', wochen: 16, mitKlima: false }
const ohneRaster: ProgrammKurz = { ...skx, id: 'canna-aqua', name: 'Canna Aqua', wochen: 0, mitKlima: false }

describe('Programm ohne Grow: Auswahl', () => {
  it('sagt, ob sich ein Programm bearbeiten lässt', () => {
    expect(hatRaster(skx)).toBe(true)
    expect(hatRaster(ohneRaster)).toBe(false)
  })

  it('nennt zu jedem Programm, was es mitbringt', () => {
    expect(programmZeile(skx)).toBe('14 Wochen · alle Werte je Woche')
    expect(programmZeile(athena)).toBe('16 Wochen · Klima aus dem Standard')
    expect(programmZeile({ ...skx, eigen: true })).toBe('14 Wochen · alle Werte je Woche · eigenes Programm')
    expect(programmZeile({ ...skx, wochen: 1 })).toContain('1 Woche ·')
  })

  it('sagt bei einem Programm ohne Wochenwerte, warum es nichts zu ändern gibt', () => {
    expect(programmZeile(ohneRaster)).toBe('keine Wochenwerte · Ziele kommen aus dem Standard')
  })
})

describe('Programm ohne Grow: Speichern', () => {
  it('beschriftet den Knopf nach dem, was entsteht', () => {
    expect(speichernText(false)).toBe('Als eigenes Programm speichern')
    expect(speichernText(true)).toBe('Speichern')
  })

  it('schlägt für die Kopie einen Namen vor', () => {
    expect(kopieName('SKX Canna Aqua')).toBe('SKX Canna Aqua (eigen)')
  })

  it('schickt den Namen nur mit, wenn eine Kopie entsteht und einer eingetragen ist', () => {
    const a = [{ spalteId: 'w1', feld: 'airTempC', wert: 21 }]
    expect(speichernAnfrage(a, false, '  Meins  ')).toEqual({ aenderungen: a, name: 'Meins' })
    expect(speichernAnfrage(a, false, '   ')).toEqual({ aenderungen: a })
    // Ein eigenes Programm behält seinen Namen — auch wenn im Feld noch Text stand.
    expect(speichernAnfrage(a, true, 'Meins')).toEqual({ aenderungen: a })
  })

  it('lehnt einen zu langen Namen ab, nimmt einen leeren an', () => {
    expect(nameFehler('')).toBeNull()
    expect(nameFehler('x'.repeat(NAME_MAX))).toBeNull()
    expect(nameFehler('x'.repeat(NAME_MAX + 1))).toMatch(/höchstens 80 Zeichen/)
    // Leerraum zählt nicht mit — das Backend schneidet ihn ebenfalls ab.
    expect(nameFehler(`  ${'x'.repeat(NAME_MAX)}  `)).toBeNull()
  })

  it('berichtet, ob eine Kopie entstand', () => {
    const werte = { programmId: 'eigen-x', programmName: 'Meins', eigen: true, andereGrows: 0, spalten: [] }
    const neu: ProgrammGespeichert = { werte, neuAngelegt: true }
    const alt: ProgrammGespeichert = { werte, neuAngelegt: false }
    expect(ergebnisText(neu, 3)).toBe('3 Änderungen gespeichert — als neues eigenes Programm „Meins“. Die Vorlage bleibt unverändert.')
    expect(ergebnisText(alt, 1)).toBe('1 Änderung gespeichert in „Meins“.')
  })

  it('hängt den Hinweis zur Übergabe an Home Assistant an, wenn es einen gibt', () => {
    const werte = { programmId: 'eigen-x', programmName: 'Meins', eigen: true, andereGrows: 1, spalten: [] }
    const hinweis = 'Home Assistant war gerade nicht erreichbar — die Übergabe holt der nächste Lauf nach.'
    expect(ergebnisText({ werte, neuAngelegt: false, hinweis }, 1)).toBe(`1 Änderung gespeichert in „Meins“. ${hinweis}`)
    expect(ergebnisText({ werte, neuAngelegt: false, hinweis: null }, 1)).toBe('1 Änderung gespeichert in „Meins“.')
  })
})

describe('Programm ohne Grow: Wirkung auf laufende Grows', () => {
  it('schweigt bei Vorlagen und bei Programmen ohne Nutzer', () => {
    expect(wirktAufGrows({ eigen: false, andereGrows: 3 })).toBeNull()
    expect(wirktAufGrows({ eigen: true, andereGrows: 0 })).toBeNull()
  })

  it('warnt, wenn ein eigenes Programm auf laufende Grows wirkt', () => {
    expect(wirktAufGrows({ eigen: true, andereGrows: 1 })).toBe('Gilt sofort für 1 laufenden Grow ohne eigenen Plan.')
    expect(wirktAufGrows({ eigen: true, andereGrows: 2 })).toBe('Gilt sofort für 2 laufende Grows ohne eigenen Plan.')
  })
})
