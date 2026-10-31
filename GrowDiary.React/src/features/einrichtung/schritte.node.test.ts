import { describe, expect, it } from 'vitest'
import { berechneSchritte, zaehlung } from './schritte'
import type { EinrichtungDaten } from './schritte'

const ganz: EinrichtungDaten = {
  verbindung: { verbunden: true, entitaeten: 34 },
  zelte: [
    { id: 1, name: 'A', status: 'Active', tentType: 'Production', messgroessen: ['AirTemperature', 'Humidity', 'ReservoirPh'] },
    { id: 2, name: 'B', status: 'Active', tentType: 'Drying', messgroessen: ['AirTemperature'] },
    { id: 3, name: 'C', status: 'Archived', tentType: 'Production', messgroessen: [] },
  ],
  auswahl: [{ gewaehlt: true, pflichtZugeordnet: 2, pflichtGesamt: 2 }, { gewaehlt: true, pflichtZugeordnet: 1, pflichtGesamt: 3 }, { gewaehlt: false, pflichtZugeordnet: 0, pflichtGesamt: 1 }],
  hydro: 1, wasserErfasst: false, pushEingerichtet: true, kiAktiv: true,
}
const status = (daten: EinrichtungDaten) => Object.fromEntries(berechneSchritte(daten).map((s) => [s.id, s.status]))

describe('Schritte der Einrichtung', () => {
  it('zählt je Schritt, nicht gegen feste Geräte', () => {
    const s = berechneSchritte(ganz)
    expect(s.map((x) => x.titel)).toEqual(['1 · Verbindung', '2 · Zelte & Räume', '3 · Geräte & Zuordnung', '4 · Steuerungen', '5 · Hydro-System', '6 · Wasser', '7 · Handy', '8 · KI-Assistent'])
    expect(status(ganz)).toEqual({ verbindung: 'erledigt', zelte: 'erledigt', zuordnung: 'offen', steuerungen: 'offen', hydro: 'erledigt', wasser: 'offen', handy: 'erledigt', ki: 'optional' })
    expect(s.find((x) => x.id === 'zuordnung')?.text).toBe('1 von 2 Zelten mit Temperatur und Feuchte')
    expect(s.find((x) => x.id === 'steuerungen')?.text).toBe('1 von 2 gewählten Steuerungen vollständig')
  })

  it('archivierte Zelte zählen nicht; ein nicht lesbarer Abruf ist nie „erledigt“', () => {
    const leer: EinrichtungDaten = { verbindung: null, zelte: null, auswahl: null, hydro: null, wasserErfasst: null, pushEingerichtet: null, kiAktiv: false }
    const s = berechneSchritte(leer)
    expect(s).toHaveLength(7)
    expect(s.every((x) => x.status === 'unbekannt')).toBe(true)
    expect(zaehlung(s)).toEqual({ erledigt: 0, gesamt: 0, naechster: null })
  })

  it('zählt erledigte gegen erledigbare und nennt den nächsten offenen Schritt', () => {
    const z = zaehlung(berechneSchritte(ganz))
    expect(z.erledigt).toBe(4)
    expect(z.gesamt).toBe(7)
    expect(z.naechster?.id).toBe('zuordnung')
  })

  it('ohne gewählte Steuerung ist der Schritt optional; ohne KI fehlt der KI-Schritt', () => {
    const s = status({ ...ganz, auswahl: [], kiAktiv: false })
    expect(s.steuerungen).toBe('optional')
    expect(s.ki).toBeUndefined()
  })
})
