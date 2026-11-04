import { test, expect, type APIRequestContext } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Weitere Wasserkühler (Etappe 2, Release 5b): anlegen, benennen, wieder löschen — gegen das echte Backend mit Demobestand.
 * Home Assistant ist nicht verbunden; die Seite des neuen
 * Wasserkühlers muss trotzdem laden. Aufgeräumt wird in jedem Fall am Ende (über die Schnittstelle).
 * Der Demobestand hat vier Zelte: die Zelt-Wahl erscheint und wird mitgespeichert. Mit einem Zelt (nachgestellte Zuweisung)
 * kommt keine Zelt-Frage dazu.
 */
async function alleEntfernen(request: APIRequestContext) {
  const antwort = await request.get('/api/steuerung/instanzen')
  if (!antwort.ok()) return
  const liste = (await antwort.json()) as { instanzen: Array<{ modul: string }> }
  for (const i of liste.instanzen) await request.delete(`/api/steuerung/instanzen/${i.modul}`)
}

// Alle Fälle teilen sich das eine Register der App — nacheinander, sonst räumt einer dem anderen weg.
test.describe.configure({ mode: 'serial' })

// Das Register der Instanzen und die Zuweisung gehören der ganzen App: wer sie beschreibt, nimmt das Schloss.
test.beforeEach(async ({ request }) => {
  await nimmSchloss()
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend — die Prüfung braucht die laufende App mit Demobestand.')
})

test.afterEach(async ({ request }) => {
  await alleEntfernen(request)
  gibSchloss()
})

test('Anlegen → Seite des neuen Wasserkühlers → Umbenennen → Löschen, und der erste bleibt unberührt', async ({ page, request }) => {
  await alleEntfernen(request)
  await page.goto('/steuerung/chiller-neu', { waitUntil: 'networkidle' })

  await expect(page.getByRole('heading', { name: 'Weiteren Wasserkühler anlegen' })).toBeVisible()
  const zeltWahl = page.getByRole('group', { name: 'Zelt' })
  await expect(zeltWahl.getByRole('button')).toHaveCount(4)
  await zeltWahl.getByRole('button', { name: 'Mutterzelt (Testdaten)' }).click()
  const weiter = page.getByRole('button', { name: 'Weiter' })
  await expect(weiter).toBeDisabled() // ohne Namen geht es nicht weiter
  await page.getByLabel('Name').fill('Wasserkühler Reserve')
  await weiter.click()

  // Die Seite des neuen Wasserkühlers: eigener Titel, eigener Block, Chip in der Leiste.
  // Nummern werden nie wiederverwendet: frühere Läufe haben schon welche verbraucht.
  await expect(page).toHaveURL(/\/steuerung\/chiller-i\d+$/)
  const modul = page.url().split('/').pop()!
  await expect(page.getByRole('heading', { name: 'Wasserkühler Reserve', level: 1 })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Dieser Wasserkühler' })).toBeVisible()
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul, zeltId: 2 }])

  // Umbenennen: Titel und gespeicherter Name ändern sich, die Nummer nicht.
  const speichern = page.getByRole('button', { name: 'Speichern', exact: true })
  await expect(speichern).toBeDisabled()
  await page.getByLabel('Name').fill('Kühler links')
  // Das Zelt lässt sich hier ändern; der Chip des gespeicherten Zeltes ist gewählt.
  const hier = page.getByRole('group', { name: 'Zelt' })
  await expect(hier.getByRole('button', { name: 'Mutterzelt (Testdaten)' })).toHaveAttribute('aria-pressed', 'true')
  await hier.getByRole('button', { name: 'Quarantänezelt (Testdaten)' }).click()
  await speichern.click()
  await expect(page.getByText('Gespeichert.')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Kühler links', level: 1 })).toBeVisible()
  const nachUmbenennen = (await (await request.get('/api/steuerung/instanzen')).json()).instanzen
  expect(nachUmbenennen).toMatchObject([{ modul, name: 'Kühler links', zeltId: 3 }])
  // Die erste Instanz behält ihr Zelt.
  const zuweisung = await (await request.get('/api/steuerung/zuweisung')).json()
  expect(zuweisung.eintraege.find((e: { kennung: string }) => e.kennung === 'chiller')?.zeltId).not.toBe(3)

  // Die Rollen-Seite kennt den neuen Wasserkühler unter seinem Namen.
  const geraete = await (await request.get('/api/steuerung/geraete')).json()
  expect(geraete.module.find((m: { modul: string }) => m.modul === modul)?.titel).toBe('Wasserkühler · Kühler links')

  // Löschen: erst die Rückfrage, Abbrechen lässt alles stehen, Löschen entfernt den Eintrag.
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await expect(page.getByRole('heading', { name: /Kühler links löschen\?/ })).toBeVisible()
  await page.getByRole('button', { name: 'Abbrechen' }).click()
  await expect(page.getByRole('heading', { name: 'Dieser Wasserkühler' })).toBeVisible()
  await page.getByRole('button', { name: 'Löschen …' }).click()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await expect(page).toHaveURL(/\/steuerung$/)
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toEqual([])
  // Der erste Wasserkühler ist unberührt und nicht löschbar.
  expect((await request.get('/api/steuerung/chiller')).ok()).toBe(true)
  expect((await request.delete('/api/steuerung/instanzen/chiller')).status()).toBe(404)
})

test('Der erste Wasserkühler bietet den Weg zum Anlegen an, eine gelöschte Instanz gibt eine Fehlerseite statt fremder Zahlen', async ({ page, request }) => {
  await alleEntfernen(request)
  await page.goto('/steuerung/chiller', { waitUntil: 'networkidle' })
  await page.getByRole('link', { name: '+ Weiteren Wasserkühler anlegen' }).click()
  await expect(page).toHaveURL(/\/steuerung\/chiller-neu$/)

  await page.goto('/steuerung/chiller-i7', { waitUntil: 'networkidle' })
  await expect(page.getByText('Diese Instanz gibt es nicht.')).toBeVisible()
})

test('Mit einem Zelt kommt keine Zelt-Frage dazu', async ({ page, request }) => {
  await alleEntfernen(request)
  await page.route(/\/api\/steuerung\/zuweisung$/, (route) => route.fulfill({ json: { zelte: [{ id: 1, name: 'Einziges Zelt' }], eintraege: [] } }))
  await page.goto('/steuerung/chiller-neu', { waitUntil: 'networkidle' })
  await expect(page.getByRole('heading', { name: 'Weiteren Wasserkühler anlegen' })).toBeVisible()
  await expect(page.getByRole('group', { name: 'Zelt' })).toHaveCount(0)
  let gesendet: { zeltId: number | null } | null = null
  page.on('request', (r) => { if (r.method() === 'POST' && r.url().endsWith('/api/steuerung/instanzen')) gesendet = r.postDataJSON() })
  await page.getByLabel('Name').fill('Ein zweiter')
  await page.getByRole('button', { name: 'Weiter' }).click()
  await expect(page).toHaveURL(/\/steuerung\/chiller-i\d+$/)
  // Die Anfrage nennt kein Zelt (das Backend hat vier; die Antwort zeigt sein Standardzelt).
  expect(gesendet).toMatchObject({ art: 'chiller', name: 'Ein zweiter', zeltId: null })
})

test('Löschen mit „Auch aus Home Assistant entfernen“: wenn Home Assistant nicht antwortet, bleibt die Instanz stehen und sagt warum', async ({ page, request }) => {
  await alleEntfernen(request)
  const angelegt = await request.post('/api/steuerung/instanzen', { data: { art: 'chiller', name: 'Zum Löschen', zeltId: null } })
  expect(angelegt.ok()).toBe(true)
  const modul = (await angelegt.json()).instanzen[0].modul as string

  await page.goto(`/steuerung/${modul}`, { waitUntil: 'networkidle' })
  await page.getByRole('button', { name: 'Löschen …' }).click()
  // Ohne den Haken bleibt der Text beim Alten: die Regelung in Home Assistant läuft weiter.
  await expect(page.getByText('läuft weiter, bis du sie dort entfernst')).toBeVisible()
  await page.getByRole('checkbox', { name: /Auch aus Home Assistant entfernen/ }).check()
  await expect(page.getByText('werden auch aus Home Assistant entfernt')).toBeVisible()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()

  // Der Demobestand hat kein erreichbares Home Assistant: nichts wird entfernt, die Instanz bleibt, die Meldung nennt den Grund.
  await expect(page.getByText(/Nicht alles ließ sich aus Home Assistant entfernen/)).toBeVisible()
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toMatchObject([{ modul }])

  // Wiederholen ohne Haken entfernt sie aus Cultivate Control.
  await page.getByRole('checkbox', { name: /Auch aus Home Assistant entfernen/ }).uncheck()
  await page.getByRole('button', { name: 'Löschen', exact: true }).click()
  await expect(page).toHaveURL(/\/steuerung$/)
  expect((await (await request.get('/api/steuerung/instanzen')).json()).instanzen).toEqual([])
})
