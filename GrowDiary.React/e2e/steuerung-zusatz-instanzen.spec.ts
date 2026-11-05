import { test, expect, type APIRequestContext } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Zusatz-Entfeuchter je Entfeuchter (Etappe 2, Release 9b): auf der Seite des weiteren Entfeuchters anlegen, mit seinen Werten
 * (`?instanz=`) anzeigen, umbenennen, allein löschen; der Entfeuchter nimmt ihn beim Löschen mit — gegen das echte Backend mit
 * Demobestand. Aufgeräumt wird in jedem Fall am Ende.
 */
async function alleEntfernen(request: APIRequestContext) {
  const antwort = await request.get('/api/steuerung/instanzen')
  if (!antwort.ok()) return
  const liste = (await antwort.json()) as { instanzen: Array<{ modul: string; art: string }> }
  // Zusatz zuerst, dann die Entfeuchter (der Entfeuchter nähme ihn sonst mit und der zweite Aufruf ginge ins Leere).
  for (const i of liste.instanzen.filter((x) => x.art === 'entfeuchter-zusatz')) await request.delete(`/api/steuerung/instanzen/${i.modul}`)
  for (const i of liste.instanzen.filter((x) => x.art !== 'entfeuchter-zusatz')) await request.delete(`/api/steuerung/instanzen/${i.modul}`)
}
const instanzen = async (request: APIRequestContext) => (await (await request.get('/api/steuerung/instanzen')).json()).instanzen as Array<{ modul: string; art: string; name: string; nummer: number }>

test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ request }) => {
  await nimmSchloss()
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test.afterEach(async ({ request }) => {
  await alleEntfernen(request)
  gibSchloss()
})

test('Zusatz anlegen → steht auf der Seite seines Entfeuchters → Umbenennen → allein Löschen', async ({ page, request }) => {
  await alleEntfernen(request)
  const angelegt = await request.post('/api/steuerung/instanzen', { data: { art: 'entfeuchter', name: 'Entfeuchter Zelt B', zeltId: 2 } })
  const haupt = (await angelegt.json()).instanzen[0].modul as string
  const nummer = haupt.split('-i')[1]
  const zusatzModul = `entfeuchter-zusatz-i${nummer}`

  await page.goto(`/steuerung/${haupt}`, { waitUntil: 'networkidle' })
  await expect(page.getByRole('heading', { name: 'Zusatz-Entfeuchter', level: 2 })).toBeVisible()
  await page.getByRole('link', { name: '+ Zusatz-Entfeuchter dazu' }).click()

  await expect(page).toHaveURL(new RegExp(`/steuerung/entfeuchter-zusatz-neu\\?haupt=${haupt}$`))
  await expect(page.getByRole('heading', { name: 'Zusatz-Entfeuchter anlegen' })).toBeVisible()
  // Das Zelt ist das des Entfeuchters: keine Zelt-Wahl.
  await expect(page.getByRole('group', { name: 'Zelt' })).toHaveCount(0)
  const weiter = page.getByRole('button', { name: 'Weiter' })
  await expect(weiter).toBeDisabled()
  await page.getByLabel('Name').fill('Zusatz Zelt B')
  // Die Seite fragt mit dem Modul des Zusatzes ab — sonst stünden die Werte des ersten Zusatzes darunter.
  const abruf = page.waitForRequest((r) => r.url().endsWith(`/api/steuerung/entfeuchter-zusatz?instanz=${zusatzModul}`) && r.method() === 'GET')
  await weiter.click()

  await expect(page).toHaveURL(new RegExp(`/steuerung/${haupt}$`))
  await abruf
  expect(await instanzen(request)).toMatchObject([{ modul: haupt, art: 'entfeuchter' }, { modul: zusatzModul, art: 'entfeuchter-zusatz', name: 'Zusatz Zelt B' }])
  await expect(page.getByRole('heading', { name: 'Dieser Zusatz-Entfeuchter' })).toBeVisible()
  await expect(page.getByRole('link', { name: '+ Zusatz-Entfeuchter dazu' })).toHaveCount(0)
  await expect(page.getByText('Entfeuchter Zelt B und Zusatz Zelt B arbeiten zusammen')).toBeVisible()
  // Ein Zusatz hat keine eigene Seite: seine Adresse führt auf die des Entfeuchters.
  await page.goto(`/steuerung/${zusatzModul}`, { waitUntil: 'networkidle' })
  await expect(page).toHaveURL(new RegExp(`/steuerung/${haupt}$`))
  // Je Entfeuchter höchstens ein Zusatz.
  expect((await request.post('/api/steuerung/instanzen', { data: { art: 'entfeuchter-zusatz', name: 'Noch einer', hauptModul: haupt } })).ok()).toBe(false)
  expect((await request.get(`/api/steuerung/entfeuchter-zusatz?instanz=${zusatzModul}`)).ok()).toBe(true)

  // Umbenennen: der Block des Zusatzes (der erste „Name“ auf der Seite) ändert nur ihn.
  const block = page.locator('section', { has: page.getByRole('heading', { name: 'Dieser Zusatz-Entfeuchter' }) })
  await block.getByLabel('Name').fill('Zusatz Keller')
  await block.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect(page.getByText('Gespeichert.').first()).toBeVisible()
  expect(await instanzen(request)).toMatchObject([{ modul: haupt, name: 'Entfeuchter Zelt B' }, { modul: zusatzModul, name: 'Zusatz Keller' }])

  // Allein löschen: der Entfeuchter bleibt, die Rückfrage nennt ihn.
  await block.getByRole('button', { name: 'Löschen …' }).click()
  await expect(page.getByRole('heading', { name: /Zusatz Keller löschen\?/ })).toBeVisible()
  await expect(page.getByText('Der Entfeuchter „Entfeuchter Zelt B“ bleibt unverändert.')).toBeVisible()
  await expect(page.getByText('Auch aus Home Assistant entfernen')).toBeVisible()
  await page.getByRole('button', { name: 'Abbrechen' }).click()
  await block.getByRole('button', { name: 'Löschen …' }).click()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Entfeuchter Zelt B', level: 1 })).toBeVisible()
  await expect(page.getByRole('link', { name: '+ Zusatz-Entfeuchter dazu' })).toBeVisible()
  expect(await instanzen(request)).toMatchObject([{ modul: haupt }])
  expect(await instanzen(request)).toHaveLength(1)
})

test('Den Entfeuchter löschen nimmt seinen Zusatz mit, und die Rückfrage nennt ihn', async ({ page, request }) => {
  await alleEntfernen(request)
  const haupt = (await (await request.post('/api/steuerung/instanzen', { data: { art: 'entfeuchter', name: 'Entfeuchter Zelt B', zeltId: 2 } })).json()).instanzen[0].modul as string
  const zusatz = await request.post('/api/steuerung/instanzen', { data: { art: 'entfeuchter-zusatz', name: 'Zusatz Zelt B', hauptModul: haupt } })
  expect(zusatz.ok()).toBe(true)

  await page.goto(`/steuerung/${haupt}`, { waitUntil: 'networkidle' })
  await page.getByRole('heading', { name: 'Dieser Entfeuchter' }).scrollIntoViewIfNeeded()
  const block = page.locator('section', { has: page.getByRole('heading', { name: 'Dieser Entfeuchter' }) })
  await block.getByRole('button', { name: 'Löschen …' }).click()
  await expect(page.getByText('Zusatz Zelt B', { exact: false }).first()).toBeVisible()
  await expect(page.getByText('gehört zu diesem Entfeuchter und wird mitgelöscht')).toBeVisible()
  await page.getByRole('button', { name: 'Beide löschen' }).click()
  await expect(page).toHaveURL(/\/steuerung$/)
  expect(await instanzen(request)).toEqual([])
})
