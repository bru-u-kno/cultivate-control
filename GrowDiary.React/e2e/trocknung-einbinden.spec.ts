import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Steuerung › Trocknung: „Gerät einbinden“. Der Haken erscheint sofort beim Antippen; solange der Fork speichert und abgleicht,
 * zeigt die Karte die Phase, danach „Fertig“. Scheitert das Speichern, springt der Haken zurück und die Karte sagt warum.
 * Ein Vorgang einer anderen Seite sperrt die Karten und wird angezeigt.
 *
 * Der Demobestand hat kein Trockenzelt mit Ports; Übersicht und Stand sind nachgestellt, gespeichert wird nichts.
 */
type Phase = 'speichern' | 'abgleichen' | 'pruefen'
interface Zustand {
  betrieb: 'melden' | 'regeln'
  abluft: boolean
  entfeuchterAm: number
  inArbeit: { phase: Phase; seit: string; clientId: string | null } | null
  /** Entfeuchter, den der Fork schaltet (statt Grenzwert): Urteil und Schaltstand. */
  schalten?: { urteil: string; modus: string; zustand: string; feuchte: number | null; zuletztUtc: string | null; zuletztAktion: string | null }
}

const entfeuchterZeile = (z: Zustand) => z.schalten
  ? {
    rolle: 'entfeuchter', portName: 'DRY Dehumi', folgt: true, modus: z.schalten.modus, wertAmGeraet: null, einheit: null, planWert: 60,
    urteil: z.schalten.urteil, vorher: null, zuletztGesetzt: null,
    schalten: { zustand: z.schalten.zustand, feuchte: z.schalten.feuchte, einAb: 58, ausUnter: 56, modus: z.schalten.modus, zuletztUtc: z.schalten.zuletztUtc, zuletztAktion: z.schalten.zuletztAktion, gescheitert: z.schalten.urteil === 'schaltetNicht' },
  }
  : { rolle: 'entfeuchter', portName: 'DRY Dehumi', folgt: true, modus: 'Auto', wertAmGeraet: z.entfeuchterAm, einheit: '%', planWert: 60, urteil: z.entfeuchterAm === 60 ? 'passt' : 'weichtAb', vorher: null, zuletztGesetzt: null }

const stand = (z: Zustand) => ({
  trocknet: true, betrieb: z.betrieb, portsErreichbar: true, tag: 7, tageGeplant: 10, etappe: 'Ausklang',
  tempMin: 16, tempMax: 18, feuchteMin: 56, feuchteMax: 60,
  geraete: [
    entfeuchterZeile(z),
    { rolle: 'kuehlung', portName: 'DRY Clima', folgt: false, modus: 'Off', wertAmGeraet: null, einheit: null, planWert: 18, urteil: 'geraetAus', vorher: null, zuletztGesetzt: null },
    { rolle: 'abluft-feuchte', portName: 'DRY Out Venti', folgt: z.abluft, modus: 'On', wertAmGeraet: null, einheit: null, planWert: 60, urteil: 'ohneAutomatik', vorher: null, zuletztGesetzt: null },
    { rolle: 'abluft-temp', portName: 'DRY Out Venti', folgt: z.abluft, modus: 'On', wertAmGeraet: null, einheit: null, planWert: 18, urteil: 'ohneAutomatik', vorher: null, zuletztGesetzt: null },
  ],
  inArbeit: z.inArbeit,
})

async function nachstellen(page: Page, z: Zustand, opts: { putAntwort?: () => Promise<{ status: number; body: unknown }> } = {}) {
  await page.route(/\/api\/steuerung$/, (route) => route.fulfill({
    json: {
      haErreichbar: true, standUtc: new Date().toISOString(), nichtEingerichtet: [], zelte: [{ id: 2, name: 'Trockenzelt' }],
      module: [{ kennung: 'trocknung', titel: 'Trocknung', status: 'an', kurz: 'Beispiel', wert: '1', unterzeile: 'Beispiel', hatDetail: true, zeltId: 2 }],
    },
  }))
  const gesendet: Array<Record<string, unknown>> = []
  await page.route(/\/api\/tents\/2\/trocknung\/steuerung$/, async (route) => {
    const req = route.request()
    if (req.method() === 'PUT') {
      const body = req.postDataJSON() as Record<string, unknown>
      gesendet.push(body)
      const antwort = opts.putAntwort ? await opts.putAntwort() : { status: 200, body: null }
      if (antwort.status !== 200) return route.fulfill({ status: antwort.status, json: antwort.body ?? { error: 'controller_nicht_erreichbar', message: 'Der Controller hat nicht geantwortet.' } })
      z.betrieb = body.betrieb as Zustand['betrieb']
      z.abluft = body.abluftFolgt === true
      z.inArbeit = null
      return route.fulfill({ json: stand(z) })
    }
    return route.fulfill({ json: stand(z) })
  })
  return gesendet
}

const abluft = (page: Page) => page.locator('[data-audit="trocknung-abluft"]').locator('xpath=ancestor::article[1]')

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test('Haken ist beim Antippen sofort da, die Karte zeigt den Vorgang und danach „Fertig“', async ({ page }) => {
  const z: Zustand = { betrieb: 'melden', abluft: false, entfeuchterAm: 60, inArbeit: null }
  let freigeben: () => void = () => undefined
  const gesendet = await nachstellen(page, z, { putAntwort: () => new Promise((fertig) => { freigeben = () => fertig({ status: 200, body: null }) }) })
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = abluft(page)
  const haken = karte.getByRole('checkbox', { name: /Gerät einbinden/ })
  await expect(karte).toContainText('nicht eingebunden')
  await expect(haken).not.toBeChecked()

  await haken.click()
  // Die Antwort des Servers steht noch aus — der Haken muss trotzdem schon gesetzt sein.
  await expect(haken).toBeChecked()
  await expect(karte.locator('[data-audit="trocknung-vorgang"]')).toContainText('wird gespeichert')
  await expect(karte).toContainText('Der Haken ist schon gesetzt')
  await expect(page.locator('[data-audit="trocknung-erklaerung-abluft"]')).toHaveCount(0)

  // Ein zweites Antippen währenddessen ändert nichts (Karte gesperrt).
  await haken.click({ force: true })
  expect(gesendet).toHaveLength(1)
  expect(gesendet[0]).toMatchObject({ abluftFolgt: true, clientId: expect.stringMatching(/^s-/) })

  // Die Seite fragt dichter nach, solange etwas läuft: die Phase kommt vom Server.
  z.inArbeit = { phase: 'abgleichen', seit: new Date().toISOString(), clientId: (gesendet[0].clientId as string) }
  await expect(karte.locator('.zj-schritte li.is-jetzt')).toContainText('Mit dem Controller abgleichen', { timeout: 8000 })

  freigeben()
  await expect(karte).toContainText('Fertig. Abluft ist eingebunden — Port nicht auf Auto.')
  await expect(page.locator('[data-audit="kurzmeldung"]')).toContainText('Abluft ist eingebunden')
  await expect(haken).toBeChecked()
  await expect(page.locator('[data-audit="trocknung-erklaerung-abluft"]')).toContainText('Der Port steht nicht auf Auto (aktuell: On)')
})

test('Scheitert der Vorgang, zeigt der Haken wieder den gespeicherten Stand und die Karte nennt den Grund', async ({ page }) => {
  const z: Zustand = { betrieb: 'melden', abluft: false, entfeuchterAm: 60, inArbeit: null }
  await nachstellen(page, z, { putAntwort: async () => ({ status: 502, body: { error: 'controller', message: 'Der Controller hat nicht geantwortet.' } }) })
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = abluft(page)
  const haken = karte.getByRole('checkbox', { name: /Gerät einbinden/ })
  await haken.click()
  await expect(karte).toContainText('Nicht abgeschlossen. Der Haken zeigt wieder den gespeicherten Stand.')
  await expect(haken).not.toBeChecked()
  await expect(page.locator('[data-audit="kurzmeldung"]')).toContainText('Nicht abgeschlossen')

  // Dieselbe Handlung ein zweites Mal: wieder sofort gesetzt, wieder zurück (kein „schon offen“-Zustand).
  await haken.click()
  await expect(karte).toContainText('Nicht abgeschlossen')
  await expect(haken).not.toBeChecked()
})

test('Ein Vorgang einer anderen Seite wird angezeigt und sperrt die Karten', async ({ page }) => {
  const z: Zustand = { betrieb: 'melden', abluft: false, entfeuchterAm: 60, inArbeit: { phase: 'abgleichen', seit: new Date().toISOString(), clientId: 's-fremd' } }
  await nachstellen(page, z)
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  await expect(page.getByText('Jemand anderes ändert gerade etwas')).toBeVisible()
  await expect(page.getByText(/Die Steuerung gleicht ab\. Die Karten sind gesperrt/)).toBeVisible()
  await expect(abluft(page).locator('.v1-switch')).toHaveClass(/is-gesperrt/)

  // Ist der Vorgang vorbei, verschwindet der Hinweis von selbst (der Takt fragt dichter nach, solange etwas läuft).
  z.inArbeit = null
  await expect(page.getByText('Jemand anderes ändert gerade etwas')).toHaveCount(0, { timeout: 8000 })
})

test('In „Regeln“ erscheint der Haken sofort, gilt aber erst nach der Bestätigung; Abbrechen stellt ihn zurück', async ({ page }) => {
  const z: Zustand = { betrieb: 'regeln', abluft: false, entfeuchterAm: 50, inArbeit: null }
  const gesendet = await nachstellen(page, z)
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = abluft(page)
  const haken = karte.getByRole('checkbox', { name: /Gerät einbinden/ })
  await haken.click()
  await expect(haken).toBeChecked()
  await expect(karte).toContainText('wartet auf Bestätigung')
  await expect(page.getByRole('dialog')).toContainText('Gerät einbinden?')
  await expect(page.locator('[data-audit="rueckfrage-entfeuchter"]')).toContainText('50 → 60 %')
  expect(gesendet).toHaveLength(0)

  await page.getByRole('button', { name: 'Abbrechen' }).click()
  await expect(haken).not.toBeChecked()
  await expect(page.locator('[data-audit="kurzmeldung"]')).toContainText('Abgebrochen')
  expect(gesendet).toHaveLength(0)

  await haken.click()
  await page.getByRole('button', { name: 'Ja, einbinden' }).click()
  await expect(karte).toContainText('Fertig. Abluft ist eingebunden')
  expect(gesendet).toHaveLength(1)
})

test('Die Betriebsart zeigt beim Speichern oben die Phase und sperrt die Karten', async ({ page }) => {
  const z: Zustand = { betrieb: 'melden', abluft: false, entfeuchterAm: 60, inArbeit: null }
  let freigeben: () => void = () => undefined
  const gesendet = await nachstellen(page, z, { putAntwort: () => new Promise((fertig) => { freigeben = () => fertig({ status: 200, body: null }) }) })
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  await page.getByRole('button', { name: 'Regeln', exact: true }).click()
  // Entfeuchter steht auf dem Plan (60 %), also keine Rückfrage: das Speichern beginnt sofort.
  const band = page.locator('[data-audit="trocknung-vorgang-betrieb"]')
  await expect(band).toContainText('Betriebsart wird gespeichert')
  await expect(abluft(page).locator('.v1-switch')).toHaveClass(/is-gesperrt/)
  expect(gesendet).toHaveLength(1)

  z.inArbeit = { phase: 'pruefen', seit: new Date().toISOString(), clientId: (gesendet[0].clientId as string) }
  await expect(band.locator('li.is-jetzt')).toContainText('Wert am Gerät prüfen', { timeout: 8000 })

  freigeben()
  await expect(band).toHaveCount(0)
  await expect(page.getByText(/Betriebsart: Regeln\. Die Steuerung gleicht die eingebundenen Geräte jetzt mit dem Plan ab\./).first()).toBeVisible()
})

test('Die Rückfrage bestätigt mit dem frischen Stand: ein zwischenzeitlich geänderter anderer Haken bleibt erhalten', async ({ page }) => {
  const z: Zustand = { betrieb: 'regeln', abluft: false, entfeuchterAm: 50, inArbeit: null }
  const gesendet = await nachstellen(page, z)
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = abluft(page)
  await karte.getByRole('checkbox', { name: /Gerät einbinden/ }).click()
  await expect(page.getByRole('dialog')).toContainText('Gerät einbinden?')

  // Während die Rückfrage offen ist, bindet jemand anderes die Kühlung ein; die Seite erfährt es beim nächsten Takt.
  z.betrieb = 'regeln'
  const stand0 = stand(z)
  stand0.geraete[1].folgt = true
  await page.unroute(/\/api\/tents\/2\/trocknung\/steuerung$/)
  await page.route(/\/api\/tents\/2\/trocknung\/steuerung$/, async (route) => {
    if (route.request().method() === 'PUT') { gesendet.push(route.request().postDataJSON()); return route.fulfill({ json: stand0 }) }
    return route.fulfill({ json: stand0 })
  })
  const kuehlung = page.locator('[data-audit="trocknung-kuehlung"]').locator('xpath=ancestor::article[1]').getByRole('checkbox', { name: /Gerät einbinden/ })
  await expect(kuehlung).toBeChecked({ timeout: 25000 })   // der ruhige Takt der Seite beträgt 15 Sekunden
  await page.getByRole('button', { name: 'Ja, einbinden' }).click()
  await expect.poll(() => gesendet.length).toBe(1)
  expect(gesendet[0]).toMatchObject({ abluftFolgt: true, kuehlungFolgt: true })
})

test('Der Entfeuchter, den der Fork schaltet, zeigt Schwellen und Luftfeuchte statt eines Grenzwerts', async ({ page }) => {
  const z: Zustand = {
    betrieb: 'regeln', abluft: false, entfeuchterAm: 60, inArbeit: null,
    schalten: { urteil: 'schaltet', modus: 'Off', zustand: 'steht', feuchte: 56.6, zuletztUtc: new Date().toISOString(), zuletztAktion: 'aus' },
  }
  await nachstellen(page, z)
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = page.locator('[data-audit="trocknung-entfeuchter"]').locator('xpath=ancestor::article[1]')
  await expect(karte).toContainText('schaltet nach Plan')
  const zeilen = karte.locator('[data-audit="trocknung-schalten"]')
  await expect(zeilen).toContainText('Schaltet ein ab')
  await expect(zeilen).toContainText('58 %')
  await expect(zeilen).toContainText('Schaltet aus unter')
  await expect(zeilen).toContainText('56 %')
  await expect(zeilen).toContainText('Luftfeuchte im Zelt 56,6 %')
  await expect(zeilen).toContainText(/aus seit \d\d:\d\d/)
  // Kein Grenzwert am Gerät, kein „Zuletzt gesetzt“: der Entfeuchter wird geschaltet, nicht über eine Grenze geführt.
  await expect(karte).not.toContainText('Luftfeuchte: am Gerät')
  await expect(karte).not.toContainText('Zuletzt gesetzt')
  await expect(page.locator('[data-audit="trocknung-erklaerung-entfeuchter"]')).toContainText('ein ab 58 % und aus unter 56 %')
})

test('Steht der Port des Entfeuchters auf Auto, nennt die Karte den Handgriff und warnt', async ({ page }) => {
  const z: Zustand = {
    betrieb: 'regeln', abluft: false, entfeuchterAm: 60, inArbeit: null,
    schalten: { urteil: 'portNichtSchaltbar', modus: 'Auto', zustand: 'portNichtSchaltbar', feuchte: 62, zuletztUtc: null, zuletztAktion: null },
  }
  await nachstellen(page, z)
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })

  const karte = page.locator('[data-audit="trocknung-entfeuchter"]').locator('xpath=ancestor::article[1]')
  await expect(karte).toContainText('Port steht auf Auto')
  await expect(page.locator('[data-audit="trocknung-erklaerung-entfeuchter"]')).toContainText('Stell den Port in der App des Controllers auf On')
})
