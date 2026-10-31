import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * „Mappen" am Zelt führt auf die Zuordnungsseite — und zwar zum richtigen Zelt und zur richtigen Zeile (cc.284).
 *
 * Der Demobestand hat kein Trockenzelt, und andere Dateien zählen dessen Zelte: gespeichert wird hier nichts. Das
 * Trockenzelt (ohne angelegte Sensorzeilen, wie bei der Anlage in der echten Anlage) wird in die Antworten gelegt.
 */
const TROCKENZELT = {
  id: 99, name: 'Trockenzelt (Testdaten)', kind: 'Growzelt', tentType: 'Drying', status: 'Active', displayOrder: 99, accentColor: '#22c55e',
  widthCm: 120, depthCm: 120, tentHeightCm: 200, exhaustFanCount: 1, exhaustM3h: 348, circulationFanCount: 0,
  co2Available: false, hasCo2Enrichment: false, activeGrowCount: 0, archivedGrowCount: 0, activeSetupCount: 0, archivedSetupCount: 0,
  sensors: [], cameras: [],
}

async function trockenzeltEinlegen(page: Page) {
  await page.route(/\/api\/settings(\?.*)?$/, async (route) => {
    const antwort = await route.fetch()
    const koerper = await antwort.json()
    await route.fulfill({ response: antwort, json: { ...koerper, tents: [...koerper.tents, TROCKENZELT] } })
  })
  await page.route(/\/api\/settings\/tents\?.*$/, async (route) => {
    const antwort = await route.fetch()
    const koerper = await antwort.json()
    await route.fulfill({ response: antwort, json: [...koerper, TROCKENZELT] })
  })
}

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — der Sprung braucht die laufende App mit Demobestand.')
})

test('Direktlink wählt das Trockenzelt, zeigt nur dessen Messwerte und hebt die Zeile hervor', async ({ page }) => {
  await trockenzeltEinlegen(page)
  await page.goto('/geraete?reiter=zuordnung&zelt=99&messwert=Humidity', { waitUntil: 'networkidle' })

  // cc.285: eine Zeile je Zelt unter der Messgröße. Das Trockenzelt hat genau drei (Temperatur, Feuchte, VPD) und keine in „RDWC/DWC".
  const seine = page.locator('[data-audit="ha-entity-row"][data-tent="99"]')
  await expect(seine).toHaveCount(3)
  const treffer = page.locator('[data-metric="Humidity"][data-tent="99"]')
  await expect(treffer).toHaveClass(/ha-treffer/)
  await expect(treffer).toBeInViewport()
  await expect(page.locator('[data-metric="AirTemperature"][data-tent="99"]')).not.toHaveClass(/ha-treffer/)
  await page.getByRole('tab', { name: 'RDWC/DWC' }).click()
  await expect(page.locator('[data-audit="ha-entity-row"][data-tent="99"]')).toHaveCount(0)
})

test('Ein Messwert aus „Reservoir" öffnet die richtige Gruppe beim Produktionszelt', async ({ page }) => {
  await page.goto('/geraete?reiter=zuordnung&messwert=ReservoirPh', { waitUntil: 'networkidle' })
  await expect(page.locator('[data-metric="ReservoirPh"]').first()).toHaveClass(/ha-treffer/)
})

test('„Mappen" in der Zeltliste springt zum Zelt und zur fehlenden Messgröße', async ({ page }) => {
  await trockenzeltEinlegen(page)
  await page.goto('/zelte', { waitUntil: 'networkidle' })
  await page.getByText('Trockenzelt (Testdaten)').first().click()
  await expect(page.getByText('Luftwechsel')).toHaveCount(0)
  await page.getByRole('link', { name: 'Mappen' }).first().click()
  await expect(page).toHaveURL(/\/geraete\?reiter=zuordnung&zelt=99&messwert=AirTemperature/)
  await expect(page.locator('[data-metric="AirTemperature"][data-tent="99"]')).toHaveClass(/ha-treffer/)
})

test('Eine alte Adresse der Home-Assistant-Seite führt auf die Zuordnung weiter', async ({ page }) => {
  await page.goto('/home-assistant?zelt=1&messwert=Humidity', { waitUntil: 'networkidle' })
  await expect(page).toHaveURL(/\/geraete\?reiter=zuordnung&zelt=1&messwert=Humidity/)
  await expect(page.locator('[data-metric="Humidity"][data-tent="1"]')).toHaveClass(/ha-treffer/)
})
