import { test, expect } from '@playwright/test'
import { darfUeberspringen } from './pflicht'
import { abraeumen, eigenenGrowAnlegen, type EigenerGrow } from './eigener-grow'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Der Abschluss eines Grows (cc.279): Pflanzen „geerntet", Reinigung des leeren Systems empfohlen.
 *
 * Zwei eigene Grows mit eigenen Systemen: Der eine wird über die Ernte-Seite abgeschlossen (Blatt mit dem Angebot „Danach:
 * Reinigung"), der andere über die Schnittstelle (Aufgaben › Fällige Routinen mit „Starten"). Der geteilte Demobestand bleibt
 * unberührt.
 */
let ernte: EigenerGrow | null = null
let aufgaben: EigenerGrow | null = null

test.describe.configure({ mode: 'serial' })

test.describe('Grow-Abschluss', () => {
  // Der Fall schreibt in den geteilten Bestand: das Schloss hält andere Dateien fern, solange er läuft.
  test.beforeEach(async () => { await nimmSchloss() })
  test.afterEach(() => { gibSchloss() })

  test.beforeAll(async ({ playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL })
    try {
      ernte = await eigenenGrowAnlegen(api, 'Abschluss-Ernte')
      aufgaben = await eigenenGrowAnlegen(api, 'Abschluss-Aufgaben')
    } finally {
      await api.dispose()
    }
  })

  test.afterAll(async ({ playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL })
    try {
      await abraeumen(api, ernte)
      await abraeumen(api, aufgaben)
    } finally {
      await api.dispose()
    }
  })

  test('Ernte abschließen: Pflanzen geerntet, das Blatt bietet die Reinigung an, „Reinigung jetzt starten“ legt sie an', async ({ page, request }) => {
    darfUeberspringen(ernte == null, 'Kein eigener Grow anlegbar — läuft die App unter GROW_OS_URL?')

    // Der Grow legt seine zwei Pflanzen selbst an (plantCount 2) — sie stehen vor dem Abschluss auf „aktiv".
    const vorher = (await (await request.get('/api/plants')).json() as Array<{ growId: number; plantStatus: string }>)
      .filter((p) => p.growId === ernte!.growId)
    expect(vorher.map((p) => p.plantStatus), 'Vor dem Abschluss sind beide Pflanzen „aktiv“.').toEqual(['Active', 'Active'])

    await page.goto(`/grows/${ernte!.growId}/harvest`, { waitUntil: 'networkidle' })
    await page.getByRole('button', { name: 'Speichern & Grow abschließen' }).click()

    const angebot = page.locator('[data-audit=reinigung-im-blatt]')
    await expect(angebot, 'Im Blatt nach der Ernte steht das Angebot „Danach: Reinigung“.').toBeVisible()
    await expect(angebot).toContainText('Reinigung des leeren Systems')

    // Pflanzen stehen auf „geerntet" — gelesen aus der Schnittstelle, nicht aus der Seite.
    const pflanzen = (await (await request.get('/api/plants')).json() as Array<{ growId: number; plantStatus: string }>)
      .filter((p) => p.growId === ernte!.growId)
    expect(pflanzen.length, 'Die zwei Pflanzen müssen auffindbar sein.').toBe(2)
    expect(pflanzen.map((p) => p.plantStatus), 'Nach dem Abschluss ist keine Pflanze mehr „aktiv“.').toEqual(['Harvested', 'Harvested'])

    await page.locator('[data-audit=reinigung-jetzt-starten]').click()
    await expect(page).toHaveURL(new RegExp(`/sops\\?growId=${ernte!.growId}$`))
    // Dort, wo man hinkommt, steht die Reinigung auch wirklich — kein Sprung auf eine Seite, die nichts von ihr zeigt.
    await expect(page.getByText('Laufende Routinen')).toBeVisible()
    await expect(page.getByText('Reinigung des leeren Systems').first()).toBeVisible()

    const instanzen = await (await request.get(`/api/sop-instances?growId=${ernte!.growId}`)).json() as Array<{ sopId: string; status: string }>
    expect(instanzen.some((i) => i.sopId === 'system-cleaning-empty' && i.status === 'Active'),
      'Die Reinigung des leeren Systems muss als laufende Routine angelegt sein.').toBe(true)
  })

  test('Aufgaben › Fällige Routinen: die Empfehlung steht dort, „Starten“ legt die Reinigung an, danach heißt der Knopf „Öffnen“', async ({ page, request }) => {
    darfUeberspringen(aufgaben == null, 'Kein eigener Grow anlegbar — läuft die App unter GROW_OS_URL?')

    // Abschluss über die Schnittstelle (der Weg „Archivieren"), ohne die Ernte-Seite.
    const archiv = await request.post(`/api/grows/${aufgaben!.growId}/archive`)
    expect(archiv.ok(), `Archivieren: HTTP ${archiv.status()}`).toBe(true)

    await page.goto('/aufgaben', { waitUntil: 'networkidle' })
    const zeile = page.locator('[data-audit=reinigung-nach-grow]').filter({ hasText: 'Abschluss-Aufgaben' })
    await expect(zeile, 'Der abgeschlossene Grow bekommt die Reinigung als fällige Routine.').toBeVisible()
    await expect(zeile).toContainText('nach dem Grow empfohlen')

    await zeile.getByRole('button', { name: 'Starten' }).click()
    await expect(page).toHaveURL(new RegExp(`/sops\\?growId=${aufgaben!.growId}$`))

    // Zweiter Besuch: die Routine läuft — kein zweiter Start, sondern „Öffnen".
    await page.goto('/aufgaben', { waitUntil: 'networkidle' })
    const danach = page.locator('[data-audit=reinigung-nach-grow]').filter({ hasText: 'Abschluss-Aufgaben' })
    await expect(danach).toContainText('läuft')
    await expect(danach.getByRole('button', { name: 'Starten' })).toHaveCount(0)
    await expect(danach.getByRole('link', { name: 'Öffnen' })).toBeVisible()
  })
})
