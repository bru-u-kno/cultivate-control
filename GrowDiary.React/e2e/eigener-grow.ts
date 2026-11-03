import type { APIRequestContext } from '@playwright/test'

/**
 * Ein Grow nur für diesen Lauf — samt eigenem Hydro-System.
 *
 * **Wozu.** Sechs Formulare standen in `OHNE_RUNDWEG` von
 * `formular-rundweg.spec.ts`, und der Grund war fast immer derselbe: „der
 * Rundweg würde den Demobestand verändern, gegen den alle anderen Prüfungen
 * laufen." Der Grund war richtig — die Folgerung, deshalb gar nicht zu prüfen,
 * war es nicht. In genau dieser Lücke sassen zwei Fehler der Ernteseite
 * („21,5" wurde zu 215; die Summe stand englisch).
 *
 * Wer schreibend prüfen will, legt sich seinen eigenen Datensatz an und räumt
 * ihn wieder ab. Das ist der Unterschied zwischen „geht nicht" und „ist noch
 * nicht gebaut".
 *
 * **Ein eigenes Hydro-System dazu**, nicht nur ein eigener Grow: der
 * Löschschutz eines Systems hängt an seinen laufenden Grows, und das System des
 * Demobestands soll unberührt bleiben.
 */
export type EigenerGrow = {
  growId: number
  systemId: number
  tentId: number
}

/**
 * Legt Hydro-System und Grow an — oder gibt `null` zurück, wenn die App nicht
 * antwortet.
 *
 * Bewusst `null` statt einer Ausnahme: der Aufrufer entscheidet mit
 * `darfUeberspringen`, ob das ein Grund zum Überspringen ist. Im strengen Lauf
 * ist es einer zum Durchfallen.
 */
export async function eigenenGrowAnlegen(
  api: APIRequestContext,
  name: string,
  zusatz: Record<string, unknown> = {},
  zelt?: number,
): Promise<EigenerGrow | null> {
  // Die Zeltliste haengt an den Einstellungen — GET /api/tents gehoert dem
  // Live-Bildschirm und liefert etwas anderes.
  const zelte = await api.get('/api/settings/tents')
  if (!zelte.ok()) return null
  const liste = await zelte.json()
  const erstes = Array.isArray(liste) ? liste[0] : liste?.tents?.[0]
  if (erstes?.id == null) return null
  const tentId: number = zelt ?? erstes.id

  const marke = `${name} ${Date.now()}`

  const system = await api.post('/api/hydro-setups', {
    data: {
      name: `${marke} · RDWC`,
      tentId,
      hydroStyle: 'RDWC',
      potCount: 2,
      potSizeLiters: 20,
      reservoirLiters: 60,
      layoutType: 'Row',
      reservoirPosition: 'Left',
    },
  })
  if (!system.ok()) return null
  const systemId: number = (await system.json()).id

  const grow = await api.post('/api/grows', {
    data: {
      name: marke,
      hydroStyle: 'RDWC',
      status: 'Running',
      /* Weit in der Vergangenheit, mit Absicht: die Grow-Liste sortiert nach
         StartDate DESC, und ein Wegwerf-Grow darf sich nie vor den
         Demobestand schieben. Sonst waehlt jede Seite ohne ?growId= ihn aus
         — und eine fremde Pruefung schreibt in den falschen Grow. */
      startDate: '2020-01-01',
      plantCount: 2,
      tentId,
      systemId,
      ...zusatz,
    },
  })
  if (!grow.ok()) return null

  return { growId: (await grow.json()).id, systemId, tentId }
}

/**
 * Räumt wieder ab — auch wenn der Fall durchgefallen ist.
 *
 * Ein liegengebliebener Grow verschiebt jede spätere Prüfung: er zählt in
 * Listen mit, belegt ein Hydro-System und taucht in Fälligkeiten auf. Deshalb
 * schluckt diese Funktion ihre eigenen Fehler — beim Abräumen darf nichts den
 * Lauf rot machen, und was liegen bleibt, fällt beim nächsten frischen Bestand
 * ohnehin weg.
 */
export async function abraeumen(api: APIRequestContext, was: EigenerGrow | null): Promise<void> {
  if (was == null) return
  try {
    await api.delete(`/api/grows/${was.growId}`)
    await api.delete(`/api/hydro-setups/${was.systemId}`)
  } catch {
    // bewusst still, siehe oben
  }
}
