import { test, expect, type APIRequestContext } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Weitere Entfeuchter (Etappe 2, Release 6c): anlegen, Seite mit den Reitern der Entfeuchtung, benennen, löschen — gegen das echte
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

test('Entfeuchter anlegen → Seite mit den Reitern → Umbenennen → Löschen, und der erste bleibt unberührt', async ({ page, request }) => {
  await alleEntfernen(request)
  await page.goto('/steuerung/entfeuchter-neu', { waitUntil: 'networkidle' })

  await expect(page.getByRole('heading', { name: 'Weiteren Entfeuchter anlegen' })).toBeVisible()
  await expect(page.getByText('Er richtet sich nach dem Plan und dem CO₂ seines Zeltes.')).toBeVisible()
  const zeltWahl = page.getByRole('group', { name: 'Zelt' })
  await zeltWahl.getByRole('button', { name: 'Mutterzelt (Testdaten)' }).click()
  const weiter = page.getByRole('button', { name: 'Weiter' })
  await expect(weiter).toBeDisabled()
  await page.getByLabel('Name').fill('Entfeuchter Reserve')
  await weiter.click()

  // Nummern werden nie wiederverwendet: frühere Läufe haben schon welche verbraucht.
  await expect(page).toHaveURL(/\/steuerung\/entfeuchter-i\d+$/)
  const modul = page.url().split('/').pop()!
  await expect(page.getByRole('heading', { name: 'Entfeuchter Reserve', level: 1 })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Dieser Entfeuchter' })).toBeVisible()
  // Die Reiter der Entfeuchtung, für diesen einen Entfeuchter — noch ohne Zusatz-Entfeuchter, mit dem Weg, einen anzulegen.
  await expect(page.locator('.v1-tab')).toHaveText(['Überblick', 'Einstellungen', 'Einrichtung'])
  await expect(page.getByRole('link', { name: '+ Zusatz-Entfeuchter dazu' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Dieser Zusatz-Entfeuchter' })).toHaveCount(0)
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul, art: 'entfeuchter', zeltId: 2 }])

  // Die Einrichtung führt zu den Rollen dieses Entfeuchters.
  await page.locator('.v1-tab', { hasText: /^Einrichtung$/ }).click()
  await expect(page.getByRole('link', { name: /Geräte & Entitäten|Rollen/ }).first()).toBeVisible()

  // Umbenennen.
  const speichern = page.getByRole('button', { name: 'Speichern', exact: true })
  await page.getByLabel('Name').fill('Entfeuchter links')
  await speichern.click()
  await expect(page.getByText('Gespeichert.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Entfeuchter links', level: 1 })).toBeVisible()
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul, name: 'Entfeuchter links' }])
  const geraete = await (await request.get('/api/steuerung/geraete')).json()
  expect(geraete.module.find((m: { modul: string }) => m.modul === modul)).toBeTruthy()

  // Löschen: Abbrechen lässt alles stehen, Löschen entfernt den Eintrag.
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await expect(page.getByRole('heading', { name: /Entfeuchter links löschen\?/ })).toBeVisible()
  await expect(page.getByText('Den ersten Entfeuchter (')).toBeVisible()
  await page.getByRole('button', { name: 'Abbrechen' }).click()
  await expect(page.getByRole('heading', { name: 'Dieser Entfeuchter' })).toBeVisible()
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await expect(page).toHaveURL(/\/steuerung$/)
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toEqual([])
  expect((await request.get('/api/steuerung/entfeuchter')).ok()).toBe(true)
  expect((await request.delete('/api/steuerung/instanzen/entfeuchter')).status()).toBe(404)
})

test('Die Übersicht zeigt jeden weiteren Entfeuchter mit seinem Namen und bietet den Weg zum Anlegen an', async ({ page, request }) => {
  await alleEntfernen(request)
  const angelegt = await request.post('/api/steuerung/instanzen', { data: { art: 'entfeuchter', name: 'Zelt B Entfeuchter', zeltId: 2 } })
  expect(angelegt.ok()).toBe(true)
  const modul = (await angelegt.json()).instanzen[0].modul as string

  const uebersicht = await (await request.get('/api/steuerung')).json()
  expect(uebersicht.module.find((m: { kennung: string }) => m.kennung === modul)).toMatchObject({ titel: 'Zelt B Entfeuchter', zeltId: 2 })

  await page.goto('/steuerung', { waitUntil: 'networkidle' })
  await expect(page.getByRole('link', { name: /\+ Weiteren Entfeuchter in / }).first()).toBeVisible()
  await page.getByText('Zelt B Entfeuchter').first().click()
  await expect(page).toHaveURL(new RegExp(`/steuerung/${modul}$`))

  // Eine nie angelegte Instanz gibt eine Fehlerseite statt fremder Zahlen.
  await page.goto('/steuerung/entfeuchter-i9', { waitUntil: 'networkidle' })
  await expect(page.getByText('Diese Instanz gibt es nicht.')).toBeVisible()
})
