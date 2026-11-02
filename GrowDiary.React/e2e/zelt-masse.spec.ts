import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Zeltmaße an EINER Stelle (cc.305): Volumen, Grundfläche und Luftwechsel rechnet das Backend aus den Maßen —
 * die Zeltseite zeigt sie, die CO₂-Seite führt kein eigenes Handfeld mehr, wenn das Volumen aus den Maßen kommt.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test('Zeltseite zeigt Größe, Grundfläche und Volumen aus den Maßen des Zeltes', async ({ page }) => {
  await page.goto('/zelte', { waitUntil: 'networkidle' })
  const gruppe = page.locator('[data-audit="tent-masse"]')
  // Das gewählte Zelt des Demobestands: 120 × 120 × 200 cm.
  await expect(gruppe).toContainText('120×120×200')
  await expect(gruppe).toContainText('1,44')
  await expect(gruppe).toContainText('2,88')
})

test('CO₂-Seite: Volumen aus den Maßen ist nur zu lesen, ohne CO₂-Zelt bleibt das Handfeld mit Grund', async ({ page }) => {
  // Ohne Eingriff: der Demobestand hat kein Zelt mit CO₂ → Handfeld, und die Seite sagt warum.
  await page.goto('/steuerung/co2', { waitUntil: 'networkidle' })
  await page.locator('.v1-tab', { hasText: 'Dosierung' }).click()
  await expect(page.getByLabel('Zeltvolumen')).toBeVisible()
  await expect(page.getByText('Bis dahin von Hand.')).toBeVisible()
  await expect(page.locator('[data-audit="co2-zeltvolumen-aus-massen"]')).toHaveCount(0)

  // Kommt das Volumen aus den Maßen (Antwort des Backends so abgewandelt), gibt es kein Eingabefeld mehr.
  await page.route(/\/api\/steuerung\/co2$/, async (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    const antwort = await route.fetch()
    const koerper = await antwort.json()
    await route.fulfill({ response: antwort, json: { ...koerper, zeltvolumenQuelle: { ausMassen: true, volumenM3: 2.88, zeltName: 'Zelt-RDWC', masse: '120×120×200 cm', grund: null } } })
  })
  await page.goto('/steuerung/co2', { waitUntil: 'networkidle' })
  await page.locator('.v1-tab', { hasText: 'Dosierung' }).click()
  const zeile = page.locator('[data-audit="co2-zeltvolumen-aus-massen"]')
  await expect(zeile).toContainText('Aus den Maßen von Zelt-RDWC')
  await expect(zeile).toContainText('2,88')
  await expect(page.getByLabel('Zeltvolumen')).toHaveCount(0)
})
