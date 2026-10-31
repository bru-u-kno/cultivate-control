import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Einrichtung (cc.287): Schritteliste mit Live-Stand, Anlage, Reiterleiste über beide Seiten, ein Menüpunkt.
 * Nur lesen — nichts wird gespeichert.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — der Stand der Schritte wird aus der laufenden App gelesen.')
})

test('Schritte: jede Zeile hat einen Stand und ein Ziel, oben „x von y“ und der nächste Schritt', async ({ page }) => {
  await page.goto('/einrichtung', { waitUntil: 'networkidle' })
  await expect(page.getByRole('heading', { level: 1, name: 'Einrichtung' })).toBeVisible()
  const zeilen = page.locator('[data-audit="einrichtung-schritt"]')
  expect(await zeilen.count(), 'Keine Schritte — die Prüfung sähe nichts.').toBeGreaterThanOrEqual(6)
  for (const zeile of await zeilen.all()) {
    await expect(zeile.locator('.co-row-value')).toHaveText(/^(erledigt|offen|optional|nicht lesbar)$/)
    await expect(zeile).toHaveAttribute('href', /^\//)
  }
  await expect(page.locator('[data-audit="einrichtung-stand"]')).toContainText(/\d+ von \d+/)
  // Ein Schritt führt auf seine Seite — und wieder zurück (zweiter Durchgang mit anderem Schritt).
  await page.locator('[data-schritt="zuordnung"]').click()
  await expect(page).toHaveURL(/\/geraete\?reiter=zuordnung/)
  await page.goBack()
  await page.locator('[data-schritt="verbindung"]').click()
  await expect(page).toHaveURL(/\/geraete\?reiter=verbindung/)
})

test('Die Reiterleiste führt zwischen Schritten, Anlage und Technik hin und zurück', async ({ page }) => {
  await page.goto('/einrichtung', { waitUntil: 'networkidle' })
  const leiste = page.getByRole('tablist', { name: 'Einrichtung' })
  await leiste.locator('.v1-tab', { hasText: 'Anlage' }).click()
  await expect(page).toHaveURL(/bereich=anlage/)
  await expect(page.locator('[data-audit="einrichtung-anlage"]')).toContainText('Zelte & Räume')
  await leiste.locator('.v1-tab', { hasText: 'Technik' }).click()
  await expect(page).toHaveURL(/\/geraete/)
  const technik = page.getByRole('tablist', { name: 'Einrichtung' })
  await expect(technik.locator('.v1-tab.active')).toHaveText('Technik')
  await technik.locator('.v1-tab', { hasText: 'Schritte' }).click()
  await expect(page).toHaveURL(/\/einrichtung$/)
  await page.getByRole('tablist', { name: 'Einrichtung' }).locator('.v1-tab', { hasText: 'Anlage' }).click()
  await page.getByRole('tablist', { name: 'Einrichtung' }).locator('.v1-tab', { hasText: 'Technik' }).click()
  await expect(page).toHaveURL(/\/geraete/)
})

test('Die alte Seite „Erste Schritte“ leitet auf die Einrichtung', async ({ page }) => {
  await page.goto('/start', { waitUntil: 'networkidle' })
  expect(new URL(page.url()).pathname).toBe('/einrichtung')
})

test('Im Menü steht die Einrichtung einmal — die Seiten dahinter nicht einzeln', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 900 })
  await page.goto('/', { waitUntil: 'networkidle' })
  await page.getByText('Mehr', { exact: true }).first().click()
  const gruppe = page.locator('.v1-mobile-more-panel section').filter({ has: page.locator('.v1-nav-group-head', { hasText: /^Einrichtung$/ }) })
  await expect(gruppe.locator('a.v1-more-tile', { hasText: /^Einrichtung$/ })).toHaveCount(1)
  for (const weg of ['Geräte & Entitäten', 'Zelte & Räume', 'Hydro-Systeme', 'Wasser', 'Home Assistant (Verbindung)', 'Sensoren & Wartung (erfassen)']) {
    await expect(page.locator('.v1-mobile-more-panel a.v1-more-tile', { hasText: new RegExp(`^${weg.replace(/[()&]/g, '\\$&')}$`) }), `${weg} steht noch im Menü`).toHaveCount(0)
  }
  await expect(gruppe.locator('a.v1-more-tile', { hasText: 'Handy' })).toHaveCount(1)
})
