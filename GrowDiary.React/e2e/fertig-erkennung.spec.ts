import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { abraeumen, eigenenGrowAnlegen, type EigenerGrow } from './eigener-grow'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Fertig-Erkennung der Trocknung (A-029, Etappe 4) am echten Weg: Karte an der Live-Seite, Wägung im Blatt eintragen,
 * nach dem Neuladen wiederfinden, falsche Eingabe, löschen — und der Weg zum Trockengewicht.
 *
 * Ein eigenes Trockenzelt mit eigenem Grow und einer Ernte, die ihm zugewiesen ist; am Ende wird alles abgeräumt. Der
 * Demobestand hat kein Zelt mit Zweck „Trocknung“, und andere Prüfungen zählen dessen Zelte (`ernte-trockenzelt`
 * erwartet, dass es keines gibt) — deshalb hält ein EINZIGER Fall das Schloss von Anlegen bis Abräumen.
 */
/** „JJJJ-MM-TT“ von vor n Tagen, Ortszeit. */
function tagVor(n: number): string {
  const d = new Date(Date.now() - n * 86_400_000)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

let zeltId: number | null = null
let eigener: EigenerGrow | null = null

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — die Fertig-Erkennung braucht die laufende App.')
  await nimmSchloss()
  const zelt = await request.post('/api/settings/tents', { data: { name: `Fertig-Test ${Date.now()}`, tentType: 'Drying', status: 'Active', displayOrder: 99 } })
  if (!zelt.ok()) return
  zeltId = (await zelt.json()).id
  eigener = await eigenenGrowAnlegen(request, 'Fertig-Erkennung')
  if (!eigener) return
  const ernte = await request.put(`/api/grows/${eigener.growId}/harvest`, {
    data: {
      // Acht Tage her: genug Platz für Wägungen an mehreren früheren Tagen.
      harvestedAtLocal: tagVor(8),
      wetWeightG: null, dryWeightG: null, dryDays: null, yieldNotes: null, rating: null, flavorNotes: null,
      effectNotes: null, nugStructure: null, plantWeightsJson: null, dryingTentId: zeltId,
    },
  })
  expect(ernte.ok(), 'Die Ernte ließ sich dem Trockenzelt nicht zuweisen.').toBe(true)
})

test.afterEach(async ({ request }) => {
  try {
    await abraeumen(request, eigener)
    if (zeltId != null) await request.delete(`/api/settings/tents/${zeltId}`).catch(() => undefined)
    // Das Zelt muss wirklich weg sein — ein liegengebliebenes Trockenzelt verfälscht jede spätere Prüfung.
    if (zeltId != null) {
      const alle = await (await request.get('/api/settings/tents')).json()
      expect(alle.some((t: { id: number }) => t.id === zeltId), 'Das Prüf-Trockenzelt ließ sich nicht wieder löschen.').toBe(false)
    }
  } finally {
    zeltId = null
    eigener = null
    gibSchloss()
  }
})

async function karte(page: Page) {
  await page.goto(`/live/${zeltId}`, { waitUntil: 'networkidle' })
  const k = page.locator('[data-audit="live-fertig"]')
  await expect(k, 'Im trocknenden Zelt fehlt die Karte „Fertig-Erkennung“.').toBeVisible()
  return k
}

test('Fertig-Erkennung: eintragen, wiederfinden, falsche Eingabe, löschen, Weg zum Trockengewicht', async ({ page, request }) => {
  darfUeberspringen(zeltId == null || eigener == null, 'Testzelt ließ sich nicht anlegen.')

  await test.step('Das Zelt trocknet; ein anderes Zelt zeigt keine Karte', async () => {
    const stand = await (await request.get(`/api/tents/${zeltId}/trocknung/fertig`)).json()
    expect(stand.trocknet).toBe(true)
    expect(stand.stufe).toBe('keine')
    await page.goto('/', { waitUntil: 'networkidle' })
    await expect(page.locator('[data-audit="live-fertig"]'), 'Die Karte darf nur erscheinen, wenn das gewählte Zelt trocknet.').toHaveCount(0)
  })

  await test.step('Trocknungs-Ziel: der Plan der Etappe, noch keine Ports, das Blatt ohne Home Assistant', async () => {
    // Die Ernte liegt acht Tage zurück → Trocknungstag 9 → Etappe „Ausklang“ (Standard: 18 °C, 55 %, Band oben 19 °C / 57 %).
    const ziel = await (await request.get(`/api/tents/${zeltId}/trocknung/ziel`)).json()
    expect(ziel.trocknet).toBe(true)
    expect(ziel.etappe).toBe('Ausklang')
    expect(ziel.tempOben, 'Band oben der Temperatur: Ziel + 1 °C.').toBe(19)
    expect(ziel.feuchteOben, 'Band oben der Luftfeuchte: Ziel + 2 Punkte, unter der Obergrenze 65 %.').toBe(57)
    expect(ziel.geraete.map((g: { urteil: string }) => g.urteil)).toEqual(['nichtZugeordnet', 'nichtZugeordnet'])

    await page.goto('/', { waitUntil: 'networkidle' })
    await expect(page.locator('[data-audit="live-trocknungs-ziel"]'), 'Die Karte darf nur erscheinen, wenn das gewählte Zelt trocknet.').toHaveCount(0)

    const k = page.locator('[data-audit="live-trocknungs-ziel"]')
    await page.goto(`/live/${zeltId}`, { waitUntil: 'networkidle' })
    await expect(k, 'Im trocknenden Zelt fehlt die Karte „Trocknungs-Ziel“.').toBeVisible()
    await expect(k).toContainText('Noch keine Geräte zugeordnet')
    await expect(k).toContainText('Plan: 18 °C · 55 % Luftfeuchte')

    // Die Live-Seite zeigt nur Werte (Teil B): die Zuordnung steht unter Steuerung › Trocknung, nicht an der Karte.
    await expect(k.locator('button:not(.ls-fold-titel)'), 'Die Live-Karte darf keine Bedienknöpfe tragen — eingestellt wird unter Steuerung.').toHaveCount(0)
    await k.getByRole('link', { name: /Unter Steuerung zuordnen/ }).click()
    await expect(page).toHaveURL(/\/steuerung\/trocknung/)
    await expect(page.getByRole('heading', { name: 'Trocknung', level: 1 })).toBeVisible()
    await expect(page.getByText('Nur anzeigen').first()).toBeVisible()

    // Die Demo hat kein Home Assistant: Das Blatt sagt es, statt eine leere Auswahl anzubieten, und Speichern ist gesperrt.
    await page.getByRole('button', { name: /Ports bearbeiten/ }).click()
    await expect(page.getByRole('alert')).toContainText('nicht erreichbar')
    await expect(page.getByRole('button', { name: 'Speichern', exact: true })).toBeDisabled()
    await page.getByRole('button', { name: 'Abbrechen' }).click()
    await expect(page.getByRole('alert')).toHaveCount(0)

    // Schnittstelle: ohne zugeordneten Port lässt sich „Gerät einbinden“ (entfeuchterFolgt) nicht einschalten, eine unbekannte Betriebsart nicht setzen.
    const ohnePort = await request.put(`/api/tents/${zeltId}/trocknung/steuerung`, { data: { betrieb: 'regeln', entfeuchterFolgt: true, kuehlungFolgt: false } })
    expect(ohnePort.status()).toBe(400)
    const falsch = await request.put(`/api/tents/${zeltId}/trocknung/steuerung`, { data: { betrieb: 'x', entfeuchterFolgt: false, kuehlungFolgt: false } })
    expect(falsch.status()).toBe(400)
    const lesen = await (await request.get(`/api/tents/${zeltId}/trocknung/steuerung`)).json()
    expect(lesen.betrieb, 'Nach den abgewiesenen Anfragen steht die Betriebsart unverändert auf „melden“.').toBe('melden')
    // Zurück zur Live-Seite für die folgenden Schritte (Zeltwahl steckt im Router-Zustand).
    await page.goto(`/live/${zeltId}`, { waitUntil: 'networkidle' })
  })

  await test.step('Erste Wägung im Blatt, Wägungen früherer Tage per Schnittstelle, nach dem Neuladen wiederfinden', async () => {
    const k = await karte(page)
    await expect(k).toContainText('Noch keine Wägung')
    // Die erste Wägung über die Oberfläche: das Blatt, das Gewicht mit Komma, das Datum „gewogen am“.
    await k.getByRole('button', { name: 'Erste Wägung eintragen' }).click()
    await page.getByLabel('Gewogen am').fill(tagVor(6))
    await page.getByLabel('Gewicht in Gramm').fill('75,6')
    await page.getByRole('button', { name: 'Speichern', exact: true }).click()
    await expect(k).toContainText('Erste Wägung gespeichert')

    // Wägungen an weiteren Tagen — erst damit gibt es einen Verlauf (eine Wägung am selben Tag ist noch keiner).
    for (const [tage, gramm] of [[4, 61.2]] as const) {
      const r = await request.post(`/api/tents/${zeltId}/trocknung/waegungen`, { data: { gewichtG: gramm, datum: tagVor(tage) } })
      expect(r.ok(), `Wägung vor ${tage} Tagen wurde abgewiesen.`).toBe(true)
    }
    const k2 = await karte(page) // neu aufrufen: Die Zeltwahl steckt im Router-Zustand von /live/:id und überlebt kein reload()

    await k2.getByRole('button', { name: 'Probe wiegen' }).click()
    await page.getByLabel('Gewicht in Gramm').fill('50,3')
    await page.getByRole('button', { name: 'Speichern', exact: true }).click()
    await expect(k2).toContainText('Gewicht sinkt noch')
    await expect(k2, '50,3 g von 75,6 g sind 67 % der ersten Wägung.').toContainText('67 %')
    await expect(k2.locator('svg.fe-kurve')).toBeVisible()

    // ERNEUT AUFRUFEN: nach dem Neuladen steht es noch da.
    const wieder = await karte(page)
    await expect(wieder).toContainText('67 %')
    await expect(wieder).toContainText('50,3 g')
  })

  await test.step('Ein unlesbares Gewicht wird gemeldet und nichts gespeichert', async () => {
    const vorher = (await (await request.get(`/api/tents/${zeltId}/trocknung/fertig`)).json()).punkte.length
    const k = await karte(page)
    await k.getByRole('button', { name: 'Probe wiegen' }).click()
    await page.getByLabel('Gewicht in Gramm').fill('abc')
    await page.getByRole('button', { name: 'Speichern', exact: true }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    const nachher = (await (await request.get(`/api/tents/${zeltId}/trocknung/fertig`)).json()).punkte.length
    expect(nachher, 'Ein unlesbares Gewicht darf keine Wägung anlegen.').toBe(vorher)
  })

  await test.step('Die letzte Wägung löschen, dann „hält sich“ und der Weg zum Trockengewicht', async () => {
    const k = await karte(page)
    page.once('dialog', (dialog) => void dialog.accept())
    await k.getByRole('button', { name: 'Letzte Wägung löschen' }).click()
    await expect(k).toContainText('61,2 g')

    // Zwei ruhige Wägetage hintereinander (je unter 1 %): das Gewicht hält sich.
    for (const [tage, gramm] of [[2, 61.0], [0, 60.8]] as const) {
      const r = await request.post(`/api/tents/${zeltId}/trocknung/waegungen`, { data: { gewichtG: gramm, ...(tage > 0 ? { datum: tagVor(tage) } : {}) } })
      expect(r.ok()).toBe(true)
    }
    const ruhig = await karte(page)
    await expect(ruhig).toContainText('Gewicht hält sich')

    await ruhig.getByRole('link', { name: 'Fühlt sich fertig an' }).click()
    await expect(page).toHaveURL(new RegExp(`/grows/${eigener!.growId}/harvest\\?fertig=1`))
    await expect(page.getByText('Trocknung fertig?')).toBeVisible()
    await expect(page.getByLabel('Frischgewicht (optional)')).toBeVisible()
  })
})
