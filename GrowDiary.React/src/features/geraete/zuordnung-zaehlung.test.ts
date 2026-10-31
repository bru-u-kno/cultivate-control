import { describe, expect, it } from 'vitest'
import type { TentDto } from '../../types'
import { definitions } from '../home-assistant/messgroessen'
import { kurzauskunft, offeneKernwerte, offeneZelte, standardOffen } from './zuordnung-zaehlung'

const zelt = (id: number, tentType: string, belegt: string[]): TentDto => ({ id, name: `Z${id}`, tentType, sensors: belegt.map((metricType) => ({ metricType, haEntityId: `sensor.${metricType}`, isActive: true })) } as unknown as TentDto)
const def = (metricType: string) => definitions.find((d) => d.metricType === metricType)!
const keinRaum = { raumZelte: new Set<number>() }

describe('Zählung der Zuordnung', () => {
  const zelte = [zelt(1, 'Production', ['AirTemperature', 'Humidity']), zelt(2, 'Drying', ['AirTemperature']), zelt(3, 'Production', [])]

  it('zählt offene Zellen je Messgröße; das Trockenzelt hat nur seine drei', () => {
    expect(offeneZelte(zelte, def('AirTemperature'), keinRaum).map((z) => z.id)).toEqual([3])
    expect(offeneZelte(zelte, def('Humidity'), keinRaum).map((z) => z.id)).toEqual([2, 3])
    expect(offeneZelte(zelte, def('ReservoirPh'), keinRaum).map((z) => z.id)).toEqual([1, 3])
  })

  it('VPD ist nie offen — der Fork rechnet ihn', () => {
    expect(offeneZelte(zelte, def('Vpd'), keinRaum)).toEqual([])
    expect(kurzauskunft(zelte, def('Vpd'), keinRaum)).toEqual({ text: 'wird gerechnet, wo leer', ton: undefined })
  })

  it('Außenwerte, die der Raum liefert, zählen nicht als offen', () => {
    expect(offeneZelte(zelte, def('OutsideTemperature'), keinRaum).map((z) => z.id)).toEqual([1, 3])
    expect(offeneZelte(zelte, def('OutsideTemperature'), { raumZelte: new Set([1]) }).map((z) => z.id)).toEqual([3])
  })

  it('Kurzauskunft und Ton: Kernwerte mahnen, optionale nicht', () => {
    expect(kurzauskunft(zelte, def('AirTemperature'), keinRaum)).toEqual({ text: '2 von 3 zugeordnet', ton: 'warn' })
    expect(kurzauskunft(zelte, def('Ppfd'), keinRaum)).toEqual({ text: '0 von 2 zugeordnet', ton: undefined })
    expect(kurzauskunft([zelte[0]], def('AirTemperature'), keinRaum)).toEqual({ text: 'zugeordnet', ton: 'ok' })
  })

  it('die Zahl am Reiter zählt nur offene Kernwerte; aufgeklappt wird nur bei Lücke in einem Kernwert', () => {
    expect(offeneKernwerte(zelte, definitions, 'tent', keinRaum)).toBe(3)
    expect(standardOffen(zelte, def('AirTemperature'), keinRaum)).toBe(true)
    expect(standardOffen(zelte, def('Ppfd'), keinRaum)).toBe(false)
    expect(standardOffen([zelte[0]], def('AirTemperature'), keinRaum)).toBe(false)
  })
})
