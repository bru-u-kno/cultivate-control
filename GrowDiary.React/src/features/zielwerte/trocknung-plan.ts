import { istLeer, zahlOderNull } from '../../zahlenfeld'
import { alsText } from '../wochenplan/wochenwerte-bearbeiten'

/**
 * Fork AI (A-029, Etappe 2): die Trocknung eines Programms — die Rechnung ohne Oberfläche.
 *
 * Die Etappen liegen im Programm (Name, Dauer in Tagen, Temperatur- und Feuchteziel, Obergrenze der
 * Feuchte). Der Entwurf hält den **getippten Text** je Feld, nicht die Zahl: „1," ist beim Tippen ein
 * Zwischenstand und darf nicht zu 1 werden, solange der Nutzer noch schreibt.
 *
 * Die Bereichsgrenzen (Temperatur, Feuchte …) prüft das Backend; die Oberfläche zeigt dessen Satz.
 * Zweimal dieselben Grenzen zu führen hieße, sie irgendwann zweimal zu pflegen.
 */

/** Eine Etappe, wie das Backend sie liefert und annimmt. */
export type TrockenEtappe = {
  name: string
  dauerTage: number
  lufttemperaturC: number
  luftfeuchteZiel: number
  luftfeuchteMax: number
}

export type TrocknungLaeuft = { zelt: string; tag: number; etappeNr: number; etappeName: string; gehalten: boolean }

export type Trocknung = {
  programmId: string
  programmName: string
  eigen: boolean
  /** Das Programm führt keine eigenen Etappen — angezeigt wird der Standard. */
  istStandard: boolean
  maxTage: number
  maxEtappen: number
  etappen: TrockenEtappe[]
  standard: TrockenEtappe[]
  laeuft: TrocknungLaeuft[]
}

export type TrocknungGespeichert = { trocknung: Trocknung; neuAngelegt: boolean }

/** Eine Etappe im Entwurf: Text je Feld. */
export type EtappeEntwurf = { name: string; dauer: string; temp: string; ziel: string; max: string }

export const NAME_MAX = 60

export function entwurfAus(etappen: readonly TrockenEtappe[]): EtappeEntwurf[] {
  return etappen.map((e) => ({
    name: e.name,
    dauer: alsText(e.dauerTage),
    temp: alsText(e.lufttemperaturC),
    ziel: alsText(e.luftfeuchteZiel),
    max: alsText(e.luftfeuchteMax),
  }))
}

/** Die Zahl im Feld — oder `null`, wenn das Feld leer oder unlesbar ist. */
const zahl = (text: string): number | null => (istLeer(text) ? null : zahlOderNull(text))

/** Der Entwurf als Etappen für das Backend; `null`, wenn ein Feld keine Zahl ist. */
export function etappenAus(entwurf: readonly EtappeEntwurf[]): TrockenEtappe[] | null {
  const liste: TrockenEtappe[] = []
  for (const e of entwurf) {
    const dauer = zahl(e.dauer)
    const temp = zahl(e.temp)
    const ziel = zahl(e.ziel)
    const max = zahl(e.max)
    if (dauer === null || temp === null || ziel === null || max === null) return null
    liste.push({ name: e.name.trim(), dauerTage: dauer, lufttemperaturC: temp, luftfeuchteZiel: ziel, luftfeuchteMax: max })
  }
  return liste
}

/** Weicht der Entwurf von dem ab, was gespeichert ist? Ein nur angetipptes Feld zählt nicht. */
export function istGeaendert(entwurf: readonly EtappeEntwurf[], gespeichert: readonly TrockenEtappe[]): boolean {
  const jetzt = etappenAus(entwurf)
  if (jetzt === null) return true // etwas Unlesbares steht da — das ist eine Änderung, und `fehler` meldet sie
  if (jetzt.length !== gespeichert.length) return true
  return jetzt.some((e, i) => {
    const g = gespeichert[i]
    return e.name !== g.name
      || e.dauerTage !== g.dauerTage
      || Math.abs(e.lufttemperaturC - g.lufttemperaturC) > 1e-9
      || Math.abs(e.luftfeuchteZiel - g.luftfeuchteZiel) > 1e-9
      || Math.abs(e.luftfeuchteMax - g.luftfeuchteMax) > 1e-9
  })
}

/** Zusammen so viele Tage — unlesbare Felder zählen als null. */
export function summeTage(entwurf: readonly EtappeEntwurf[]): number {
  return entwurf.reduce((summe, e) => summe + (zahl(e.dauer) ?? 0), 0)
}

/**
 * Was die Oberfläche vor dem Backend schon sagen kann: Pflichtfelder, ganze Tage, die Summe.
 * Alles andere (Bereiche, Obergrenze unter Ziel) meldet das Backend beim Speichern.
 */
export function fehler(entwurf: readonly EtappeEntwurf[], maxTage: number, maxEtappen: number): string[] {
  const saetze: string[] = []
  if (entwurf.length === 0) saetze.push('Mindestens eine Etappe muss bleiben.')
  if (entwurf.length > maxEtappen) saetze.push(`Höchstens ${maxEtappen} Etappen.`)
  entwurf.forEach((e, i) => {
    const nr = `Etappe ${i + 1}`
    if (e.name.trim().length === 0) saetze.push(`${nr}: Der Name fehlt.`)
    if (e.name.trim().length > NAME_MAX) saetze.push(`${nr}: Der Name darf höchstens ${NAME_MAX} Zeichen lang sein.`)
    const felder: Array<[string, string]> = [['Dauer', e.dauer], ['Temperatur', e.temp], ['Luftfeuchte', e.ziel], ['Obergrenze', e.max]]
    for (const [bezeichnung, text] of felder) {
      if (!istLeer(text) && zahlOderNull(text) === null) saetze.push(`${nr}: „${text}“ ist bei ${bezeichnung} keine Zahl.`)
      else if (istLeer(text)) saetze.push(`${nr}: ${bezeichnung} fehlt.`)
    }
    const dauer = zahl(e.dauer)
    if (dauer !== null && !Number.isInteger(dauer)) saetze.push(`${nr}: Die Dauer zählt in ganzen Tagen.`)
  })
  const summe = summeTage(entwurf)
  if (summe > maxTage) saetze.push(`Zusammen ${summe} Tage — das Trocknungsfenster endet ${maxTage} Tage nach der Ernte.`)
  return saetze
}

/** Eine neue Etappe hinter der gewählten: gleiche Werte wie die gewählte, damit sie sofort gültig ist. */
export function etappeEinfuegen(entwurf: readonly EtappeEntwurf[], nach: number): EtappeEntwurf[] {
  const vorlage = entwurf[nach] ?? entwurf[entwurf.length - 1]
  const neu: EtappeEntwurf = vorlage
    ? { ...vorlage, name: `Etappe ${entwurf.length + 1}` }
    : { name: 'Etappe 1', dauer: '3', temp: '19', ziel: '58', max: '65' }
  const kopie = [...entwurf]
  kopie.splice(nach + 1, 0, neu)
  return kopie
}

/** Die gewählte Etappe entfernen; die letzte bleibt stehen. */
export function etappeEntfernen(entwurf: readonly EtappeEntwurf[], index: number): EtappeEntwurf[] {
  if (entwurf.length <= 1) return [...entwurf]
  return entwurf.filter((_, i) => i !== index)
}

/** Ein Feld einer Etappe ändern. */
export function feldSetzen(entwurf: readonly EtappeEntwurf[], index: number, feld: keyof EtappeEntwurf, text: string): EtappeEntwurf[] {
  return entwurf.map((e, i) => (i === index ? { ...e, [feld]: text } : e))
}

/** Die Zeile über dem Blatt, wenn gerade ein Zelt nach diesem Programm trocknet. */
export function laeuftText(l: TrocknungLaeuft): string {
  const satz = `Trocknet gerade: ${l.zelt} · Tag ${l.tag} · Etappe ${l.etappeNr} „${l.etappeName}“`
  return l.gehalten ? `${satz} (der Plan ist abgelaufen, die letzte Etappe gilt weiter)` : satz
}

/** Der Satz nach dem Speichern. */
export function ergebnisText(antwort: TrocknungGespeichert): string {
  return antwort.neuAngelegt
    ? `Trocknung gespeichert — als neues eigenes Programm „${antwort.trocknung.programmName}“. Die Vorlage bleibt unverändert.`
    : `Trocknung gespeichert in „${antwort.trocknung.programmName}“.`
}

/** Der Rumpf der Speichern-Anfrage; der Name zählt nur bei einer neuen Kopie. */
export function speichernAnfrage(etappen: TrockenEtappe[], eigen: boolean, name: string): { etappen: TrockenEtappe[]; name?: string } {
  const sauber = name.trim()
  return eigen || sauber.length === 0 ? { etappen } : { etappen, name: sauber }
}

/**
 * Läuft gerade ein Zelt nach einer mitgelieferten Vorlage, ändert eine Kopie dort nichts: Der Grow zeigt
 * weiter auf die Vorlage. Das steht vor dem Speichern da, nicht erst hinterher.
 */
export function vorlageHinweis(daten: Pick<Trocknung, 'eigen' | 'laeuft'>): string | null {
  if (daten.eigen || daten.laeuft.length === 0) return null
  const zelte = daten.laeuft.map((l) => `„${l.zelt}“`).join(' und ')
  return `Das Zelt ${zelte} trocknet gerade nach dieser Vorlage. Eine Kopie ändert dort nichts, solange der Grow nach der Vorlage läuft — ändere die Trocknung in dem eigenen Programm, nach dem der Grow läuft.`
}
