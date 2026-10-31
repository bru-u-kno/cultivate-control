import { formatNumber } from '../../utils'
import type { FertigStand } from './FertigErkennung'

/**
 * Die Worte der Fertig-Erkennung — rein, damit sie sich ohne Seite prüfen lassen.
 *
 * <b>Der Anlass (Prüfer, 31.10.2026).</b> Der Satz sagte immer „… Prozentpunkte weniger als bei der Wägung davor“, auch
 * wenn das Gewicht gestiegen war (feuchtere Probe, andere Waage). Das Vorzeichen steckt in `aenderungPunkte`; der Satz
 * muss es lesen.
 */
export function ueberschrift(stand: FertigStand): string {
  switch (stand.stufe) {
    case 'keine': return 'Noch keine Wägung'
    case 'erste': return 'Erste Wägung gespeichert'
    case 'haeltSich': return 'Gewicht hält sich'
    case 'ziel': return 'Ziel erreicht'
    default: return (stand.aenderungPunkte ?? 0) > 0 ? 'Gewicht ist gestiegen' : 'Gewicht sinkt noch'
  }
}

/** Der Satz unter der Überschrift bei „sinkt“ bzw. „gestiegen“: Wie viel, und in welche Richtung. */
export function verlaufSatz(stand: FertigStand, prozent: string | null): string {
  const was = stand.bezugFrischgewicht ? 'Charge' : 'Probe'
  const bezug = stand.bezugFrischgewicht ? 'des Frischgewichts' : 'ihrer ersten Wägung'
  const aenderung = stand.aenderungPunkte
  if (aenderung == null || aenderung === 0) return `Die ${was} wiegt ${prozent} % ${bezug}.`
  const betrag = formatNumber(Math.abs(aenderung), 1)
  return aenderung < 0
    ? `Die ${was} wiegt ${prozent} % ${bezug} — ${betrag} Prozentpunkte weniger als bei der Wägung davor.`
    : `Die ${was} wiegt ${prozent} % ${bezug} — ${betrag} Prozentpunkte mehr als bei der Wägung davor. Gleiche Probe, gleiche Waage?`
}
