import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Zuordnung (cc.288): Messgröße als Überschrift, Block auf/zu nach Zählung, „Nur offene zeigen“, Ansicht „Nach Zelt“.
 * Nur lesen — gespeichert wird hier nichts (die Speicher-Rundwege stehen in den Fällen zu Außenwerten und Raum).
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Zuordnung braucht den Demobestand.')
})

const zeilen = (page: import('@playwright/test').Page) => page.locator('.ha-zuordnung [data-audit="ha-entity-row"]:visible')

test('Nach Messgröße: große Überschrift, Lücke im Kernwert steht offen, „Nur offene“ lässt sich ein- und ausschalten', async ({ page }) => {
  await page.setViewportSize({ width: 369, height: 900 })
  await page.goto('/geraete?reiter=zuordnung', { waitUntil: 'networkidle' })
  const temperatur = page.locator('[data-audit="ha-messgroesse"]').filter({ hasText: 'Lufttemperatur' })
  await expect(temperatur, 'Überschrift „Lufttemperatur“ fehlt').toHaveCount(1)
  await expect(temperatur.locator('.kk-kopf')).toHaveAttribute('aria-expanded', 'true')
  // Ein Block ohne Lücke im Kernwert ist zu und sagt es in der Kurzauskunft.
  const hoehe = await temperatur.locator('.kk-titel').evaluate((el) => parseFloat(getComputedStyle(el).fontSize))
  expect(hoehe, 'Die Überschrift ist nicht größer als der Zeilentext').toBeGreaterThan(16)

  const schalter = page.getByRole('checkbox', { name: 'Nur offene zeigen' })
  await expect(schalter).toBeVisible()
  for (let runde = 0; runde < 2; runde++) {
    await schalter.check()
    const nurOffene = await zeilen(page).count()
    await schalter.uncheck()
    const alle = await zeilen(page).count()
    expect(alle, `Runde ${runde + 1}: ohne Filter stehen nicht mehr Zeilen da`).toBeGreaterThan(nurOffene)
  }
})

test('Nach Zelt: ein Zelt, seine Messgrößen untereinander; zurück zu „Nach Messgröße“ und wieder hin', async ({ page }) => {
  await page.goto('/geraete?reiter=zuordnung', { waitUntil: 'networkidle' })
  const ansicht = page.getByRole('group', { name: 'Ansicht' })
  for (let runde = 0; runde < 2; runde++) {
    await ansicht.getByRole('button', { name: 'Nach Zelt' }).click()
    await expect(page.locator('[data-audit="ha-zelt-wahl"]')).toBeVisible()
    const zelte = new Set(await zeilen(page).evaluateAll((els) => els.map((el) => el.getAttribute('data-tent'))))
    expect(zelte.size, `Runde ${runde + 1}: „Nach Zelt“ zeigt mehr als ein Zelt`).toBe(1)
    expect(await zeilen(page).count()).toBeGreaterThanOrEqual(2)
    await ansicht.getByRole('button', { name: 'Nach Messgröße' }).click()
    await expect(page.locator('[data-audit="ha-zelt-wahl"]')).toHaveCount(0)
  }
})

test('Die Gruppenleiste trägt die Zahl der offenen Kernwerte und wechselt die Gruppe', async ({ page }) => {
  await page.goto('/geraete?reiter=zuordnung', { waitUntil: 'networkidle' })
  const gruppen = page.getByRole('group', { name: 'Messgruppe' })
  await expect(gruppen.getByRole('button', { name: /^Zelt/ })).toHaveAttribute('aria-pressed', 'true')
  await gruppen.getByRole('button', { name: /^RDWC\/DWC/ }).click()
  await expect(gruppen.getByRole('button', { name: /^RDWC\/DWC/ })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('[data-audit="ha-messgroesse"]').filter({ hasText: 'pH' })).toHaveCount(1)
  await gruppen.getByRole('button', { name: 'Kameras' }).click()
  await expect(page.locator('[data-audit="ha-kameras-zelt"]').first()).toBeVisible()
})
