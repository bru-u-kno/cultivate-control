import { test, expect, type Page } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * cc.296: Lädt Home Assistant den iframe neu (Handy-App nach ein paar Minuten), macht die App auf der zuletzt geöffneten Seite
 * weiter statt auf Live. Nachgestellt wie in Home Assistant: eine Seite gleicher Herkunft mit der App im iframe; „Neuladen“
 * heißt, diese Hülle neu zu öffnen. Nur im iframe — im eigenen Tab bleibt `goto('/')` auf Live (sonst kippten alle Fälle,
 * die die Startseite ansteuern). Ändert nichts an der App, nur am Speicher des Browsers.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — der Rahmen braucht die laufende App.')
})

async function huelle(page: Page) {
  await page.route('**/e2e-ha-huelle.html*', (r) => r.fulfill({ contentType: 'text/html', body: '<iframe src="/" style="width:390px;height:800px"></iframe>' }))
}
const rahmenPfad = (page: Page) => new URL(page.frames().find((f) => f !== page.mainFrame())!.url()).pathname

/** Wie ein Tipp im Menü: den echten Link der App auslösen (der Router bekommt den Wechsel mit). */
async function linkOeffnen(page: Page, pfad: string) {
  const f = page.frames().find((x) => x !== page.mainFrame())!
  await f.evaluate((p) => { (document.querySelector(`nav a[href="${p}"], a[href="${p}"]`) as HTMLElement).click() }, pfad)
  await expect.poll(() => rahmenPfad(page)).toBe(pfad)
  // Gemerkt wird nach dem Zeichnen; ein Mensch lädt nicht im selben Atemzug neu.
  await expect.poll(() => f.evaluate(() => JSON.parse(localStorage.getItem('growos.letzteSeite') ?? '{}').pfad)).toBe(pfad)
}

async function imRahmenOeffnen(page: Page, pfad: string) {
  await page.goto('/e2e-ha-huelle.html' + pfad, { waitUntil: 'networkidle' })
  await expect.poll(() => page.frames().length).toBeGreaterThan(1)
  await page.frames().find((f) => f !== page.mainFrame())!.waitForLoadState('networkidle')
}

test('Neuladen im iframe: die zuletzt geöffnete Seite kommt wieder, Live bleibt Live, nach 30 Minuten gilt sie nicht mehr', async ({ page }) => {
  await huelle(page)

  // Unterseite öffnen, dann „Neuladen“ (Hülle neu öffnen) — und ein zweites Mal mit anderer Seite.
  await page.goto('/e2e-ha-huelle.html', { waitUntil: 'networkidle' })
  await expect.poll(() => page.frames().length).toBeGreaterThan(1)
  for (const seite of ['/tagebuch', '/aufgaben']) {
    await linkOeffnen(page, seite)
    await imRahmenOeffnen(page, '')
    await expect.poll(() => rahmenPfad(page), `Nach dem Neuladen auf ${seite}`).toBe(seite)
  }

  // Wer bewusst auf Live ging, bleibt dort.
  await linkOeffnen(page, '/')
  await imRahmenOeffnen(page, '')
  await expect.poll(() => rahmenPfad(page)).toBe('/')

  // Länger als 30 Minuten her: wieder Live.
  await linkOeffnen(page, '/tagebuch')
  await page.evaluate(() => {
    const g = JSON.parse(localStorage.getItem('growos.letzteSeite')!)
    g.zeit -= 31 * 60 * 1000
    localStorage.setItem('growos.letzteSeite', JSON.stringify(g))
  })
  await imRahmenOeffnen(page, '')
  await expect.poll(() => rahmenPfad(page)).toBe('/')
})

test('Ein Ziel aus Home Assistant (?index=) hat Vorrang vor der gemerkten Seite', async ({ page }) => {
  await huelle(page)
  await page.goto('/tagebuch', { waitUntil: 'networkidle' })
  await page.goto('/e2e-ha-huelle.html?index=aufgaben', { waitUntil: 'networkidle' })
  await expect.poll(() => rahmenPfad(page)).toBe('/aufgaben')
})

test('Im eigenen Tab bleibt die Startseite die Startseite', async ({ page }) => {
  await page.goto('/tagebuch', { waitUntil: 'networkidle' })
  await page.goto('/', { waitUntil: 'networkidle' })
  expect(new URL(page.url()).pathname).toBe('/')
})
