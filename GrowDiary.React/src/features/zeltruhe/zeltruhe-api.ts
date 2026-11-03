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
  /** Der Zweck dieses Zelts: ein Trockenzelt („Drying") hat weder Block noch Frage — seine Geräte folgen dem Trocknungsplan. */
  zeltTyp?: string | null
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

/** Je Zelt: ohne `zeltId` gilt das Zelt der Steuerung (wie bisher). */
const mitZelt = (pfad: string, zeltId?: number | null) => (zeltId == null ? pfad : `${pfad}?zeltId=${zeltId}`)

export const ladeZeltRuhe = (zeltId?: number | null) => apiFetch<ZeltRuheStand>(mitZelt('/api/steuerung/zelt-ruhe', zeltId))

/** Mit `geraete` gilt diese Auswahl (und wird gemerkt), ohne die gespeicherte. */
export const schalteRuhend = (geraete?: string[], zeltId?: number | null) =>
  apiFetch<ZeltRuheErgebnis>(mitZelt('/api/steuerung/zelt-ruhe/ruhend', zeltId), { method: 'POST', body: JSON.stringify(geraete ? { geraete } : {}) })

export const schalteAktiv = (zeltId?: number | null) => apiFetch<ZeltRuheErgebnis>(mitZelt('/api/steuerung/zelt-ruhe/aktiv', zeltId), { method: 'POST' })

export const speichereAuswahl = (geraete: string[], zeltId?: number | null) =>
  apiFetch<ZeltRuheStand>(mitZelt('/api/steuerung/zelt-ruhe/auswahl', zeltId), { method: 'PUT', body: JSON.stringify({ geraete }) })

/** Geräte, die im Ruhemodus ausgehen sollen und gerade laufen — nur danach fragt das Blatt nach der Ernte. */
export function laufendeAuswahl(stand: ZeltRuheStand | null | undefined): ZeltRuheGeraet[] {
  if (!stand || stand.ruhend || stand.zeltTyp === 'Drying') return []
  return stand.geraete.filter((g) => g.vorhanden && g.ausschalten && g.laeuft === true)
}
