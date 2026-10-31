import { test } from '@playwright/test'
import { writeFileSync } from 'node:fs'

/**
 * Hilfslauf, kein Test: sammelt jede Klasse, die auf einer Route tatsaechlich im
 * DOM landet, samt dem berechneten `display` des ersten Vorkommens.
 *
 * Grund: eine Klasse im Quelltext zu suchen sagt nicht, ob sie gerendert wird —
 * und genau diese Unterscheidung entscheidet, welche verlorenen Regeln
 * zurueckmuessen und welche mit dem alten Markup ohnehin weg sind.
 */
const ROUTES = [
  '/', '/messung', '/addback', '/aufgaben', '/grows', '/grows/1', '/sorten', '/archiv',
  '/regeln', '/messungen', '/diagnose', '/journal', '/sops', '/zelte', '/zelte/1',
  '/hydro', '/hydro/1', '/hydro/new', '/geraete?reiter=inventar', '/geraete?reiter=verbindung', '/wissen', '/start', '/settings',
]

// Die Liste muss etwas hergeben. Ohne diese Zeile laeuft die Schleife
// darunter bei einer leeren Liste null Mal durch — und der Testlauf meldet
// gruen, obwohl er nichts geprueft hat. Genau diese Falle hat in diesem
// Projekt schon zugeschlagen.
if (ROUTES.length < 5) throw new Error(`Diese Pruefung sieht nur ${ROUTES.length} Seiten — sie wuerde nichts messen.`)

test('sammle gerenderte Klassen', async ({ page }) => {
  // Standardmaessig aus: der Lauf schreibt eine Datei, und ein Testlauf, der bei
  // jedem Durchgang den Arbeitsbaum aendert, ist keiner. Mit COLLECT_CLASSES=1
  // aufrufen, wenn die Bestandsaufnahme neu gebraucht wird.
  test.skip(!process.env.COLLECT_CLASSES, 'Hilfslauf — mit COLLECT_CLASSES=1 starten')

  const seen: Record<string, { routes: string[]; display: string }> = {}

  for (const route of ROUTES) {
    await page.setViewportSize({ width: 1440, height: 900 })
    await page.goto(route, { waitUntil: 'networkidle' })
    const found = await page.evaluate(() => {
      const out: Record<string, string> = {}
      for (const element of Array.from(document.querySelectorAll('*'))) {
        for (const cls of Array.from(element.classList)) {
          if (!(cls in out)) out[cls] = getComputedStyle(element).display
        }
      }
      return out
    })
    for (const [cls, display] of Object.entries(found)) {
      if (!seen[cls]) seen[cls] = { routes: [], display }
      seen[cls].routes.push(route)
    }
  }

  writeFileSync('e2e/baseline/rendered-classes.json', JSON.stringify(seen, null, 1))
  console.log(`${Object.keys(seen).length} Klassen auf ${ROUTES.length} Routen`)
})
