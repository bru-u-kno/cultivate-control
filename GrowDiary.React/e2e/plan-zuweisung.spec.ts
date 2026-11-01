import { test, expect } from '@playwright/test'
import { backendAntwortet, darfUeberspringen } from './pflicht'
import { gibSchloss, nimmSchloss } from './schloss'

/**
 * Pläne zuweisen (A-030) am echten Weg: Plan › Düngeplan › „Gilt für diese Grows“ und Plan › Trocknung › „Gilt für diese
 * Trockenzelte“. Der Düngeplan gehört dem Grow, der Trocknungsplan dem Zelt.
 *
 * Der Demobestand hat kein Zelt mit Zweck „Trocknung“, und andere Prüfungen zählen dessen Zelte (`ernte-trockenzelt` erwartet
 * keines) — deshalb halten die Fälle das Schloss von Anlegen bis Abräumen.
 *
 * Der erste Fall sichert eine Verwechslung ab, die beim Bauen passiert ist: Zwei Geschwister in `PlanInhalt` trugen denselben
 * `key` (das Programm). React ließ den älteren Block beim Aktualisieren als zweiten Knoten stehen — die Seite zeigte die Liste
 * doppelt. Deshalb zählt der Fall die Blöcke, nicht nur ihr Dasein.
 */
type Etappe = { name: string; dauerTage: number; lufttemperaturC: number; luftfeuchteZiel: number; luftfeuchteMax: number }
type Plan = { etappen: Etappe[]; istStandard: boolean }
type ZuweisungGrow = { growId: number; name: string; zugewiesen: boolean; hatPlan: boolean; eigeneAenderungen: number; aktuellesProgramm: string | null }

const zelte: number[] = []

test.beforeEach(async ({ request }) => {
  darfUeberspringen(!(await backendAntwortet(request)), 'Kein Backend unter GROW_OS_URL — die Zuweisung braucht die laufende App.')
  await nimmSchloss()
})

test.afterEach(async ({ request }) => {
  for (const id of zelte.splice(0)) await request.delete(`/api/settings/tents/${id}`)
  await gibSchloss()
})

test('Düngeplan: einem Grow zuweisen — Plan und Grow stimmen überein, die Liste steht genau einmal da', async ({ page, request }) => {
  const programme = (await (await request.get('/api/programme')).json()) as Array<{ id: string; eigen: boolean; wochen: number }>
  const ziel = programme.find((p) => !p.eigen && p.wochen > 0)
  darfUeberspringen(!ziel, 'Kein mitgeliefertes Programm mit Wochenraster.')
  const programmId = ziel!.id

  const kandidaten = (await (await request.get(`/api/programme/${programmId}/zuweisung`)).json()) as ZuweisungGrow[]
  // Ohne eigene Änderungen (sonst fragt die Seite zuerst) und mit einem bisherigen Programm, damit der Bestand genau zurückgesetzt werden kann.
  const grow = kandidaten.find((g) => !g.zugewiesen && g.eigeneAenderungen === 0 && g.aktuellesProgramm !== null)
  darfUeberspringen(!grow, 'Kein Grow ohne eigene Änderungen, der ein anderes Programm nutzt.')
  const vorher = ((await (await request.get(`/api/grows/${grow!.growId}`)).json()) as { feedProgramId: string | null }).feedProgramId

  let abrufe = 0
  page.on('request', (r) => { if (r.url().includes(`/api/programme/${programmId}/zuweisung`) && r.method() === 'GET') abrufe += 1 })

  try {
    await test.step('Die Liste erscheint genau einmal, ohne Haken ist Speichern gesperrt', async () => {
      await page.goto(`/plan?programm=${programmId}`, { waitUntil: 'networkidle' })
      await expect(page.locator('[data-audit=programm-zuweisung]')).toHaveCount(1)
      await expect(page.locator('[data-audit=plan-gilt-duenge] [data-audit=gilt-zeile]')).toHaveCount(kandidaten.length)
      await expect(page.locator('[data-audit=plan-zuweisen]')).toBeDisabled()
      expect(abrufe, 'Die Liste wurde mehrfach geladen — zwei Instanzen derselben Komponente.').toBe(1)
    })

    await test.step('Ankreuzen zeigt, was ersetzt wird; Speichern stellt Grow und Plan um', async () => {
      const zeile = page.locator('[data-audit=gilt-zeile]').filter({ hasText: grow!.name })
      await zeile.locator('input').check()
      if (grow!.hatPlan) await expect(zeile).toContainText(/Wochenwerte wechseln|eigene Änderung/)
      await page.locator('[data-audit=plan-zuweisen]').click()
      await expect(page.getByText(/Zugewiesen:/)).toBeVisible()
      await expect(zeile).toContainText('Schon zugewiesen')
      await expect(zeile.locator('input')).toBeDisabled()

      const nachher = (await (await request.get(`/api/grows/${grow!.growId}`)).json()) as { feedProgramId: string | null }
      expect(nachher.feedProgramId).toBe(programmId)
      if (grow!.hatPlan) {
        const plan = (await (await request.get(`/api/grows/${grow!.growId}/plan`)).json()) as { programmId: string }
        expect(plan.programmId, 'Der Plan gewinnt beim Lesen — er muss dasselbe Programm tragen wie der Grow.').toBe(programmId)
      }
      await expect(page.locator('[data-audit=programm-zuweisung]')).toHaveCount(1)
    })
  } finally {
    // Der Bestand gehört auch den anderen Prüfungen: Programm zurück, wie es war.
    if (vorher) {
      await request.post(`/api/programme/${vorher}/zuweisung`, { data: { growIds: [grow!.growId], aenderungenBehalten: true } })
    }
  }
})

test('Trocknungsplan: ein einziges Trockenzelt hat nichts zu wählen — zwei: der Plan geht als Kopie ans andere', async ({ page, request }) => {
  const anlegen = async (name: string) => {
    const antwort = await request.post('/api/settings/tents', { data: { name: `${name} ${Date.now()}`, tentType: 'Drying', status: 'Active', displayOrder: 99 } })
    darfUeberspringen(!antwort.ok(), 'Testzelt ließ sich nicht anlegen.')
    const zelt = (await antwort.json()) as { id: number; name: string }
    zelte.push(zelt.id)
    return zelt
  }
  const a = await anlegen('Zuweisung A')

  await test.step('Mit einem Trockenzelt gibt es den Block nicht', async () => {
    await page.goto(`/plan?art=trocknung&zelt=${a.id}`, { waitUntil: 'networkidle' })
    await expect(page.locator('[data-audit=trocknung-reiter]')).toBeVisible()
    await expect(page.locator('[data-audit=trocknung-zuweisung]')).toHaveCount(0)
  })

  const b = await anlegen('Zuweisung B')
  const standard = (await (await request.get(`/api/tents/${a.id}/trocknung/plan`)).json()) as Plan
  const etappen = standard.etappen.map((e, i) => (i === 0 ? { ...e, dauerTage: 4 } : e))
  await request.put(`/api/tents/${a.id}/trocknung/plan`, { data: { etappen } })

  await test.step('Mit zwei Zelten steht der Block genau einmal da; dieses Zelt ist gesperrt angekreuzt', async () => {
    await page.goto(`/plan?art=trocknung&zelt=${a.id}`, { waitUntil: 'networkidle' })
    await expect(page.locator('[data-audit=trocknung-zuweisung]')).toHaveCount(1)
    const zeilen = page.locator('[data-audit=trocknung-zuweisung] [data-audit=gilt-zeile]')
    await expect(zeilen).toHaveCount(2)
    await expect(zeilen.filter({ hasText: a.name }).locator('input')).toBeChecked()
    await expect(zeilen.filter({ hasText: a.name }).locator('input')).toBeDisabled()
    await expect(page.locator('[data-audit=trocknung-uebertragen]')).toBeDisabled()
  })

  await test.step('Übertragen: B bekommt dieselben Etappen und gilt als eigener Plan', async () => {
    await page.locator('[data-audit=gilt-zeile]').filter({ hasText: b.name }).locator('input').check()
    await page.locator('[data-audit=trocknung-uebertragen]').click()
    await expect(page.getByText(/Übertragen auf:/)).toBeVisible()

    const planB = (await (await request.get(`/api/tents/${b.id}/trocknung/plan`)).json()) as Plan
    expect(planB.istStandard).toBe(false)
    expect(planB.etappen.map((e) => e.dauerTage)).toEqual(etappen.map((e) => e.dauerTage))
    expect(planB.etappen[0].dauerTage).toBe(4)
  })

  await test.step('Eine Kopie: ändert man A danach, bleibt B unberührt', async () => {
    await request.put(`/api/tents/${a.id}/trocknung/plan`, { data: { etappen: etappen.map((e, i) => (i === 0 ? { ...e, dauerTage: 5 } : e)) } })
    const planB = (await (await request.get(`/api/tents/${b.id}/trocknung/plan`)).json()) as Plan
    expect(planB.etappen[0].dauerTage).toBe(4)
  })

  await test.step('Ungespeicherte Änderung: übertragen geht erst nach dem Speichern', async () => {
    await page.goto(`/plan?art=trocknung&zelt=${a.id}`, { waitUntil: 'networkidle' })
    await page.locator('[data-audit=gilt-zeile]').filter({ hasText: b.name }).locator('input').check()
    await page.locator('[data-audit=trocknung-dauer]').fill('2')
    await expect(page.locator('[data-audit=trocknung-uebertragen]')).toBeDisabled()
    await expect(page.getByText(/noch nicht gespeichert/)).toBeVisible()
  })
})
