import { test, expect } from '@playwright/test'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Lichtplan (Steuerung › Licht › Lichtplan): Herstellerangabe und Messpunkte eintragen, speichern, die Seite neu laden und den
 * Wert wiederfinden; die Kurve und der Stufenvorschlag stehen da. Die Automatik bleibt aus. Der Fall schreibt in den geteilten
 * Bestand (Kalibrierung) und stellt sie am Ende leer zurück — das Schloss hält andere Dateien fern.
 */
test.describe.configure({ mode: 'serial' })

test.describe('Lichtplan', () => {
  test.beforeEach(async () => { await nimmSchloss() })
  test.afterEach(() => { gibSchloss() })

  // Leer anfangen und leer aufhören: der Bestand ist geteilt.
  const leeren = async (playwright: import('@playwright/test').PlaywrightWorkerArgs['playwright'], baseURL: string | undefined) => {
    const api = await playwright.request.newContext({ baseURL })
    try {
      await api.put('/api/steuerung/licht/kalibrierung', { data: { herstellerPpfd: null, herstellerAbstandCm: null, messpunkte: [], automatikStufe: false } })
    } finally {
      await api.dispose()
    }
  }
  test.beforeAll(async ({ playwright, baseURL }) => { await leeren(playwright, baseURL) })
  test.afterAll(async ({ playwright, baseURL }) => { await leeren(playwright, baseURL) })

  test('Kalibrierung speichern, neu laden, wiederfinden', async ({ page }) => {
    await page.goto('/steuerung/licht', { waitUntil: 'networkidle' })
    await page.getByText('Lichtplan', { exact: true }).first().click()

    const hersteller = page.getByLabel('Herstellerangabe Lichtwert')
    await expect(hersteller).toBeVisible()
    await hersteller.fill('1200')
    await page.getByLabel('Abstand der Herstellerangabe').fill('30')
    await page.getByRole('button', { name: '+ Messpunkt' }).click()
    await page.getByLabel('Messpunkt 1 Stufe').fill('5')
    await page.getByLabel('Messpunkt 1 Lichtwert').fill('610')
    await page.getByRole('button', { name: 'Kalibrierung speichern' }).click()
    await expect(page.getByRole('button', { name: 'Kalibrierung speichern' })).toHaveCount(0)

    // Zweites Mal speichern: ändern, speichern, noch einmal ändern, speichern.
    await page.getByLabel('Messpunkt 1 Lichtwert').fill('620')
    await page.getByRole('button', { name: 'Kalibrierung speichern' }).click()
    await expect(page.getByRole('button', { name: 'Kalibrierung speichern' })).toHaveCount(0)

    await page.reload({ waitUntil: 'networkidle' })
    await page.getByText('Lichtplan', { exact: true }).first().click()
    await expect(page.getByLabel('Herstellerangabe Lichtwert')).toHaveValue('1200')
    await expect(page.getByLabel('Messpunkt 1 Lichtwert')).toHaveValue('620')
    // Die Kurve steht da: Stufe 10 aus der Herstellerangabe.
    await expect(page.getByRole('cell', { name: 'Herstellerangabe' })).toBeVisible()
    // Die Automatik ist aus (Voreinstellung).
    await expect(page.getByRole('checkbox', { name: /Stufe automatisch nach Plan/ })).not.toBeChecked()
  })

  test('Ungültiger Wert wird abgewiesen und nicht gespeichert', async ({ page }) => {
    await page.goto('/steuerung/licht', { waitUntil: 'networkidle' })
    await page.getByText('Lichtplan', { exact: true }).first().click()
    await page.getByLabel('Herstellerangabe Lichtwert').fill('99999')
    await page.getByRole('button', { name: 'Kalibrierung speichern' }).click()
    await expect(page.getByText(/Wert zwischen 1 und 3000/)).toBeVisible()
  })

  test('Stunden an: die Ausschaltzeit wird aus Einschaltzeit und Stunden gerechnet, ohne zu speichern', async ({ page }) => {
    await page.goto('/steuerung/licht', { waitUntil: 'networkidle' })
    await page.getByText('Lichtplan', { exact: true }).first().click()
    const ein = await page.getByLabel('Blüte · Licht an um').inputValue()
    const [h, m] = ein.split(':').map(Number)
    await page.getByLabel('Blüte · Stunden an').fill('10')
    const aus = `${String((h + 10) % 24).padStart(2, '0')}:${String(m).padStart(2, '0')}`
    await expect(page.getByText(`Licht aus um ${aus}`).last()).toBeVisible()
    // Die Einschaltzeit verschieben lässt die Stunden stehen.
    await page.getByLabel('Blüte · Licht an um').fill('07:00')
    await expect(page.getByLabel('Blüte · Stunden an')).toHaveValue('10')
    await expect(page.getByText('Licht aus um 17:00').last()).toBeVisible()
  })
})
