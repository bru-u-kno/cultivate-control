import type { Aenderung, WochenwertSpalte } from '../wochenplan/wochenwerte-bearbeiten'

/**
 * Fork AI (A-029, Etappe 1): ein Programm ohne Grow auf der Seite „Plan“ —
 * die Rechnung ohne Oberfläche.
 *
 * Ein mitgeliefertes Programm bleibt unverändert; wer es ändert, bekommt eine
 * Kopie als eigenes Programm. Ein eigenes wird an Ort und Stelle geändert.
 */

export type ProgrammKurz = {
  id: string
  name: string
  hersteller: string
  /** Eigenes Programm (`eigen-…`): änderbar. */
  eigen: boolean
  wochen: number
  mitKlima: boolean
}

export type ProgrammWerte = {
  programmId: string
  programmName: string
  eigen: boolean
  /** Laufende Grows ohne eigenen Plan, die dieses Programm benutzen. */
  andereGrows: number
  spalten: WochenwertSpalte[]
}

export type ProgrammGespeichert = {
  werte: ProgrammWerte
  neuAngelegt: boolean
  /** Gespeichert, aber die Übergabe an Home Assistant ging nicht durch. */
  hinweis?: string | null
}

export const NAME_MAX = 80

/** Hat das Programm Wochenwerte, die sich bearbeiten lassen? */
export const hatRaster = (p: Pick<ProgrammKurz, 'wochen'>): boolean => p.wochen > 0

/** Die Zeile unter dem Programmnamen: was das Programm mitbringt. */
export function programmZeile(p: ProgrammKurz): string {
  if (!hatRaster(p)) return 'keine Wochenwerte · Ziele kommen aus dem Standard'
  const wochen = p.wochen === 1 ? '1 Woche' : `${p.wochen} Wochen`
  const klima = p.mitKlima ? 'alle Werte je Woche' : 'Klima aus dem Standard'
  return `${wochen} · ${klima}${p.eigen ? ' · eigenes Programm' : ''}`
}

/** Vorschlag für den Namen einer Kopie. */
export const kopieName = (programmName: string): string => `${programmName} (eigen)`

/** Beschriftung des Speichern-Knopfs: eine Kopie entsteht nur bei einem mitgelieferten Programm. */
export const speichernText = (eigen: boolean): string => (eigen ? 'Speichern' : 'Als eigenes Programm speichern')

/** Der Name ist in Ordnung? Leer gilt als in Ordnung (dann gilt der Vorschlag). */
export function nameFehler(name: string): string | null {
  return name.trim().length > NAME_MAX ? `Der Name darf höchstens ${NAME_MAX} Zeichen lang sein.` : null
}

/** Der Text nach dem Speichern. */
export function ergebnisText(antwort: ProgrammGespeichert, anzahl: number): string {
  const was = anzahl === 1 ? '1 Änderung' : `${anzahl} Änderungen`
  const satz = antwort.neuAngelegt
    ? `${was} gespeichert — als neues eigenes Programm „${antwort.werte.programmName}“. Die Vorlage bleibt unverändert.`
    : `${was} gespeichert in „${antwort.werte.programmName}“.`
  return antwort.hinweis ? `${satz} ${antwort.hinweis}` : satz
}

/** Der Rumpf der Speichern-Anfrage; der Name zählt nur bei einer neuen Kopie. */
export function speichernAnfrage(
  aenderungen: Aenderung[],
  eigen: boolean,
  name: string,
): { aenderungen: Aenderung[]; name?: string } {
  const sauber = name.trim()
  return eigen || sauber.length === 0 ? { aenderungen } : { aenderungen, name: sauber }
}

/** Wirkt eine Änderung sofort auf laufende Grows? Dann sagt die Seite es dazu. */
export function wirktAufGrows(werte: Pick<ProgrammWerte, 'eigen' | 'andereGrows'>): string | null {
  if (!werte.eigen || werte.andereGrows <= 0) return null
  return werte.andereGrows === 1
    ? 'Gilt sofort für 1 laufenden Grow ohne eigenen Plan.'
    : `Gilt sofort für ${werte.andereGrows} laufende Grows ohne eigenen Plan.`
}
