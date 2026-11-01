import { describe, expect, it } from 'vitest'
import { istZeltOffen, leseZeltOffen, setzeZeltOffen } from './zelt-offen'

/**
 * Welche Zelte auf der Live-Seite aufgeklappt sind — am Gerät gemerkt.
 * Ohne Merkzettel gilt das Standardzelt; mit Merkzettel zählt, was gemerkt ist.
 */
describe('Zelte aufgeklappt (je Gerät)', () => {
  it('ohne Merkzettel ist nur das Standardzelt offen', () => {
    expect(istZeltOffen({}, 1, 1)).toBe(true)
    expect(istZeltOffen({}, 2, 1)).toBe(false)
  })

  it('ein gemerktes Zelt gilt vor dem Standard — auch ein zugeklapptes Standardzelt', () => {
    const gemerkt = setzeZeltOffen(setzeZeltOffen({}, 1, false), 2, true)
    expect(istZeltOffen(gemerkt, 1, 1)).toBe(false)
    expect(istZeltOffen(gemerkt, 2, 1)).toBe(true)
  })

  it('ohne Standardzelt (noch nicht geladen) ist nichts offen', () => {
    expect(istZeltOffen({}, 1, null)).toBe(false)
  })

  it('setzen verändert den Merkzettel nicht an Ort und Stelle', () => {
    const vorher = { '1': true }
    const nachher = setzeZeltOffen(vorher, 2, true)
    expect(vorher).toEqual({ '1': true })
    expect(nachher).toEqual({ '1': true, '2': true })
  })

  it('liest nur gültige Einträge und verträgt kaputte Ablage', () => {
    expect(leseZeltOffen(null)).toEqual({})
    expect(leseZeltOffen('kein json')).toEqual({})
    expect(leseZeltOffen('[1,2]')).toEqual({})
    expect(leseZeltOffen('{"1":true,"2":"ja","3":false}')).toEqual({ '1': true, '3': false })
  })
})
