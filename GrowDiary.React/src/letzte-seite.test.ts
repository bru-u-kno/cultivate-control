import { beforeEach, describe, expect, it, vi } from 'vitest'
import { beruehreSeite, GUELTIG_MS, merkeSeite, wiederherstellungsZiel } from './letzte-seite'

const T0 = 1_700_000_000_000

describe('letzte Seite', () => {
  // Die Tests laufen in Node ohne Browser: ein einfacher Speicher stellt localStorage nach.
  beforeEach(() => {
    const speicher = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => speicher.get(k) ?? null,
      setItem: (k: string, v: string) => void speicher.set(k, v),
      removeItem: (k: string) => void speicher.delete(k),
    })
  })

  it('stellt eine frisch gemerkte Unterseite auf der Startseite wieder her', () => {
    merkeSeite('/tagebuch', T0)
    expect(wiederherstellungsZiel('/', '', '', T0 + 5 * 60_000)).toBe('/tagebuch')
  })

  it('behält die Abfrage der Seite', () => {
    merkeSeite('/steuerung/chiller?zelt=2', T0)
    expect(wiederherstellungsZiel('/', '', '', T0 + 1000)).toBe('/steuerung/chiller?zelt=2')
  })

  it('beginnt nach langer Pause wieder auf Live', () => {
    merkeSeite('/tagebuch', T0)
    expect(wiederherstellungsZiel('/', '', '', T0 + GUELTIG_MS + 1)).toBeNull()
  })

  it('berührt die Zeit, ohne die Seite zu ändern', () => {
    merkeSeite('/tagebuch', T0)
    beruehreSeite(T0 + 20 * 60_000)
    expect(wiederherstellungsZiel('/', '', '', T0 + 40 * 60_000)).toBe('/tagebuch')
  })

  it('leitet nicht um, wenn die App mit einem Ziel startet', () => {
    merkeSeite('/tagebuch', T0)
    expect(wiederherstellungsZiel('/aufgaben', '', '', T0 + 1000)).toBeNull()
    expect(wiederherstellungsZiel('/', '?zelt=2', '', T0 + 1000)).toBeNull()
    expect(wiederherstellungsZiel('/', '', '#x', T0 + 1000)).toBeNull()
  })

  it('bleibt auf Live, wenn Live zuletzt offen war', () => {
    merkeSeite('/tagebuch', T0)
    merkeSeite('/', T0 + 1000)
    expect(wiederherstellungsZiel('/', '', '', T0 + 2000)).toBeNull()
  })

  it('lehnt fremde oder kaputte Ziele ab', () => {
    for (const bose of ['//evil.example', 'https://evil.example', '/a b', 'javascript:1', '/x#y']) {
      localStorage.setItem('growos.letzteSeite', JSON.stringify({ pfad: bose, zeit: T0 }))
      expect(wiederherstellungsZiel('/', '', '', T0 + 1000)).toBeNull()
    }
    localStorage.setItem('growos.letzteSeite', '{kaputt')
    expect(wiederherstellungsZiel('/', '', '', T0 + 1000)).toBeNull()
  })

  it('eine nicht erlaubte Seite löscht den alten Eintrag statt ihn zu behalten', () => {
    merkeSeite('/tagebuch', T0)
    merkeSeite('/seite~mit:zeichen', T0 + 1000)
    beruehreSeite(T0 + 2000)
    expect(wiederherstellungsZiel('/', '', '', T0 + 3000)).toBeNull()
  })

  it('Grenze: genau 30 Minuten gelten noch, negatives Alter (Uhr zurück) nicht', () => {
    merkeSeite('/tagebuch', T0)
    expect(wiederherstellungsZiel('/', '', '', T0 + GUELTIG_MS)).toBe('/tagebuch')
    expect(wiederherstellungsZiel('/', '', '', T0 - 1)).toBeNull()
  })

  it('ohne Eintrag bleibt es auf Live (Selbsttest: die Prüfung sieht ihre Grundmenge)', () => {
    expect(wiederherstellungsZiel('/', '', '', T0)).toBeNull()
    merkeSeite('/tagebuch', T0)
    expect(wiederherstellungsZiel('/', '', '', T0)).not.toBeNull()
  })
})
