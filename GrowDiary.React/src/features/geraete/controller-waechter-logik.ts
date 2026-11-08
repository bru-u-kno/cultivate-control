import type { ControllerWaechterEintrag } from '../steuerung/steuerung-typen'

/**
 * Ist eine Steckdose gewählt? Das Backend lässt leere Felder weg, „keine“ kommt also als FEHLENDES Feld an
 * (`undefined`), nicht als `null` — ein Vergleich mit `!== null` hielt am 08.11.2026 (cc.339) jeden Controller für „mit
 * Steckdose“ und versprach auf der Seite einen Neustart, der nie stattfand.
 */
export function hatSteckdose(c: Pick<ControllerWaechterEintrag, 'steckdoseEntity'>): boolean {
  return Boolean(c.steckdoseEntity)
}

/** Der Satz „Was passiert bei einem Ausfall“ unter den Einstellungen — richtet sich nach Modus UND Steckdose. */
export function ablaufText(c: Pick<ControllerWaechterEintrag, 'steckdoseEntity' | 'modus' | 'meldenNachMin' | 'neustartNachMin' | 'ausDauerSek' | 'maxNeustartsProTag'>): string {
  const neustart = c.modus === 'neustart'
  if (hatSteckdose(c) && neustart) {
    return `Nach ${c.meldenNachMin} Min. Ausfall kommt der Push, nach ${c.neustartNachMin} Min. wird die Steckdose ${c.ausDauerSek} Sek. ausgeschaltet und wieder eingeschaltet. Ist der Controller 5 Min. danach noch offline, gibt es einen Push und eine Stunde Ruhe; höchstens ${c.maxNeustartsProTag}× pro Tag.`
  }
  if (neustart) {
    return 'Keine schaltbare Steckdose gewählt — der Wächter meldet nur. Der Neustart läuft erst, wenn du oben eine Steckdose wählst.'
  }
  return `Der Wächter meldet nach ${c.meldenNachMin} Min. Ausfall und ändert nichts.`
}
