import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Zelt anlegen: Zweck „Trocknung" (cc.277) und kein Freitextfeld „Typ" mehr.
 *
 * Gespeichert wird nichts — andere Dateien zählen die Zelte des Demobestands. Geprüft wird, was das Formular zeigt:
 * die Zweck-Auswahl, das Fehlen von „Typ" und dass beim Trockenzelt Licht, CO₂ und Blatt-Offset verschwinden.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — das Formular braucht die laufende App.')
})

test('Zweck Trocknung blendet Licht, CO₂ und Blatt-Offset aus; „Typ" gibt es nicht', async ({ page }) => {
  await page.goto('/zelte/new')
  const form = page.locator('form.rc2-tent-form')
  await expect(form).toBeVisible()

  await expect(form.getByText('Typ', { exact: true })).toHaveCount(0)
  await expect(form.getByLabel(/Trocknung/)).toBeVisible()
  await expect(form.getByText('Lichttyp')).toBeVisible()
  await expect(form.getByText('CO₂-Sensor vorhanden')).toBeVisible()

  await form.getByLabel(/Trocknung/).check()
  await expect(form.getByText('Lichttyp')).toHaveCount(0)
  await expect(form.getByText('Watt', { exact: true })).toHaveCount(0)
  await expect(form.getByText('CO₂-Sensor vorhanden')).toHaveCount(0)
  await expect(form.getByText(/Blatttemperatur/)).toHaveCount(0)
  await expect(form.getByText('Abluft Anzahl')).toBeVisible()

  // Zurück auf „Blüte / Run": alles wieder da (der Zustand hängt nur am Zweck).
  await form.getByLabel(/Blüte \/ Run/).check()
  await expect(form.getByText('Lichttyp')).toBeVisible()
  await expect(form.getByText(/Blatttemperatur/)).toBeVisible()
})
