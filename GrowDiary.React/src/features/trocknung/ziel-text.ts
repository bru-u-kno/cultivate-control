import { formatNumber } from '../../utils'

/** Antwort von `GET /api/tents/{id}/trocknung/ziel` (A-029, Etappe 3, Teil A). */
export type ZielUrteil = 'nichtZugeordnet' | 'unbekannt' | 'geraetAus' | 'ohneAutomatik' | 'ausloeserAus' | 'passt' | 'weichtAb'
export interface ZielGeraet {
  rolle: 'entfeuchter' | 'kuehlung'
  urteil: ZielUrteil
  portName: string | null
  modus: string | null
  wertAmGeraet: number | null
  einheit: string | null
  planWert: number | null
}
export interface Ziel {
  trocknet: boolean
  tag: number | null
  etappe: string | null
  tempZiel: number | null
  feuchteZiel: number | null
  tempOben: number | null
  feuchteOben: number | null
  portsErreichbar: boolean
  geraete: ZielGeraet[]
}

/** Plakette je Urteil: Text und Ton (warn = gelb, ok = grün). */
export const URTEIL_TEXT: Record<ZielUrteil, { text: string; ton?: 'warn' | 'ok' }> = {
  nichtZugeordnet: { text: 'nicht zugeordnet' },
  unbekannt: { text: 'nicht lesbar', ton: 'warn' },
  geraetAus: { text: 'Gerät aus' },
  ohneAutomatik: { text: 'ohne Automatik', ton: 'warn' },
  ausloeserAus: { text: 'Auslöser aus', ton: 'warn' },
  passt: { text: 'passt', ton: 'ok' },
  weichtAb: { text: 'weicht ab', ton: 'warn' },
}

export const zahl = (wert: number) => formatNumber(wert, Number.isInteger(wert) ? 0 : 1)

/** Die Zeile „am Gerät“ je Rolle — mit dem, was der Zustand erlaubt zu sagen. */
export function amGeraetText(g: ZielGeraet): string {
  const einheit = g.rolle === 'entfeuchter' ? '%' : '°C'
  if (g.urteil === 'geraetAus') return 'Modus aus'
  if (g.urteil === 'ohneAutomatik') return g.modus ? `Modus ${g.modus}` : 'ohne Automatik'
  if (g.urteil === 'unbekannt' || g.wertAmGeraet == null) return '–'
  return g.urteil === 'ausloeserAus' ? `${zahl(g.wertAmGeraet)} ${einheit} (aus)` : `${zahl(g.wertAmGeraet)} ${einheit}`
}
