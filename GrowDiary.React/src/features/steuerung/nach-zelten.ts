import type { SteuerungModul, SteuerungZeltKurz } from './steuerung-typen'

/** Eine Gruppe der Übersicht: ein Zelt (oder `null` für „noch keinem Zelt zugewiesen“) mit seinen Steuerungen. */
export type ZeltGruppe = { zelt: SteuerungZeltKurz | null; module: SteuerungModul[] }

/**
 * Die Übersicht nach Zelten gliedern. Mit weniger als zwei Zelten bleibt es bei der einen Liste wie bisher (`null`).
 * Zelte ohne Steuerung erscheinen nicht; Steuerungen, deren Zelt es nicht (mehr) gibt, stehen am Ende in einer eigenen Gruppe.
 */
export function gliedernNachZelten(module: SteuerungModul[], zelte: SteuerungZeltKurz[] | undefined): ZeltGruppe[] | null {
  if (!zelte || zelte.length < 2) return null
  const gruppen: ZeltGruppe[] = zelte
    .map((zelt) => ({ zelt, module: module.filter((m) => m.zeltId === zelt.id) }))
    .filter((g) => g.module.length > 0)
  const bekannt = new Set(zelte.map((z) => z.id))
  const ohne = module.filter((m) => m.zeltId == null || !bekannt.has(m.zeltId))
  if (ohne.length > 0) gruppen.push({ zelt: null, module: ohne })
  return gruppen
}

/**
 * Wohin eine Zeile führt. Das CO₂ gibt es je Zelt: Gibt es mehr als eine CO₂-Zeile, trägt der Pfad das Zelt (`?zelt=`), sonst
 * bleibt er wie bisher.
 */
export function pfadFuer(modul: SteuerungModul, alle: SteuerungModul[]): string {
  const basis = `/steuerung/${modul.kennung}`
  if (modul.kennung === 'co2' && modul.zeltId != null && alle.filter((m) => m.kennung === 'co2').length > 1) {
    return `${basis}?zelt=${modul.zeltId}`
  }
  return basis
}

/** „3 an · 1 aus · 1 Hinweis“ — die Kurzauskunft im Kopf eines Zeltes. */
export function zeltZusammenfassung(module: SteuerungModul[]): string {
  const an = module.filter((m) => m.status === 'an').length
  const warn = module.filter((m) => m.status === 'warn').length
  const aus = module.length - an - warn
  return [`${an} an`, aus > 0 ? `${aus} aus` : null, warn > 0 ? `${warn} ${warn === 1 ? 'Hinweis' : 'Hinweise'}` : null].filter(Boolean).join(' · ')
}
