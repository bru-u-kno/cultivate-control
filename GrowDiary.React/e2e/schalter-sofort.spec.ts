import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Haken, die auf den Server warten, müssen die Wahl des Nutzers sofort zeigen (Licht-Wächter, Steuerung › Licht).
 * Das Speichern wird hier angehalten und erst nach der Prüfung freigegeben; der Stand kommt aus dem Demobestand.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test('Licht-Wächter: der Haken ist beim Antippen sofort gesetzt und zeigt „wird gespeichert“', async ({ page, request }) => {
  const stand = await request.get('/api/steuerung/licht/waechter')
  darfUeberspringen(!stand.ok(), 'Der Demobestand liefert keinen Licht-Wächter (/api/steuerung/licht/waechter).')
  const vorher = (await stand.json()) as { einstellungen?: { aktiv: boolean } }
  darfUeberspringen(vorher.einstellungen == null, 'Der Licht-Wächter hat keine Einstellungen im Demobestand.')

  let freigeben: () => void = () => undefined
  const angehalten = new Promise<void>((f) => { freigeben = f })
  await page.route(/\/api\/steuerung\/licht\/waechter$/, async (route) => {
    if (route.request().method() !== 'PUT') return route.continue()
    await angehalten
    return route.fulfill({ json: { ...vorher, einstellungen: { ...vorher.einstellungen, aktiv: !vorher.einstellungen!.aktiv } } })
  })
  // Der Demobestand hat keine eingerichtete Licht-Steuerung; die Übersicht ist nachgestellt, die Seite selbst kommt aus dem Backend.
  await page.route(/\/api\/steuerung$/, (route) => route.fulfill({
    json: {
      haErreichbar: true, standUtc: new Date().toISOString(), nichtEingerichtet: [], zelte: [{ id: 1, name: 'Zelt' }],
      module: [{ kennung: 'licht', titel: 'Licht', status: 'an', kurz: 'Beispiel', wert: '1', unterzeile: 'Beispiel', hatDetail: true, zeltId: 1 }],
    },
  }))
  await page.goto('/steuerung/licht', { waitUntil: 'networkidle' })
  await page.getByRole('button', { name: 'Wächter', exact: true }).click()

  const haken = page.locator('label.v1-switch').filter({ has: page.locator('strong', { hasText: /^Wächter$/ }) }).locator('input')
  const war = await haken.isChecked()
  await haken.click()
  await expect(haken).toBeChecked({ checked: !war })               // sofort, obwohl der Server noch nicht geantwortet hat
  await expect(page.locator('.v1-switch-wartet')).toContainText('wird gespeichert')

  freigeben()
  await expect(page.locator('.v1-switch-wartet')).toHaveCount(0)
  await expect(haken).toBeChecked({ checked: !war })
})
