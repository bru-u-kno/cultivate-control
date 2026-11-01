import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Der eigene Trocknungsplan des Trockenzelts (A-029) am echten Weg: Plan › Trocknung, Etappen ändern, speichern, nach dem Neuladen
 * wiederfinden, zu lange Pläne, Etappe löschen — und dass die Trocknung kein Teil des Düngeplans mehr ist.
 *
 * Der Demobestand hat kein Zelt mit Zweck „Trocknung“, und andere Prüfungen zählen dessen Zelte (`ernte-trockenzelt` erwartet
 * keines) — deshalb hält ein EINZIGER Fall das Schloss von Anlegen bis Abräumen.
 */
type Etappe = { name: string; dauerTage: number; lufttemperaturC: number }
type Plan = { zeltId: number; istStandard: boolean; etappen: Etappe[] }

let zeltId: number | null = null

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — der Trocknungsplan braucht die laufende App.')
  await nimmSchloss()
  const zelt = await request.post('/api/settings/tents', { data: { name: `Plan-Test ${Date.now()}`, tentType: 'Drying', status: 'Active', displayOrder: 99 } })
  if (zelt.ok()) zeltId = (await zelt.json()).id
})

test.afterEach(async ({ request }) => {
  if (zeltId != null) await request.delete(`/api/settings/tents/${zeltId}`)
  zeltId = null
  await gibSchloss()
})

test('Trocknungsplan: ändern, speichern, wiederfinden — unabhängig vom Düngeplan', async ({ page, request }) => {
  darfUeberspringen(zeltId == null, 'Testzelt ließ sich nicht anlegen.')

  await test.step('Der Reiter „Trocknung“ zeigt den Standard; der Düngeplan hat keinen Bereich „Trocknung“ mehr', async () => {
    await page.goto('/plan?art=trocknung', { waitUntil: 'networkidle' })
    await expect(page.locator('[data-audit=trocknung-reiter]')).toBeVisible()
    await expect(page.getByText('Eigener Plan, unabhängig vom Düngeplan')).toBeVisible()
    await expect(page.getByText('Etappe 1 von 3')).toBeVisible()
    await expect(page.locator('[data-audit=trocknung-speichern]')).toBeDisabled() // nichts geändert
    await expect(page.getByRole('button', { name: /Plan von/ }), 'Die Auswahl „Plan von“ gehört zum Düngeplan.').toHaveCount(0)

    await page.getByRole('tablist', { name: 'Art des Plans' }).getByRole('button', { name: 'Düngeplan' }).click()
    await expect(page.locator('[data-audit=trocknung-reiter]')).toHaveCount(0)
    await expect(page).not.toHaveURL(/art=trocknung/)
  })

  await test.step('Etappen ändern und speichern; zweites Speichern ersetzt den Plan als Ganzes', async () => {
    await page.goto('/plan?art=trocknung', { waitUntil: 'networkidle' })
    await page.locator('#trocknung-etappenname').fill('Kalt')
    await page.locator('[data-audit=trocknung-temp]').fill('17,5')
    await page.locator('[data-audit=trocknung-etappe-danach]').click()
    await expect(page.getByText('Etappe 2 von 4')).toBeVisible()
    await page.locator('#trocknung-etappenname').fill('Zusatz')
    await page.locator('[data-audit=trocknung-dauer]').fill('2')
    await page.locator('[data-audit=trocknung-speichern]').click()
    await expect(page.getByText(/Trocknungsplan gespeichert — er gilt ab sofort für/)).toBeVisible()

    const plan = (await (await request.get(`/api/tents/${zeltId}/trocknung/plan`)).json()) as Plan
    expect(plan.istStandard).toBe(false)
    expect(plan.etappen.map((e) => e.name)).toEqual(['Kalt', 'Zusatz', 'Hauptphase', 'Ausklang'])
    expect(plan.etappen[0].lufttemperaturC).toBe(17.5)
    expect(plan.etappen[1].dauerTage).toBe(2)

    // ZWEITES Speichern (Dauer der ersten Etappe 3 → 4): wieder der ganze Plan, kein neues Programm, keine Kopie.
    await page.getByRole('button', { name: 'Vorige Etappe' }).click()
    await page.locator('[data-audit=trocknung-dauer]').fill('4')
    await page.locator('[data-audit=trocknung-speichern]').click()
    await expect(page.getByText(/Trocknungsplan gespeichert/)).toBeVisible()
    const zweites = (await (await request.get(`/api/tents/${zeltId}/trocknung/plan`)).json()) as Plan
    expect(zweites.etappen[0].dauerTage).toBe(4)
    expect(zweites.etappen).toHaveLength(4)
  })

  await test.step('Nach dem Neuladen steht der Plan da; Standard wiederherstellen und speichern bringt ihn zurück', async () => {
    await page.reload({ waitUntil: 'networkidle' })
    await expect(page.getByText('Etappe 1 von 4')).toBeVisible()
    await expect(page.locator('#trocknung-etappenname')).toHaveValue('Kalt')
    await expect(page.locator('[data-audit=trocknung-temp]')).toHaveValue('17,5')

    await page.getByRole('button', { name: /Standard „langsam trocknen“ wiederherstellen/ }).click()
    await expect(page.getByText('Etappe 1 von 3')).toBeVisible()
    await page.locator('[data-audit=trocknung-speichern]').click()
    await expect(page.getByText(/Trocknungsplan gespeichert/)).toBeVisible()
    expect(((await (await request.get(`/api/tents/${zeltId}/trocknung/plan`)).json()) as Plan).istStandard).toBe(true)
  })
})

test('Trocknungsplan: zu lange Pläne werden gemeldet, eine Etappe lässt sich löschen, die letzte bleibt', async ({ page }) => {
  darfUeberspringen(zeltId == null, 'Testzelt ließ sich nicht anlegen.')
  await page.goto('/plan?art=trocknung', { waitUntil: 'networkidle' })

  await page.locator('[data-audit=trocknung-dauer]').fill('30')
  await expect(page.getByText(/Zusammen \d+ Tage — das Trocknungsfenster endet 21 Tage nach der Ernte/)).toBeVisible()
  await expect(page.locator('[data-audit=trocknung-speichern]')).toBeDisabled()

  await page.getByRole('button', { name: 'Verwerfen' }).click()
  await page.getByRole('button', { name: '✕ Etappe löschen' }).click()
  await expect(page.getByText('Etappe 1 von 2')).toBeVisible()
  await page.getByRole('button', { name: '✕ Etappe löschen' }).click()
  await expect(page.getByText('Etappe 1 von 1')).toBeVisible()
  await expect(page.getByRole('button', { name: '✕ Etappe löschen' })).toBeDisabled()
})
