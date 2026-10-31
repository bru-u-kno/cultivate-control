import { test } from '@playwright/test'
import { writeFileSync, mkdirSync } from 'node:fs'

/**
 * Nimmt die *berechneten* Werte auf, nicht die Regeln.
 *
 * Beim Verschieben von Regeln aus rc2-overrides in Feature-Dateien ändert sich
 * die Schicht — und damit, wer gegen wen gewinnt. Der Struktur-Baseline reicht
 * das nicht: sie prüft Überlauf und Geometrie, nicht ob eine Karte ihren
 * Innenabstand behalten hat. Also vorher aufnehmen, nachher vergleichen.
 *
 * Aufruf: SNAPSHOT=vorher npx playwright test e2e/computed-snapshot.spec.ts
 */
const ROUTES = ['/', '/zelte', '/zelte/1', '/hydro', '/grows/1', '/messungen', '/geraete?reiter=inventar', '/regeln', '/geraete?reiter=verbindung', '/addback']

const PROPS = [
  'display', 'gridTemplateColumns', 'flexDirection', 'flexWrap', 'gap',
  'padding', 'margin', 'width', 'height', 'minHeight', 'maxWidth',
  'backgroundColor', 'borderTopWidth', 'borderTopColor', 'borderRadius',
  'fontSize', 'fontWeight', 'color', 'textAlign', 'overflow',
]

test('nimm berechnete Werte auf', async ({ page }) => {
  const label = process.env.SNAPSHOT
  test.skip(!label, 'Mit SNAPSHOT=<name> starten')

  const out: Record<string, Record<string, string>> = {}

  for (const route of ROUTES) {
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.goto(route, { waitUntil: 'networkidle' })
    const values = await page.evaluate((props) => {
      const result: Record<string, string> = {}
      const seen = new Map<string, number>()
      for (const element of Array.from(document.querySelectorAll('*'))) {
        if (!(element instanceof HTMLElement)) continue
        const classes = Array.from(element.classList).sort().join('.')
        if (!classes) continue
        // Pro Klassenkombination die ersten drei Vorkommen — mehr sagt nichts Neues,
        // kostet aber Rauschen bei Listen.
        const index = seen.get(classes) ?? 0
        if (index >= 3) continue
        seen.set(classes, index + 1)
        const style = getComputedStyle(element)
        result[`${classes}#${index}`] = props.map((p) => `${p}=${style[p as never]}`).join('|')
      }
      return result
    }, PROPS)
    out[route] = values
  }

  mkdirSync('e2e/baseline', { recursive: true })
  writeFileSync(`e2e/baseline/computed-${label}.json`, JSON.stringify(out, null, 1))
  console.log(`${Object.values(out).reduce((sum, v) => sum + Object.keys(v).length, 0)} Elemente aufgenommen`)
})
