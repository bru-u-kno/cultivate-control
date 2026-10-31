import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * cc.263 (Raum, Stufe 2): Steht ein Zelt im Raum und hat der Raum beide Fühler, heißen „Außen Temp./RLF/VPD“ nach dem Raum,
 * kommen von dort — und die drei eigenen Zeilen des Zelts entfallen in der Zuordnung. Ohne Raum bleibt alles wie bisher
 * (`aussenwerte.spec.ts`). Läuft gegen die echte Demo-App; der Raum wird hier angelegt und am Ende zurückgenommen.
 */
// Raum, Rollen und Zelt gehören der ganzen geteilten App — nacheinander, nicht nebeneinander.
test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ request }) => {
  await nimmSchloss()
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — der Raum braucht die laufende App.')
})

test.afterEach(() => { gibSchloss() })

type Zelt = { id: number; name: string; raumId: string | null; sensors: Array<{ metricType: string; haEntityId: string }> }

test('Zelt im Raum: Verlauf und Zuordnung nennen den Raum statt „Außen“, die drei Zeilen am Zelt entfallen — zweimal (390 px)', async ({ page, request }) => {
  const zelte = (await (await request.get('/api/settings/tents')).json()) as Zelt[]
  const ziel = zelte[0]
  const eigene = (art: string) => ziel.sensors.find((s) => s.metricType === art)?.haEntityId
  const temp = eigene('OutsideTemperature')
  const rh = eigene('OutsideHumidity')
  darfUeberspringen(!temp || !rh, 'Der Demobestand ordnet keine Außenwerte zu — die Prüfung sähe nichts.')

  try {
    await request.put('/api/steuerung/geraete/raum', { data: { zuordnungen: { raum_temp: temp, raum_rh: rh } } })
    const aktuell = await (await request.get(`/api/settings/tents/${ziel.id}`)).json()
    await request.put(`/api/settings/tents/${ziel.id}`, { data: { ...aktuell, raumId: 'haupt' } })
    // Zuletzt der Name: das Speichern leert den kurzen Merker der Beschriftung.
    await request.put('/api/steuerung/raum', { data: { name: 'Testkeller' } })
    await expect.poll(async () => (await (await request.get('/api/steuerung/raum')).json()).aussenWerte).toBe(true)

    await page.setViewportSize({ width: 390, height: 900 })
    for (let runde = 0; runde < 2; runde++) {
      // Zuordnung: die drei Außen-Zeilen DIESES Zelts sind weg, je Messgröße steht eine Raumwert-Zeile dafür da.
      await page.goto('/geraete?reiter=zuordnung', { waitUntil: 'networkidle' })
      const zeilen = page.locator('[data-audit="ha-entity-row"]')
      expect(await zeilen.count(), 'Keine Zuordnungszeilen — die Prüfung sähe nichts.').toBeGreaterThanOrEqual(5)
      await expect(zeilen.filter({ has: page.locator(`[data-tent="${ziel.id}"]`) }), `Runde ${runde + 1}: Außen-Zeilen stehen noch`).toHaveCount(0)
      const raumzeilen = page.locator(`[data-audit="ha-raumwerte-row"][data-tent="${ziel.id}"]`)
      await expect(raumzeilen).toHaveCount(3)
      await expect(raumzeilen.first()).toContainText('Vom Raum „Testkeller“')
      await expect(raumzeilen.nth(0).locator('.co-row-value')).toHaveText(/^\d+(,\d+)? °C$/)
      await expect(raumzeilen.nth(1).locator('.co-row-value')).toHaveText(/^\d+(,\d+)? %$/)
      await expect(raumzeilen.nth(2).locator('.co-row-value')).toHaveText(/^\d+(,\d+)? kPa$/)

      // Verlauf: die drei Karten heißen nach dem Raum.
      await page.goto('/', { waitUntil: 'networkidle' })
      const verlauf = page.locator('[data-audit="verlauf"]').first()
      await verlauf.scrollIntoViewIfNeeded()
      for (const [schluessel, name] of [['outside-temperature', 'Testkeller Temp.'], ['outside-humidity', 'Testkeller RLF'], ['outside-vpd', 'Testkeller VPD']] as const) {
        await expect(verlauf.locator(`[data-audit="verlauf-karte-${schluessel}"] .vd-wert-name`), `Runde ${runde + 1}: ${schluessel}`).toHaveText(name)
      }
    }

    // Hat das Zelt den Raum verlassen, gelten seine eigenen Zeilen und „Außen“ wieder — nichts ging verloren.
    const jetzt = await (await request.get(`/api/settings/tents/${ziel.id}`)).json()
    await request.put(`/api/settings/tents/${ziel.id}`, { data: { ...jetzt, raumId: null } })
    await request.put('/api/steuerung/raum', { data: { name: 'Testkeller' } })
    await page.goto('/geraete?reiter=zuordnung', { waitUntil: 'networkidle' })
    await expect(page.locator(`[data-audit="ha-raumwerte-row"][data-tent="${ziel.id}"]`)).toHaveCount(0)
    await expect(page.locator(`[data-audit="ha-entity-row"][data-metric="OutsideTemperature"][data-tent="${ziel.id}"]`)).toHaveCount(1)
  } finally {
    const aktuell = await (await request.get(`/api/settings/tents/${ziel.id}`)).json()
    await request.put(`/api/settings/tents/${ziel.id}`, { data: { ...aktuell, raumId: ziel.raumId } })
    await request.put('/api/steuerung/geraete/raum', { data: { zuordnungen: { raum_temp: null, raum_rh: null } } })
    await request.put('/api/steuerung/raum', { data: { name: null } })
  }
})
