import { expect, test } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'

/**
 * Live-Seite mit mehreren Zelten (Demobestand: vier Zelte).
 *
 * Jedes Zelt hat einen Abschnitt: Kopf mit Pfeil links, Score-Ring, Name, Zustandswort, Phase und
 * (zugeklappt) die Kernwerte. Der Score steht nur im Kopf, aufgeklappt gibt es keinen zweiten Ring.
 * Welche Zelte offen sind, merkt sich das Gerät.
 */
test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — die Seite braucht die laufende App.')
})

const kopfVon = (zeltId: number) => `[data-audit="live-zelt"][data-zelt-id="${zeltId}"] [data-audit="zelt-kopf"]`

test('ohne Merkzettel ist nur das Zelt mit dem laufenden Grow offen, die übrigen zeigen ihre Kernwerte', async ({ page }) => {
  await page.goto('/')
  await expect(page.locator('[data-audit="live-zelt"]')).toHaveCount(4)
  const offen = page.locator('[data-audit="zelt-kopf"][aria-expanded="true"]')
  await expect(offen).toHaveCount(1)
  // Zugeklappte Zelte tragen ihre Werte im Kopf; das offene zeigt sie stattdessen in den Kacheln darunter.
  const zu = page.locator('[data-audit="zelt-kopf"][aria-expanded="false"]').first()
  await expect(zu.locator('[data-kopfwert="temperature"]')).toBeVisible()
  await expect(offen.locator('[data-kopfwert]')).toHaveCount(0)
})

test('der Score steht einmal je Zelt: im Kopf, nicht noch einmal im aufgeklappten Inhalt', async ({ page }) => {
  await page.goto('/')
  await expect(page.locator('[data-audit="zelt-score"]')).toHaveCount(4)
  const inhalt = page.locator('[data-audit="live-zelt-inhalt"]').first()
  await expect(inhalt).toBeVisible()
  await expect(inhalt.locator('.ls-ring')).toHaveCount(0)
  await expect(inhalt.locator('.ls-score-label')).toHaveCount(0)
})

test('der Pfeil steht links vor dem Namen', async ({ page }) => {
  await page.goto('/')
  const kopf = page.locator('[data-audit="zelt-kopf"]').first()
  const [pfeil, name] = await Promise.all([kopf.locator('.ls-fold-pfeil').boundingBox(), kopf.locator('strong').boundingBox()])
  expect(pfeil!.x).toBeLessThan(name!.x)
})

test('auf- und zuklappen, zweimal — und das Gerät merkt es nach dem Neuladen', async ({ page }) => {
  await page.goto('/')
  const zweites = page.locator('[data-audit="zelt-kopf"]').nth(1)
  await expect(zweites).toHaveAttribute('aria-expanded', 'false')
  for (let i = 0; i < 2; i++) {
    await zweites.click()
    await expect(zweites).toHaveAttribute('aria-expanded', 'true')
    await expect(zweites.locator('.ls-fold-pfeil')).toHaveText('▾')
    await expect(page.locator('[data-audit="live-zelt-inhalt"]')).toHaveCount(2)
    await zweites.click()
    await expect(zweites).toHaveAttribute('aria-expanded', 'false')
    await expect(zweites.locator('.ls-fold-pfeil')).toHaveText('▸')
  }
  // Gemerkt: zweites Zelt auf, erstes (Standard) zu — nach dem Neuladen genau so.
  await zweites.click()
  await page.locator('[data-audit="zelt-kopf"]').first().click()
  await page.reload()
  await expect(page.locator('[data-audit="zelt-kopf"]').nth(1)).toHaveAttribute('aria-expanded', 'true')
  await expect(page.locator('[data-audit="zelt-kopf"]').first()).toHaveAttribute('aria-expanded', 'false')
})

test('Kopf und Ring nennen denselben Zustand; bei 369 px ragt nichts über den Rand', async ({ page }) => {
  await page.setViewportSize({ width: 369, height: 900 })
  await page.goto('/')
  const kopf = page.locator(kopfVon(1))
  await expect(kopf.locator('[data-audit="zelt-zustand"]')).toHaveText(/Stabil|Beobachten|Kritisch|Nicht bewertet|Einrichten/)
  const breite = await page.evaluate(() => ({ dok: document.documentElement.scrollWidth, fenster: window.innerWidth }))
  expect(breite.dok).toBeLessThanOrEqual(breite.fenster)
  // Der Zeltname und das Zustandswort liegen nebeneinander und überlappen nicht.
  const [name, chip] = await Promise.all([kopf.locator('strong').boundingBox(), kopf.locator('[data-audit="zelt-zustand"]').boundingBox()])
  expect(name!.x + name!.width).toBeLessThanOrEqual(chip!.x + 1)
})

test('zwei offene Zelte teilen den Einklapp-Zustand — keines überschreibt das andere', async ({ page }) => {
  await page.goto('/')
  await page.locator('[data-audit="zelt-kopf"]').nth(1).click()
  const inhalte = page.locator('[data-audit="live-zelt-inhalt"]')
  await expect(inhalte).toHaveCount(2)
  // In Zelt 1 die Kamera zuklappen, danach in Zelt 2 die Aufgaben — zwei getrennte Speichervorgänge.
  await expect(inhalte.nth(0).locator('.ls-cam')).toBeVisible()
  await inhalte.nth(0).locator('.ls-cam .ls-fold-titel').click()
  await inhalte.nth(1).locator('[data-audit="live-tasks"] .ls-fold-titel').click()
  // Beides gilt in BEIDEN Zelten sofort …
  await expect(inhalte.nth(1).locator('.ls-cam.is-zu')).toBeVisible()
  await expect(inhalte.nth(0).locator('[data-audit="live-tasks"] .ls-fold-titel')).toHaveAttribute('aria-expanded', 'false')
  // … und nach dem Neuladen noch immer: das zweite Speichern hat das erste nicht überschrieben.
  await page.reload()
  const nachher = page.locator('[data-audit="live-zelt-inhalt"]')
  await expect(nachher.first().locator('.ls-cam .ls-fold-titel')).toHaveAttribute('aria-expanded', 'false')
  await expect(nachher.first().locator('[data-audit="live-tasks"] .ls-fold-titel')).toHaveAttribute('aria-expanded', 'false')
})

test('Anpassen sperrt den Zeltkopf: zuklappen geht erst nach „Fertig“, zweimal', async ({ page }) => {
  await page.goto('/')
  const abschnitt = page.locator('[data-audit="live-zelt"]').first()
  const kopf = abschnitt.locator('[data-audit="zelt-kopf"]')
  await expect(kopf).toHaveAttribute('aria-expanded', 'true')
  for (let i = 0; i < 2; i++) {
    await abschnitt.locator('[data-audit="live-more"]').click()
    await page.locator('[data-audit="dashboard-customise"]').click()
    // Gesperrt: Schloss statt Pfeil, Hinweis statt Phase, Tippen ändert nichts.
    await expect(kopf).toHaveAttribute('aria-disabled', 'true')
    await expect(kopf).toHaveAttribute('aria-label', /gesperrt/)
    await expect(abschnitt.locator('[data-audit="zelt-gesperrt"]')).toContainText('Anpassen')
    await expect(kopf.locator('.ls-fold-pfeil svg')).toBeVisible()
    // Erzwungen: Playwright hält ein aria-disabled-Element für nicht anklickbar, ein Finger tippt trotzdem.
    await kopf.click({ force: true })
    await expect(kopf).toHaveAttribute('aria-expanded', 'true')
    await expect(abschnitt.getByRole('button', { name: 'Fertig' })).toBeVisible()
    // „Fertig“ gibt den Kopf wieder frei und verwirft den Entwurf. „Speichern“ und „Zurücksetzen“ schreiben in den
    // gemeinsamen Demobestand (Anordnung des Zelts) und würden andere Prüfungen stören — hier bewusst nicht benutzt.
    await abschnitt.getByRole('button', { name: 'Fertig' }).click()
    await expect(kopf).not.toHaveAttribute('aria-disabled', 'true')
    await expect(abschnitt.locator('[data-audit="zelt-gesperrt"]')).toHaveCount(0)
  }
  await kopf.click()
  await expect(kopf).toHaveAttribute('aria-expanded', 'false')
})

test('Screenreader: der Zeltname ist eine Überschrift, der Knopfname bleibt kurz, die Kernwerte kommen als Beschreibung', async ({ page }) => {
  await page.goto('/')
  const zu = page.locator('[data-audit="live-zelt"]').nth(1)
  await expect(zu.getByRole('heading', { level: 2 })).toHaveCount(1)
  const kopf = zu.locator('[data-audit="zelt-kopf"]')
  await expect(kopf).toHaveAttribute('aria-expanded', 'false')
  // Name: „Zeltname, Score N, Zustand“ — ohne Phase und Messwerte.
  const name = await kopf.getAttribute('aria-label')
  expect(name).toMatch(/^.+, (Score \d+, )?(Stabil|Beobachten|Kritisch|Nicht bewertet|Einrichten)$/)
  expect(name).not.toMatch(/°C|kPa|RLF/)
  // Beschreibung: die Kernwerte des zugeklappten Kopfes.
  const beschreibungId = await kopf.getAttribute('aria-describedby')
  expect(beschreibungId).toBeTruthy()
  const text = await page.locator(`[id="${beschreibungId}"]`).textContent()
  expect(text).toMatch(/Luft [\d,.]+/)
})
