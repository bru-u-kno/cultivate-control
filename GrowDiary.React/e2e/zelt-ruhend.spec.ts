import { test, expect, type Page } from '@playwright/test'
import { darfUeberspringen } from './pflicht'
import { abraeumen, eigenenGrowAnlegen, type EigenerGrow } from './eigener-grow'

/**
 * „Zelt ruhend" (cc.277): ruhend schalten, wieder aktiv, und die Frage nach der Ernte.
 *
 * Läuft mit einem eigenen Grow (der neueste Grow bestimmt das Zelt der Steuerung) und stellt den Ruhezustand am Ende
 * zurück — der Testbestand ist geteilt. Der Demobestand führt Home Assistant selbst nach (Licht, Zusatz-Entfeuchter,
 * Entfeuchter), deshalb sieht der Fall echte Zustandswechsel und nicht nur Antworten.
 */
let eigener: EigenerGrow | null = null

test.describe.configure({ mode: 'serial' })

async function blockOeffnen(page: Page) {
  await page.goto('/zelte', { waitUntil: 'networkidle' })
  const items = page.locator('button.tn-item')
  const anzahl = await items.count()
  const block = page.locator('[data-audit=zelt-ruhe]')
  for (let i = 0; i < anzahl; i++) {
    if (await block.count()) break
    await items.nth(i).click()
    await page.waitForTimeout(400)
  }
  await expect(block, 'Der Betrieb-Block steht am Zelt des neuesten Grows.').toBeVisible()
  return block
}

test.describe('Zelt ruhend', () => {
  test.beforeAll(async ({ playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL })
    try {
      eigener = await eigenenGrowAnlegen(api, 'Rundweg-Ruhe')
    } finally {
      await api.dispose()
    }
  })

  test.afterAll(async ({ playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL })
    try {
      // Ruhend wieder aufheben, sonst steht der geteilte Bestand mit ausgeschaltetem Licht da.
      const stand = await (await api.get('/api/steuerung/zelt-ruhe')).json().catch(() => null)
      if (stand?.ruhend) await api.post('/api/steuerung/zelt-ruhe/aktiv')
      await abraeumen(api, eigener)
    } finally {
      await api.dispose()
    }
  })

  test('Zelt-Karte: ruhend schalten, Neuladen, wieder aktiv — und ein zweites Mal', async ({ page }) => {
    darfUeberspringen(eigener == null, 'Kein eigener Grow anlegbar — läuft die App unter GROW_OS_URL?')

    let block = await blockOeffnen(page)
    await expect(block).toContainText('In Betrieb')
    await expect(block.getByText('Licht', { exact: true })).toBeVisible()

    await block.getByRole('button', { name: 'Zelt ruhend schalten' }).click()
    await expect(block).toContainText('Ruhend seit')
    await expect(block.locator('[data-audit=zelt-ruhe-ergebnis]')).toContainText('ausgeschaltet')

    // Der Zustand liegt im Fork, nicht in der Seite: nach dem Neuladen steht er noch da.
    block = await blockOeffnen(page)
    await expect(block).toContainText('Ruhend seit')
    await expect(block.getByRole('button', { name: 'Zelt wieder aktiv' })).toBeVisible()

    await block.getByRole('button', { name: 'Zelt wieder aktiv' }).click()
    await expect(block).toContainText('In Betrieb')
    await expect(block.getByRole('button', { name: 'Zelt ruhend schalten' })).toBeVisible()

    // Zweiter Durchgang, ohne Neuladen dazwischen.
    await block.getByRole('button', { name: 'Zelt ruhend schalten' }).click()
    await expect(block).toContainText('Ruhend seit')
    await block.getByRole('button', { name: 'Zelt wieder aktiv' }).click()
    await expect(block).toContainText('In Betrieb')
  })

  test('Ernte abschließen: das Blatt fragt, „Alles laufen lassen“ schaltet nichts', async ({ page, request }) => {
    darfUeberspringen(eigener == null, 'Kein eigener Grow anlegbar — läuft die App unter GROW_OS_URL?')

    await page.goto(`/grows/${eigener!.growId}/harvest`, { waitUntil: 'networkidle' })
    await page.getByRole('button', { name: 'Speichern & Grow abschließen' }).click()

    const blatt = page.locator('[data-audit=zelt-ruhe-blatt]')
    await expect(blatt, 'Nach dem Abschluss fragt ein Blatt nach den Geräten im leeren Zelt.').toBeVisible()
    await expect(blatt).toContainText('jetzt:')

    await page.locator('[data-audit=zelt-ruhend-nein]').click()
    await expect(page).toHaveURL(/\/archiv/)

    const stand = await (await request.get('/api/steuerung/zelt-ruhe')).json()
    expect(stand.ruhend, '„Alles laufen lassen“ darf nichts ausgeschaltet haben.').toBe(false)
  })
})
