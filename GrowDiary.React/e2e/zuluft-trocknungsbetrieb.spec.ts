import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Steuerung › Zuluft: Karte „Trocknungsbetrieb“ (cc.337) — die Zuluft folgt dem Trocknungsplan.
 *
 * Gerendert wird die GEBAUTE App gegen das echte Backend mit Demobestand; der Demobestand hat kein Trockenzelt mit Zuluft, deshalb wird die Antwort von
 * `/api/trocknung/zuluft` nachgestellt. Was das Backend entscheidet, prüfen `TrocknungZuluftTests` und `TrocknungSteuerungTests`; hier geht es um das,
 * was der Nutzer sieht und tippt: Ohne Trockenzelt gibt es die Karte nicht, mit Trockenzelt steht sie über der Regel, der Haken schickt ein PUT,
 * und die Stufen-Knöpfe auch (zweimal hintereinander, damit der Fall „schon gesetzt“ mitgeprüft ist).
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'kein Backend unter GROW_OS_URL — diese Fälle bedienen die laufende App')
})

const zuluft = (teil: Record<string, unknown> = {}) => ({
  trockenzelt: true, tentId: 2, zeltName: 'Zelt-Trocknung', trocknet: true, folgt: false, regelt: false, hoechststufe: 4, abstandProzent: 3,
  zeltTempC: 17.1, zeltRhProzent: 64, zeltAbsolutGm3: 9.3, aussenAbsolutGm3: 6.7, planRhProzent: 60, planTempC: 18, tag: 8, tageGeplant: 10,
  zustand: 'saugt', stufe: 2, differenz: 2.6, laeuftStufe: 2, gescheitert: false, ...teil,
})

test('Ohne Trockenzelt gibt es die Karte nicht', async ({ page }) => {
  await page.route(/\/api\/trocknung\/zuluft$/, (route) => route.fulfill({ json: { ...zuluft(), trockenzelt: false, tentId: null, zeltName: null, trocknet: false } }))
  await page.goto('/steuerung/zuluft', { waitUntil: 'networkidle' })
  await expect(page.getByRole('heading', { name: 'Zuluft', level: 1 })).toBeVisible()
  await expect(page.locator('[data-audit="zuluft-trocknung"]')).toHaveCount(0)
})

test('Mit Trockenzelt: Karte über der Regel, Haken und Stufen schicken ein PUT, der zweite Tipp auch', async ({ page }) => {
  const stand = zuluft()
  const gesendet: Array<Record<string, unknown>> = []
  await page.route(/\/api\/trocknung\/zuluft$/, async (route) => {
    const req = route.request()
    if (req.method() !== 'PUT') return route.fulfill({ json: stand })
    const body = req.postDataJSON() as Record<string, unknown>
    gesendet.push(body)
    Object.assign(stand, body)
    return route.fulfill({ json: stand })
  })
  await page.goto('/steuerung/zuluft', { waitUntil: 'networkidle' })

  const karte = page.locator('[data-audit="zuluft-trocknung"]').locator('xpath=ancestor::article')
  await expect(karte).toBeVisible()
  await expect(karte.getByText('Zuluft von Zelt-Trocknung')).toBeVisible()
  await expect(karte.getByText('nicht eingebunden')).toBeVisible()
  // Ausgeschaltet: keine Messwerte, kein Stepper.
  await expect(karte.getByText('Höchststufe')).toHaveCount(0)
  // Die Karte steht über dem Abschnitt „Regel“.
  const kartenOben = (await karte.boundingBox())!.y
  const regelOben = (await page.getByRole('heading', { name: 'Regel', level: 2 }).boundingBox())!.y
  expect(kartenOben).toBeLessThan(regelOben)

  await karte.getByRole('checkbox', { name: /Folgt dem Trocknungsplan/ }).check()
  await expect(karte.getByText('saugt', { exact: true })).toBeVisible()
  expect(gesendet).toEqual([{ folgt: true }])
  await expect(karte.getByText(/feuchter als der Plan \(60 %\)/)).toBeVisible()
  await expect(karte.getByText('Zeltluft jetzt')).toBeVisible()

  const hoechst = karte.locator('[data-audit="zuluft-trocknung-hoechststufe"]')
  await expect(hoechst).toHaveText('4')
  await karte.getByRole('button', { name: 'Eine Stufe mehr' }).click()
  await expect(hoechst).toHaveText('5')
  await karte.getByRole('button', { name: 'Eine Stufe mehr' }).click()
  await expect(hoechst).toHaveText('6')
  expect(gesendet.slice(1)).toEqual([{ hoechststufe: 5 }, { hoechststufe: 6 }])

  await karte.getByRole('button', { name: 'Einen Prozentpunkt weniger' }).click()
  await expect(karte.locator('[data-audit="zuluft-trocknung-abstand"]')).toHaveText('2')

  // Ausschalten: Der Haken geht aus, die Messwerte verschwinden wieder.
  await karte.getByRole('checkbox', { name: /Folgt dem Trocknungsplan/ }).uncheck()
  await expect(karte.getByText('nicht eingebunden')).toBeVisible()
  await expect(karte.getByText('Höchststufe')).toHaveCount(0)
})

test('Scheitert das Speichern, springt der Haken zurück und die Karte sagt es', async ({ page }) => {
  await page.route(/\/api\/trocknung\/zuluft$/, (route) => route.request().method() === 'PUT'
    ? route.fulfill({ status: 400, json: { error: 'kein_port', message: 'Der Zuluft ist noch kein Schalter zugeordnet.' } })
    : route.fulfill({ json: zuluft() }))
  await page.goto('/steuerung/zuluft', { waitUntil: 'networkidle' })
  const karte = page.locator('[data-audit="zuluft-trocknung"]').locator('xpath=ancestor::article')
  const haken = karte.getByRole('checkbox', { name: /Folgt dem Trocknungsplan/ })
  await haken.check()
  await expect(karte.getByText('Nicht gespeichert')).toBeVisible()
  await expect(haken).not.toBeChecked()
})

test('Steuerung › Trocknung zeigt die Zuluft nur als Anzeige, wenn sie dem Plan folgt', async ({ page }) => {
  await page.route(/\/api\/steuerung$/, (route) => route.fulfill({
    json: {
      haErreichbar: true, standUtc: new Date().toISOString(), nichtEingerichtet: [], zelte: [{ id: 2, name: 'Zelt-Trocknung' }],
      module: [{ kennung: 'trocknung', titel: 'Trocknung', status: 'an', kurz: 'Beispiel', wert: '1', unterzeile: 'Beispiel', hatDetail: true, zeltId: 2 }],
    },
  }))
  await page.route(/\/api\/tents\/2\/trocknung\/steuerung$/, (route) => route.fulfill({
    json: { trocknet: true, betrieb: 'regeln', portsErreichbar: true, tag: 8, tageGeplant: 10, etappe: 'Ausklang', tempMin: 16, tempMax: 18, feuchteMin: 56, feuchteMax: 60, geraete: [], inArbeit: null, abluftMinStufe: 0 },
  }))
  await page.route(/\/api\/trocknung\/zuluft$/, (route) => route.fulfill({ json: zuluft({ folgt: true, regelt: true }) }))
  await page.goto('/steuerung/trocknung', { waitUntil: 'networkidle' })
  const karte = page.locator('[data-audit="trocknung-zuluft"]').locator('xpath=ancestor::article')
  await expect(karte).toBeVisible()
  await expect(karte.getByText('saugt', { exact: true })).toBeVisible()
  await expect(karte.getByRole('link', { name: /Zuluft-Steuerung öffnen/ })).toBeVisible()
  // Nur Anzeige: kein Haken, kein Stepper.
  await expect(karte.getByRole('checkbox')).toHaveCount(0)
})
