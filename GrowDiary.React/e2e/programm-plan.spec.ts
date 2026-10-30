import { test, expect } from '@playwright/test'
import { darfUeberspringen } from './pflicht'

/**
 * Programme ohne Grow auf der Seite „Plan“ (A-029, Etappe 1).
 *
 * <b>Der Anlass (30.10.2026).</b> Zwischen zwei Grows stand auf der Seite „Plan“
 * nur „Kein laufender Grow“. SKX, Athena und eigene Programme waren nirgends
 * änderbar — Bearbeiten ging nur über den Plan eines laufenden Grows.
 *
 * <b>Was gilt.</b> Eine mitgelieferte Vorlage bleibt unverändert; Speichern legt
 * ein eigenes Programm an. Ein eigenes wird an Ort und Stelle geändert. Der Fall
 * speichert zweimal und lädt neu, weil erst das zweite Speichern zeigt, ob der
 * Wechsel auf die Kopie hält (CLAUDE.md, „Die Reparatur einmal WIEDERHOLEN“).
 */

type Kurz = { id: string; name: string; eigen: boolean; wochen: number }
type Werte = { programmId: string; spalten: Array<{ id: string; felder: Array<{ feld: string; wert: number | null }> }> }

const luft = (w: Werte) => w.spalten[0].felder.find((f) => f.feld === 'airTempC')?.wert ?? null

async function programme(page: import('@playwright/test').Page): Promise<Kurz[]> {
  const antwort = await page.request.get('/api/programme').catch(() => null)
  return antwort?.ok() ? ((await antwort.json()) as Kurz[]) : []
}

test('Plan: die Auswahl nennt die Programme, eine Vorlage öffnet sich mit Hinweis', async ({ page }) => {
  const liste = await programme(page)
  const vorlage = liste.find((p) => !p.eigen && p.wochen > 0)
  darfUeberspringen(!vorlage, 'Kein mitgeliefertes Programm mit Wochenwerten — der Demobestand liefert SKX und Athena.')

  await page.goto(`/plan?programm=${vorlage!.id}`, { waitUntil: 'networkidle' })

  const auswahl = page.getByRole('tablist', { name: 'Plan von' })
  await expect(auswahl).toContainText('Laufender Grow')
  await expect(auswahl.locator('.v1-tab.active')).toContainText(vorlage!.name, { ignoreCase: true })
  await expect(page.getByText('Mitgelieferte Vorlage')).toBeVisible()
  await expect(page.locator('[data-audit=programm-speichern]')).toBeDisabled() // nichts geändert
})

test('Plan: Vorlage ändern legt eine Kopie an, ein zweites Speichern ändert die Kopie, die Vorlage bleibt', async ({ page }) => {
  const liste = await programme(page)
  const vorlage = liste.find((p) => !p.eigen && p.wochen > 0)
  darfUeberspringen(!vorlage, 'Kein mitgeliefertes Programm mit Wochenwerten — der Demobestand liefert SKX und Athena.')
  const vorher = luft((await (await page.request.get(`/api/programme/${vorlage!.id}/werte`)).json()) as Werte)
  const name = `E2E Kopie ${Date.now()}`

  await page.goto(`/plan?programm=${vorlage!.id}`, { waitUntil: 'networkidle' })
  const feld = page.locator('[data-audit=plan-airTempC]').first()
  await feld.fill('27,5')
  await expect(page.locator('.wp-speicherbalken-zahl')).toHaveText('1 Änderung')
  await page.locator('#programm-name-feld').fill(name)
  await page.locator('[data-audit=programm-speichern]').click()

  // Die Seite wechselt auf die Kopie und sagt, was geschah.
  await expect(page).toHaveURL(/programm=eigen-/)
  await expect(page.getByText(`als neues eigenes Programm „${name}“`)).toBeVisible()
  await expect(page.locator('[data-audit=plan-airTempC]').first()).toHaveValue('27,5')
  const kopieId = new URL(page.url()).searchParams.get('programm')!

  // Zweites Speichern: jetzt am eigenen Programm, ohne Namensfeld, ohne weitere Kopie.
  await page.locator('[data-audit=plan-airTempC]').first().fill('26')
  await expect(page.locator('#programm-name-feld')).toHaveCount(0)
  await page.locator('[data-audit=programm-speichern]').click()
  await expect(page.getByText(`1 Änderung gespeichert in „${name}“`)).toBeVisible()
  expect((await programme(page)).filter((p) => p.name === name)).toHaveLength(1)

  // Nach dem Neuladen steht der Wert da, und die Vorlage ist unberührt.
  await page.reload({ waitUntil: 'networkidle' })
  await expect(page.locator('[data-audit=plan-airTempC]').first()).toHaveValue('26')
  expect(luft((await (await page.request.get(`/api/programme/${kopieId}/werte`)).json()) as Werte)).toBe(26)
  expect(luft((await (await page.request.get(`/api/programme/${vorlage!.id}/werte`)).json()) as Werte)).toBe(vorher)
})

test('Plan: beim Anlegen einer Kopie blitzt keine Fehlermeldung auf, und „Zurück“ trägt die Meldung nicht zu einem fremden Programm', async ({ page }) => {
  const liste = await programme(page)
  const vorlagen = liste.filter((p) => !p.eigen && p.wochen > 0)
  darfUeberspringen(vorlagen.length < 2, 'Weniger als zwei mitgelieferte Programme mit Wochenwerten — SKX und Athena sollten im Demobestand sein.')
  const [erste, zweite] = vorlagen

  // Jeder Zustand der Seite wird mitgeschrieben, auch der, der nur einen Augenblick steht.
  await page.addInitScript(() => {
    ;(window as unknown as { gesehen: string[] }).gesehen = []
    new MutationObserver(() => {
      const text = document.body?.innerText ?? ''
      if (text.includes('gibt es nicht')) (window as unknown as { gesehen: string[] }).gesehen.push(text.slice(0, 120))
    }).observe(document, { childList: true, subtree: true, characterData: true })
  })

  await page.goto(`/plan?programm=${erste.id}`, { waitUntil: 'networkidle' })
  await page.getByRole('tablist', { name: 'Plan von' }).getByRole('button', { name: zweite.name, exact: false }).click()
  await expect(page).toHaveURL(new RegExp(`programm=${zweite.id}`))
  await page.locator('[data-audit=plan-airTempC]').first().fill('24')
  await page.locator('#programm-name-feld').fill(`E2E Zurück ${Date.now()}`)
  await page.locator('[data-audit=programm-speichern]').click()
  await expect(page).toHaveURL(/programm=eigen-/)
  await expect(page.getByText('als neues eigenes Programm')).toBeVisible()

  const gesehen = await page.evaluate(() => (window as unknown as { gesehen: string[] }).gesehen)
  expect(gesehen, 'Beim Wechsel auf die Kopie stand kurz „Dieses Programm gibt es nicht“.').toEqual([])

  // Beim Speichern ersetzt die Kopie den Verlaufseintrag der Vorlage; „Zurück“ führt deshalb zum
  // ersten Programm — einem fremden. Die Erfolgsmeldung gehört nur zur Kopie.
  await page.goBack()
  await expect(page).toHaveURL(new RegExp(`programm=${erste.id}`))
  await expect(page.getByText('Vorlage ·').first()).toBeVisible()
  await expect(page.getByText('als neues eigenes Programm')).toHaveCount(0)
})

test('Plan: ein Programm ohne Wochenwerte sagt, warum es nichts zu ändern gibt', async ({ page }) => {
  const liste = await programme(page)
  const ohne = liste.find((p) => p.wochen === 0)
  darfUeberspringen(!ohne, 'Kein Programm ohne Wochenwerte im Bestand — Canna Aqua und VBX bringen keine mit.')

  await page.goto(`/plan?programm=${ohne!.id}`, { waitUntil: 'networkidle' })

  await expect(page.getByText('keine Wochenwerte')).toBeVisible()
  await expect(page.locator('[data-audit=programm-speichern]')).toHaveCount(0)
})

test('Plan: ein unbekanntes Programm meldet sich, statt leer zu bleiben', async ({ page }) => {
  await page.goto('/plan?programm=gibt-es-nicht', { waitUntil: 'networkidle' })

  await expect(page.getByText('Dieses Programm gibt es nicht')).toBeVisible()
})
