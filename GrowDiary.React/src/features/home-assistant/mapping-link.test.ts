import { describe, expect, it } from 'vitest'
import { lesenMappingZiel, mappingLink } from './mapping-link'

describe('Mapping-Link', () => {
  it('baut Zelt und Messgröße in die Adresse', () => {
    expect(mappingLink(2, 'Humidity')).toBe('/home-assistant?zelt=2&messwert=Humidity')
    expect(mappingLink(2)).toBe('/home-assistant?zelt=2')
    expect(mappingLink()).toBe('/home-assistant')
  })

  it('liest sie wieder und verwirft Unbekanntes', () => {
    const ziel = lesenMappingZiel(new URLSearchParams('zelt=2&messwert=Humidity'))
    expect(ziel.tentId).toBe(2)
    expect(ziel.definition?.group).toBe('tent')
    expect(lesenMappingZiel(new URLSearchParams('zelt=abc&messwert=Quatsch'))).toEqual({ tentId: null, definition: null })
    expect(lesenMappingZiel(new URLSearchParams(''))).toEqual({ tentId: null, definition: null })
  })
})
