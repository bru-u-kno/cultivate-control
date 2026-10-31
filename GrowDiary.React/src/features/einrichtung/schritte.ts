/**
 * cc.287: Die Schritte der Einrichtung — aus dem, was der Fork ohnehin liest, nicht aus festen Entitäten.
 *
 * Jeder Schritt ist eine Zählung („2 von 2 Zelten“), keine Prüfung gegen bestimmte Geräte: Ein Fremder mit anderen
 * Geräten sieht dieselbe Liste. Ohne Importe, damit die Tests ohne JSX-Einstellung sie lesen (`*.node.test.ts`).
 */
export type SchrittStatus = 'erledigt' | 'offen' | 'optional' | 'unbekannt'

export type Schritt = { id: string; titel: string; text: string; status: SchrittStatus; ziel: string }

/** Was gelesen werden konnte; `null` heißt „Abruf fehlgeschlagen“ (der Schritt zeigt dann „nicht lesbar“, nie „erledigt“). */
export type EinrichtungDaten = {
  verbindung: { verbunden: boolean; entitaeten: number | null } | null
  zelte: Array<{ id: number; name: string; status: string; tentType: string; messgroessen: string[] }> | null
  auswahl: Array<{ gewaehlt: boolean; pflichtZugeordnet: number; pflichtGesamt: number }> | null
  hydro: number | null
  wasserErfasst: boolean | null
  pushEingerichtet: boolean | null
  kiAktiv: boolean
}

const zahlWort = (n: number, einzahl: string, mehrzahl: string) => `${n} ${n === 1 ? einzahl : mehrzahl}`

const unbekannt = (id: string, titel: string, ziel: string): Schritt => ({ id, titel, text: 'nicht lesbar', status: 'unbekannt', ziel })

export function berechneSchritte(daten: EinrichtungDaten): Schritt[] {
  const schritte: Schritt[] = []

  const v = daten.verbindung
  schritte.push(v == null ? unbekannt('verbindung', 'Verbindung', '/geraete?reiter=verbindung') : {
    id: 'verbindung', titel: 'Verbindung', ziel: '/geraete?reiter=verbindung',
    status: v.verbunden && (v.entitaeten ?? 0) > 0 ? 'erledigt' : 'offen',
    text: !v.verbunden ? 'Home Assistant ist nicht verbunden' : (v.entitaeten ?? 0) > 0 ? `Home Assistant verbunden, ${v.entitaeten} Entitäten geladen` : 'verbunden, aber keine Antwort',
  })

  const zelte = daten.zelte?.filter((z) => z.status !== 'Archived') ?? null
  schritte.push(zelte == null ? unbekannt('zelte', 'Zelte & Räume', '/zelte') : {
    id: 'zelte', titel: 'Zelte & Räume', ziel: '/zelte',
    status: zelte.length > 0 ? 'erledigt' : 'offen',
    text: zelte.length > 0 ? zahlWort(zelte.length, 'Zelt angelegt', 'Zelte angelegt') : 'noch kein Zelt angelegt',
  })

  if (zelte == null) schritte.push(unbekannt('zuordnung', 'Geräte & Zuordnung', '/geraete?reiter=zuordnung'))
  else {
    const mitKern = zelte.filter((z) => z.messgroessen.includes('AirTemperature') && z.messgroessen.includes('Humidity'))
    schritte.push({
      id: 'zuordnung', titel: 'Geräte & Zuordnung', ziel: '/geraete?reiter=zuordnung',
      status: zelte.length > 0 && mitKern.length === zelte.length ? 'erledigt' : 'offen',
      text: zelte.length === 0 ? 'erst ein Zelt anlegen' : `${mitKern.length} von ${zelte.length} Zelten mit Temperatur und Feuchte`,
    })
  }

  const gewaehlt = daten.auswahl?.filter((e) => e.gewaehlt) ?? null
  if (gewaehlt == null) schritte.push(unbekannt('steuerungen', 'Steuerungen', '/steuerung'))
  else {
    const vollstaendig = gewaehlt.filter((e) => e.pflichtGesamt === 0 || e.pflichtZugeordnet >= e.pflichtGesamt).length
    schritte.push({
      id: 'steuerungen', titel: 'Steuerungen', ziel: '/steuerung',
      status: gewaehlt.length === 0 ? 'optional' : vollstaendig === gewaehlt.length ? 'erledigt' : 'offen',
      text: gewaehlt.length === 0 ? 'optional — noch keine Steuerung gewählt' : `${vollstaendig} von ${gewaehlt.length} gewählten Steuerungen vollständig`,
    })
  }

  schritte.push(daten.hydro == null ? unbekannt('hydro', 'Hydro-System', '/hydro') : {
    id: 'hydro', titel: 'Hydro-System', ziel: '/hydro',
    status: daten.hydro > 0 ? 'erledigt' : 'offen',
    text: daten.hydro > 0 ? zahlWort(daten.hydro, 'System angelegt', 'Systeme angelegt') : 'noch kein System angelegt',
  })

  schritte.push(daten.wasserErfasst == null ? unbekannt('wasser', 'Wasser', '/wasser') : {
    id: 'wasser', titel: 'Wasser', ziel: '/wasser',
    status: daten.wasserErfasst ? 'erledigt' : 'offen',
    text: daten.wasserErfasst ? 'Wasserprofil erfasst' : 'Wasserprofil fehlt',
  })

  schritte.push(daten.pushEingerichtet == null ? unbekannt('handy', 'Handy', '/handy') : {
    id: 'handy', titel: 'Handy', ziel: '/handy',
    status: daten.pushEingerichtet ? 'erledigt' : 'offen',
    text: daten.pushEingerichtet ? 'Push-Meldungen eingerichtet' : 'Push-Meldungen nicht eingerichtet',
  })

  if (daten.kiAktiv) schritte.push({ id: 'ki', titel: 'KI-Assistent', ziel: '/ki', status: 'optional', text: 'optional — den eigenen Assistenten verbinden' })

  return schritte.map((s, i) => ({ ...s, titel: `${i + 1} · ${s.titel}` }))
}

/** „4 von 7“ zählt nur Schritte, die man erledigen kann — optionale und nicht lesbare nicht. */
export function zaehlung(schritte: Schritt[]): { erledigt: number; gesamt: number; naechster: Schritt | null } {
  const zaehlbar = schritte.filter((s) => s.status === 'erledigt' || s.status === 'offen')
  return { erledigt: zaehlbar.filter((s) => s.status === 'erledigt').length, gesamt: zaehlbar.length, naechster: schritte.find((s) => s.status === 'offen') ?? null }
}
