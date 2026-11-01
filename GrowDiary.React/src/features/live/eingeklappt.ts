import { useCallback, useSyncExternalStore } from 'react'
import type { MetricPayload } from '../../types'
import { urteilFuerMetrik } from './metric-tile-model'

/**
 * Fork AI: Was auf der Live-Seite eingeklappt ist.
 *
 * <b>Warum am Gerät und nicht am Zelt.</b> Wie bei den Kopfknöpfen ist das eine
 * Eigenschaft des Bildschirms: am Telefon klappt man die Kamera ein, am
 * Schreibtisch nicht. Deshalb im Browser abgelegt — kein Datenmodell, keine
 * Wanderung beim Update.
 *
 * <b>Die Kennungen.</b> `bereich:<id>` für einen Messwert-Bereich, `kachel:<id>`
 * für eine Kachel, `panel:<name>` für die Karten darunter. Eine Kennung, die
 * es nicht mehr gibt (Bereich gelöscht), schadet nicht: sie trifft einfach
 * nichts mehr.
 */

const KEY = 'growos.live.eingeklappt'

export function leseEingeklappt(roh: string | null): Set<string> {
  if (!roh) return new Set()
  try {
    const gelesen: unknown = JSON.parse(roh)
    if (!Array.isArray(gelesen)) return new Set()
    return new Set(gelesen.filter((eintrag): eintrag is string => typeof eintrag === 'string'))
  } catch {
    return new Set()
  }
}

function ladeEingeklappt(): Set<string> {
  try { return leseEingeklappt(localStorage.getItem(KEY)) } catch { return new Set() }
}

function speichereEingeklappt(menge: Set<string>): void {
  try { localStorage.setItem(KEY, JSON.stringify([...menge])) } catch { /* Privatmodus */ }
}

export function umschaltenEingeklappt(menge: Set<string>, id: string): Set<string> {
  const naechste = new Set(menge)
  if (naechste.has(id)) naechste.delete(id)
  else naechste.add(id)
  return naechste
}

export type Einklappen = {
  istZu: (id: string) => boolean
  umschalten: (id: string) => void
}

/**
 * Ein Stand für die ganze Seite. Bei mehreren aufgeklappten Zelten sitzt je
 * Zelt ein Bildschirm auf der Seite — hätte jeder seine eigene Kopie aus dem
 * Browser geladen, hätte ein Zuklappen im einen das Zuklappen im anderen beim
 * Speichern überschrieben, und die Zelte hätten sich nach dem Neuladen
 * widersprochen. So lesen und schreiben alle denselben Satz.
 */
let geteilt: Set<string> | null = null
const hoerer = new Set<() => void>()
const holen = (): Set<string> => (geteilt ??= ladeEingeklappt())
const abonnieren = (rueckruf: () => void) => {
  hoerer.add(rueckruf)
  return () => { hoerer.delete(rueckruf) }
}

/**
 * Im Anpassen-Modus ist alles offen: wer Kacheln umsortiert, muss jeden
 * Bereich als Ziel sehen. Gespeichert bleibt der Zustand trotzdem — nach
 * „Fertig" ist eingeklappt, was vorher eingeklappt war.
 */
export function useEingeklappt(alleOffen: boolean): Einklappen {
  const menge = useSyncExternalStore(abonnieren, holen, holen)
  const umschalten = useCallback((id: string) => {
    // Im Anpassen-Modus wäre ein Umschalten unsichtbar — es wirkte erst nach
    // „Fertig". Die Oberfläche bietet dort keinen Knopf an; das hier hält es,
    // falls doch einer durchrutscht.
    if (alleOffen) return
    const naechste = umschaltenEingeklappt(holen(), id)
    geteilt = naechste
    speichereEingeklappt(naechste)
    hoerer.forEach((rueckruf) => rueckruf())
  }, [alleOffen])
  return { istZu: (id) => !alleOffen && menge.has(id), umschalten }
}

export type Bilanz = { werte: number; warnungen: number; kritisch: number }

/**
 * Was ein eingeklappter Bereich verschweigt — in Zahlen.
 *
 * Dieselbe Lesart wie die Kachel und der Score der Seite (`kachelUrteil` mit
 * Meldegrenzen und den Stellen des Messwerts). Würde der Bereich hier anders
 * zählen als seine Kacheln, stünde zugeklappt „alles gut" über einer roten
 * Kachel.
 */
export function bereichsBilanz(metriken: MetricPayload[]): Bilanz {
  let warnungen = 0
  let kritisch = 0
  for (const metric of metriken) {
    const urteil = urteilFuerMetrik(metric)
    if (urteil === 'warn') warnungen++
    else if (urteil === 'crit') kritisch++
  }
  return { werte: metriken.length, warnungen, kritisch }
}

/**
 * Dieselben Wörter wie der Rest der Seite: „daneben" wie in der Kopfzeile
 * („5 Werte daneben") und auf der gelben Kachel, „Grenze" wie auf der roten.
 * „Warnung" wäre falsch — so heisst auf dieser Seite die Schwere eines
 * Risikos, und eine gelbe Kachel löst gerade KEINE Meldung aus.
 */
export function bilanzText({ werte, warnungen, kritisch }: Bilanz): string {
  const daneben = warnungen + kritisch
  const teile = [`${werte} ${werte === 1 ? 'Wert' : 'Werte'}`]
  if (daneben > 0) teile.push(`${daneben} daneben${kritisch > 0 ? `, davon ${kritisch} an der Grenze` : ''}`)
  return teile.join(' · ')
}
