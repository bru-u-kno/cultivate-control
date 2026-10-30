import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Ernte → Trockenzelt (cc.281). Nur lesend: Der Demobestand hat kein Zelt mit Zweck „Trocknung", und andere Dateien
 * zählen dessen Zelte und Grows — gespeichert wird hier nichts. Die Strecke mit Speichern (Ernte einem Trockenzelt
 * zuweisen → Zelt trocknet, Zelt des Grows ist frei) deckt `TrockenzeltErnteTests` im Backend ab.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — die Ernte-Seite braucht die laufende App.')
})

test('Ohne Trockenzelt bleibt die Ernte-Seite wie bisher: kein Feld „Trocknet in"', async ({ page }) => {
  await page.goto('/grows/1/harvest')
  await expect(page.getByText('Trocknungsdauer')).toBeVisible()
  await expect(page.locator('[data-audit=harvest-drying-tent]')).toHaveCount(0)
})

test('Die Trocknungs-Auskunft eines Zelts: nichts im Zelt, unbekanntes Zelt 404', async ({ request }) => {
  const leer = await request.get('/api/tents/1/trocknung')
  expect(leer.status()).toBe(200)
  expect((await leer.json()).trocknet).toBe(false)
  expect((await request.get('/api/tents/99999/trocknung')).status()).toBe(404)
})
