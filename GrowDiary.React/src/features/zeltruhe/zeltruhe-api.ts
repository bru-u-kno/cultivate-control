import { apiFetch } from '../../api'

/** Fork AI (cc.277, „Zelt ruhend"): Ein Gerät, wie das Backend es meldet. */
export interface ZeltRuheGeraet {
  kennung: string
  titel: string
  vorhanden: boolean
  /** Der Zustand in Worten: „aus", „an", „Zeitplan", „nicht erreichbar" … */
  jetzt: string
  laeuft?: boolean | null
  /** Steht es in der Auswahl „im Ruhemodus ausschalten"? */
  ausschalten: boolean
  /** Hat der Fork es beim Ruhendschalten ausgeschaltet? */
  gemerkt: boolean
}

export interface ZeltRuheStand {
  ruhend: boolean
  seitUtc?: string | null
  /** Das Zelt, zu dem die Steuerung gehört — der Block steht nur dort. */
  zeltId?: number | null
  haErreichbar: boolean
  geraete: ZeltRuheGeraet[]
}

export interface ZeltRuheSchritt {
  kennung: string
  titel: string
  ok: boolean
  text: string
}

export interface ZeltRuheErgebnis {
  ok: boolean
  zusammenfassung: string
  schritte: ZeltRuheSchritt[]
  stand: ZeltRuheStand
}

export const ladeZeltRuhe = () => apiFetch<ZeltRuheStand>('/api/steuerung/zelt-ruhe')

/** Mit `geraete` gilt diese Auswahl (und wird gemerkt), ohne die gespeicherte. */
export const schalteRuhend = (geraete?: string[]) =>
  apiFetch<ZeltRuheErgebnis>('/api/steuerung/zelt-ruhe/ruhend', { method: 'POST', body: JSON.stringify(geraete ? { geraete } : {}) })

export const schalteAktiv = () => apiFetch<ZeltRuheErgebnis>('/api/steuerung/zelt-ruhe/aktiv', { method: 'POST' })

export const speichereAuswahl = (geraete: string[]) =>
  apiFetch<ZeltRuheStand>('/api/steuerung/zelt-ruhe/auswahl', { method: 'PUT', body: JSON.stringify({ geraete }) })

/** Geräte, die im Ruhemodus ausgehen sollen und gerade laufen — nur danach fragt das Blatt nach der Ernte. */
export function laufendeAuswahl(stand: ZeltRuheStand | null | undefined): ZeltRuheGeraet[] {
  if (!stand || stand.ruhend) return []
  return stand.geraete.filter((g) => g.vorhanden && g.ausschalten && g.laeuft === true)
}
