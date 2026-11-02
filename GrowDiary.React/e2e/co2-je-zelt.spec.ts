import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * CO₂ je Zelt (Oberfläche): Der Zeltwechsel erscheint nur bei mehr als einem Zelt mit CO₂-Steuerung, lädt und speichert mit dem gewählten
 * Zelt, zeigt dessen Flasche, und Probeschaltung sowie Absicherung gibt es vorerst nur beim ersten Zelt.
 *
 * Der Demobestand hat kein CO₂-Zelt und darf hier nicht verändert werden: die Seite mit zwei Zelten entsteht aus der echten Antwort,
 * die per Route um die Zelte ergänzt wird. Gespeichert wird nichts.
 */
const ZELTE = [
  { id: 1, name: 'Zelt A', modul: 'co2' },
  { id: 2, name: 'Zelt B', modul: 'co2-z2' },
]

async function zweiZelteEinlegen(page: Page, anfragen: string[]) {
  await page.route(/\/api\/steuerung\/co2(\?.*)?$/, async (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    anfragen.push(route.request().url())
    // Das Backend kennt im Demobestand kein zweites CO₂-Zelt (404) — die Basisseite kommt ohne die Zelt-Angabe.
    const url = new URL(route.request().url())
    const zelt2 = url.searchParams.get('zeltId') === '2'
    url.searchParams.delete('zeltId')
    const antwort = await route.fetch({ url: url.toString() })
    const seite = await antwort.json()
    await route.fulfill({
      response: antwort,
      json: {
        ...seite,
        zelte: ZELTE,
        zeltId: zelt2 ? 2 : 1,
        einstellungen: { ...seite.einstellungen, kostenArtikelId: seite.artikel[zelt2 ? 1 : 0].id },
        zeltvolumenQuelle: zelt2
          ? { ausMassen: true, volumenM3: 2, zeltName: 'Zelt B', masse: '100×100×200 cm', grund: null }
          : { ausMassen: true, volumenM3: 2.88, zeltName: 'Zelt A', masse: '120×120×200 cm', grund: null },
      },
    })
  })
}

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test('Ohne zweites CO₂-Zelt gibt es keinen Zeltwechsel', async ({ page }) => {
  await page.goto('/steuerung/co2', { waitUntil: 'networkidle' })
  await expect(page.getByRole('heading', { name: /CO₂-Begasung/ })).toBeVisible()
  await expect(page.locator('[data-audit="co2-zeltwechsel"]')).toHaveCount(0)
})

test('Zwei CO₂-Zelte: Wechsel lädt das andere Zelt, zeigt dessen Flasche und blendet Probeschaltung und Absicherung aus', async ({ page }) => {
  const anfragen: string[] = []
  await zweiZelteEinlegen(page, anfragen)
  await page.goto('/steuerung/co2', { waitUntil: 'networkidle' })

  const wechsel = page.locator('[data-audit="co2-zeltwechsel"]')
  await expect(wechsel.getByRole('tab')).toHaveCount(2)
  await expect(wechsel.getByRole('tab', { name: 'Zelt A' })).toHaveAttribute('aria-current', 'true')
  expect(anfragen.at(-1)).not.toContain('zeltId')                       // erstes Zelt: ohne Angabe wie bisher

  await page.locator('.v1-tab', { hasText: 'Dosierung' }).click()
  const zeile = page.locator('[data-audit="co2-zeltvolumen-aus-massen"]')
  await expect(zeile).toContainText('2,88')
  await expect(page.getByRole('button', { name: 'Ventil kurz öffnen' })).toBeVisible()   // erstes Zelt: Probeschaltung da
  const flasche1 = await page.locator('.st-feldzeile', { hasText: 'Flasche' }).first().innerText()

  await wechsel.getByRole('tab', { name: 'Zelt B' }).click()
  await expect(wechsel.getByRole('tab', { name: 'Zelt B' })).toHaveAttribute('aria-current', 'true')
  await expect(page).toHaveURL(/zelt=2/)
  await expect.poll(() => anfragen.at(-1) ?? '').toContain('zeltId=2')
  await page.locator('.v1-tab', { hasText: 'Dosierung' }).click()
  await expect(page.locator('[data-audit="co2-zeltvolumen-aus-massen"]')).toContainText(/100×100×200 cm\s*2\s*m³/)
  const flasche2 = await page.locator('.st-feldzeile', { hasText: 'Flasche' }).first().innerText()
  expect(flasche2).not.toEqual(flasche1)                                 // jedes Zelt führt seine eigene Flasche
  await expect(page.getByRole('button', { name: 'Ventil kurz öffnen' })).toHaveCount(0)  // weiteres Zelt: keine Probeschaltung

  // Zurück zum ersten Zelt: die Adresse trägt keinen Zelt-Parameter mehr.
  await wechsel.getByRole('tab', { name: 'Zelt A' }).click()
  await expect(page).not.toHaveURL(/zelt=/)
  await page.locator('.v1-tab', { hasText: 'Dosierung' }).click()
  await expect(page.locator('[data-audit="co2-zeltvolumen-aus-massen"]')).toContainText('2,88')
})

test('Direktlink mit ?zelt=2 öffnet das zweite Zelt', async ({ page }) => {
  const anfragen: string[] = []
  await zweiZelteEinlegen(page, anfragen)
  await page.goto('/steuerung/co2?zelt=2', { waitUntil: 'networkidle' })
  await expect(page.locator('[data-audit="co2-zeltwechsel"]').getByRole('tab', { name: 'Zelt B' })).toHaveAttribute('aria-current', 'true')
  expect(anfragen.some((u) => u.includes('zeltId=2'))).toBe(true)
})
