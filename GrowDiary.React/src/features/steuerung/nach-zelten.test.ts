import { describe, expect, it } from 'vitest'
import { gliedernNachZelten, pfadFuer, zeltZusammenfassung } from './nach-zelten'
import type { SteuerungModul } from './steuerung-typen'

const modul = (kennung: string, zeltId: number | undefined, status = 'an'): SteuerungModul => ({
  kennung, titel: kennung, status, kurz: '', wert: '', unterzeile: '', hatDetail: true, zeltId,
})
const ZELTE = [{ id: 1, name: 'Zelt A' }, { id: 2, name: 'Zelt B' }]

describe('gliedernNachZelten', () => {
  it('bleibt bei einer Liste, solange es weniger als zwei Zelte gibt', () => {
    expect(gliedernNachZelten([modul('co2', 1)], [{ id: 1, name: 'Zelt A' }])).toBeNull()
    expect(gliedernNachZelten([modul('co2', 1)], undefined)).toBeNull()
  })

  it('gruppiert je Zelt in der Reihenfolge der Zelte und lässt leere Zelte weg', () => {
    const gruppen = gliedernNachZelten([modul('licht', 2), modul('co2', 1), modul('chiller', 1)], [...ZELTE, { id: 3, name: 'Zelt C' }])!
    expect(gruppen.map((g) => g.zelt?.name)).toEqual(['Zelt A', 'Zelt B'])
    expect(gruppen[0].module.map((m) => m.kennung)).toEqual(['co2', 'chiller'])
  })

  it('stellt Steuerungen ohne (bekanntes) Zelt ans Ende in eine eigene Gruppe', () => {
    const gruppen = gliedernNachZelten([modul('co2', 1), modul('zuluft', undefined), modul('licht', 99)], ZELTE)!
    expect(gruppen.at(-1)?.zelt).toBeNull()
    expect(gruppen.at(-1)?.module.map((m) => m.kennung)).toEqual(['zuluft', 'licht'])
  })
})

describe('pfadFuer', () => {
  it('trägt das Zelt nur beim CO₂, und nur wenn es mehrere CO₂-Zeilen gibt', () => {
    const eins = [modul('co2', 1), modul('licht', 1)]
    expect(pfadFuer(eins[0], eins)).toBe('/steuerung/co2')
    const zwei = [modul('co2', 1), modul('co2', 2), modul('licht', 1)]
    expect(pfadFuer(zwei[1], zwei)).toBe('/steuerung/co2?zelt=2')
    expect(pfadFuer(zwei[2], zwei)).toBe('/steuerung/licht')
  })
})

describe('zeltZusammenfassung', () => {
  it('zählt an, aus und Hinweise', () => {
    expect(zeltZusammenfassung([modul('a', 1, 'an'), modul('b', 1, 'an'), modul('c', 1, 'aus'), modul('d', 1, 'warn')])).toBe('2 an · 1 aus · 1 Hinweis')
    expect(zeltZusammenfassung([modul('a', 1, 'an')])).toBe('1 an')
  })
})
