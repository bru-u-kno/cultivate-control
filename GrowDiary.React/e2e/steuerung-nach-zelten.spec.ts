import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Steuerungen je Zelt (Etappe 1): Mit zwei oder mehr Zelten gliedert die Übersicht nach Zelten, die CO₂-Zeile eines weiteren Zeltes führt auf die
 * CO₂-Seite dieses Zeltes, und die Zuweisung speichert nur die geänderten Steuerungen. Mit einem Zelt bleibt es bei der einen Liste.
 *
 * Der Demobestand hat keine Steuerungen eingerichtet; die Übersicht entsteht aus einer nachgestellten Antwort. Gespeichert wird nichts —
 * auch das Speichern der Zuweisung geht gegen eine Route.
 */
const zeile = (kennung: string, titel: string, zeltId: number | null, status = 'an') => (
  { kennung, titel, status, kurz: 'Beispiel', wert: '1', unterzeile: 'Beispiel', hatDetail: kennung !== 'abluft', zeltId }
)
const MODULE = [
  zeile('co2', 'CO₂-Begasung', 1), zeile('co2', 'CO₂-Begasung', 2),
  zeile('entfeuchter', 'Entfeuchter', 1, 'aus'), zeile('licht', 'Licht', 2), zeile('chiller', 'Wasserkühler', null),
]
const ZELTE = [{ id: 1, name: 'Zelt A' }, { id: 2, name: 'Zelt B' }]

async function uebersicht(page: Page, zelte: typeof ZELTE) {
  await page.route(/\/api\/steuerung$/, (route) => route.fulfill({
    json: { haErreichbar: true, module: zelte.length < 2 ? MODULE.slice(0, 1) : MODULE, nichtEingerichtet: [], standUtc: new Date().toISOString(), zelte },
  }))
}

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test('Zwei Zelte: Übersicht nach Zelten, CO₂ des zweiten Zeltes führt auf dessen Seite', async ({ page }) => {
  await uebersicht(page, ZELTE)
  await page.goto('/steuerung', { waitUntil: 'networkidle' })

  const bloecke = page.locator('[data-audit="steuerung-zelt"]')
  await expect(bloecke).toHaveCount(3) // Zelt A, Zelt B, „Noch keinem Zelt zugewiesen“
  await expect(bloecke.nth(0)).toContainText('Zelt A')
  await expect(bloecke.nth(0)).toContainText('2 Steuerungen')
  await expect(bloecke.nth(1)).toContainText('Zelt B')
  await expect(bloecke.nth(2)).toContainText('Noch keinem Zelt zugewiesen')
  await expect(bloecke.nth(2)).toContainText('Wasserkühler')

  // Einklappen blendet die Zeilen aus und bleibt am Gerät gemerkt.
  await bloecke.nth(0).getByRole('button', { name: /Zelt A/ }).click()
  await expect(bloecke.nth(0).locator('.st-zeile')).toHaveCount(0)

  await bloecke.nth(1).locator('.st-zeile', { hasText: 'CO₂-Begasung' }).click()
  await expect(page).toHaveURL(/\/steuerung\/co2\?zelt=2/)
})

test('Ein Zelt: eine Liste wie bisher, keine Zeltköpfe, keine Zuweisung', async ({ page }) => {
  await uebersicht(page, ZELTE.slice(0, 1))
  await page.goto('/steuerung', { waitUntil: 'networkidle' })
  await expect(page.locator('[data-audit="steuerung-zelt"]')).toHaveCount(0)
  await expect(page.locator('.st-liste .st-zeile')).toHaveCount(1)
  await expect(page.locator('[data-audit="steuerung-zuweisung"]')).toHaveCount(0)
})

test('Zuweisung: wählt je Steuerung ein Zelt und schickt nur die Änderung', async ({ page }) => {
  await uebersicht(page, ZELTE)
  let gesendet: unknown = null
  await page.route(/\/api\/steuerung\/zuweisung$/, async (route) => {
    if (route.request().method() === 'PUT') {
      gesendet = route.request().postDataJSON()
      return route.fulfill({ json: { zelte: ZELTE, eintraege: [{ kennung: 'chiller', titel: 'Wasserkühler', zeltId: 2 }, { kennung: 'licht', titel: 'Licht', zeltId: 2 }] } })
    }
    return route.fulfill({ json: { zelte: ZELTE, eintraege: [{ kennung: 'chiller', titel: 'Wasserkühler', zeltId: null }, { kennung: 'licht', titel: 'Licht', zeltId: 2 }] } })
  })
  await page.goto('/steuerung', { waitUntil: 'networkidle' })

  await page.getByRole('button', { name: 'Steuerungen Zelten zuweisen', exact: true }).click()
  const speichern = page.getByRole('button', { name: 'Speichern' })
  await expect(speichern).toBeDisabled() // nichts geändert
  await page.getByRole('group', { name: 'Zelt für Wasserkühler' }).getByRole('button', { name: 'Zelt B' }).click()
  await expect(speichern).toBeEnabled()
  await speichern.click()

  await expect(page.getByText('Gespeichert. Die Übersicht ist neu gegliedert.')).toBeVisible()
  expect(gesendet).toEqual({ zuordnungen: { chiller: 2 } })
})
