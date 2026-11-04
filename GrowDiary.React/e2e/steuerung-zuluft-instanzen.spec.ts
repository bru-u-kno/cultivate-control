import { test, expect, type APIRequestContext } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Weitere Zuluft (Etappe 2, Release 7b): anlegen, Seite mit den Reitern der Zuluft, benennen, löschen — gegen das echte
 * Backend mit Demobestand (vier Zelte, kein erreichbares Home Assistant). Aufgeräumt wird in jedem Fall am Ende.
 */
async function alleEntfernen(request: APIRequestContext) {
  const antwort = await request.get('/api/steuerung/instanzen')
  if (!antwort.ok()) return
  const liste = (await antwort.json()) as { instanzen: Array<{ modul: string }> }
  for (const i of liste.instanzen) await request.delete(`/api/steuerung/instanzen/${i.modul}`)
}

// Das Register der Instanzen ist global — die Fälle laufen nacheinander und nehmen das Schloss.
test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ request }) => {
  await nimmSchloss()
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test.afterEach(async ({ request }) => {
  await alleEntfernen(request)
  gibSchloss()
})

test('Zuluft anlegen → Seite mit den Reitern → Umbenennen → Löschen, und die erste bleibt unberührt', async ({ page, request }) => {
  await alleEntfernen(request)
  await page.goto('/steuerung/zuluft-neu', { waitUntil: 'networkidle' })

  await expect(page.getByRole('heading', { name: 'Weitere Zuluft anlegen' })).toBeVisible()
  await expect(page.getByText('Die erste Zuluft bleibt unverändert.')).toBeVisible()
  await page.getByRole('group', { name: 'Zelt' }).getByRole('button', { name: 'Mutterzelt (Testdaten)' }).click()
  const weiter = page.getByRole('button', { name: 'Weiter' })
  await expect(weiter).toBeDisabled()
  await page.getByLabel('Name').fill('Zuluft Reserve')
  // Die Seite der neuen Zuluft fragt mit ihrem Modul ab — sonst stünden die Werte der ersten darunter.
  const abruf = page.waitForRequest((r) => /\/api\/steuerung\/zuluft\?instanz=zuluft-i\d+$/.test(r.url()) && r.method() === 'GET')
  await weiter.click()
  await abruf

  // Nummern werden nie wiederverwendet: frühere Läufe haben schon welche verbraucht.
  await expect(page).toHaveURL(/\/steuerung\/zuluft-i\d+$/)
  const modul = page.url().split('/').pop()!
  await expect(page.getByRole('heading', { name: 'Zuluft Reserve', level: 1 })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Diese Zuluft' })).toBeVisible()
  // Die Reiter der Zuluft, für diese eine Zuluft.
  await expect(page.locator('.v1-tab')).toHaveText(['Regel', 'Lüfter', 'Betrieb'])
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul, art: 'zuluft', zeltId: 2 }])
  // Die Seite fragt mit ihrem Modul ab und nicht die erste Zuluft.
  expect((await request.get(`/api/steuerung/zuluft?instanz=${modul}`)).ok()).toBe(true)

  // Umbenennen.
  await page.getByLabel('Name').fill('Zuluft Keller B')
  await page.getByRole('button', { name: 'Speichern', exact: true }).click()
  await expect(page.getByText('Gespeichert.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Zuluft Keller B', level: 1 })).toBeVisible()
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul, name: 'Zuluft Keller B' }])

  // Löschen: Abbrechen lässt alles stehen, Löschen entfernt den Eintrag.
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await expect(page.getByRole('heading', { name: /Zuluft Keller B löschen\?/ })).toBeVisible()
  await expect(page.getByText('Die Zuluft verschwindet aus Cultivate Control')).toBeVisible()
  await expect(page.getByText('Die erste Zuluft (')).toBeVisible()
  await page.getByRole('button', { name: 'Abbrechen' }).click()
  await expect(page.getByRole('heading', { name: 'Diese Zuluft' })).toBeVisible()
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await expect(page).toHaveURL(/\/steuerung$/)
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toEqual([])
  expect((await request.get('/api/steuerung/zuluft')).ok()).toBe(true)
  expect((await request.delete('/api/steuerung/instanzen/zuluft')).status()).toBe(404)
})

test('Die Übersicht zeigt jede weitere Zuluft mit ihrem Namen und bietet den Weg zum Anlegen an', async ({ page, request }) => {
  await alleEntfernen(request)
  const angelegt = await request.post('/api/steuerung/instanzen', { data: { art: 'zuluft', name: 'Zelt B Zuluft', zeltId: 2 } })
  expect(angelegt.ok()).toBe(true)
  const modul = (await angelegt.json()).instanzen[0].modul as string

  const uebersicht = await (await request.get('/api/steuerung')).json()
  expect(uebersicht.module.find((m: { kennung: string }) => m.kennung === modul)).toMatchObject({ titel: 'Zelt B Zuluft', zeltId: 2 })

  await page.goto('/steuerung', { waitUntil: 'networkidle' })
  await expect(page.getByRole('link', { name: /\+ Weitere Zuluft in / }).first()).toBeVisible()
  await page.getByText('Zelt B Zuluft').first().click()
  await expect(page).toHaveURL(new RegExp(`/steuerung/${modul}$`))

  // Eine nie angelegte Instanz gibt es auch im Backend nicht.
  expect((await request.get('/api/steuerung/zuluft?instanz=zuluft-i99')).status()).toBe(404)
})
