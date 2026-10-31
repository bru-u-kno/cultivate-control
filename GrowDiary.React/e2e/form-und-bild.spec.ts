import { test, expect, type Page } from '@playwright/test'
import { darfUeberspringen } from './pflicht'
import { TEXTSEITEN } from './seiten'

/**
 * Was auf dem Schirm falsch geschrieben steht — und was abgeschnitten wird.
 *
 * **Der Anlass (02.09.2026).** Ein Durchgang über alle Seiten in beiden Themen
 * und über drei Breiten fand drei Klassen, die keine vorhandene Prüfung sieht:
 *
 * - Auf `/handy` lief die Überschrift „Warum nicht einfach die Adresse aus der
 *   Adresszeile" bei 360 px **95 px** über ihren Kasten und wurde abgeschnitten.
 *   Die **Seite** rollte dabei nicht — `.v1-section { overflow: hidden }` schnitt
 *   still ab, und `handy-zuschnitt.spec.ts` blieb grün.
 * - Auf `/grows/1` stand in der Sorten-Kachel „gemischt (…)": `white-space:
 *   nowrap` schnitt bei 4 px Überlauf ab, und weg war die **Anzahl** — also
 *   genau die Auskunft.
 * - Im Aufgabentitel stand ein gerader Bindestrich als Gedankenstrich.
 *
 * **Warum das hier steht und nicht nur im Werkzeug.** Ein Durchgang, den
 * niemand wiederholt, ist eine Momentaufnahme. Die ausführliche Fassung liegt
 * in `zz-plausibel/visualaudit.mjs` und `zz-plausibel/formaudit.mjs`; diese
 * Datei hält die drei Klassen fest, die dabei etwas gefunden haben.
 */

/** Sichtbar heisst: ein Mensch sieht es. */
const SICHTBAR = `(el) => {
  for (let n = el; n && n !== document.body; n = n.parentElement) {
    const s = getComputedStyle(n)
    if (s.display === 'none' || s.visibility === 'hidden' || Number(s.opacity) === 0) return false
    if (s.clipPath !== 'none' && s.clipPath !== '') return false
    if (n !== el) {
      const k = n.getBoundingClientRect()
      if (k.height <= 2 && s.overflow !== 'visible') return false
    }
  }
  const k = el.getBoundingClientRect()
  return k.width > 0 && k.height > 0
}`

async function abgeschnitten(seite: Page) {
  return seite.evaluate((sichtbarQuelle) => {
    const sichtbar = eval(sichtbarQuelle) as (el: Element) => boolean
    const raus: string[] = []
    const wurzel = document.querySelector('main') ?? document.body
    const lauf = document.createTreeWalker(wurzel, NodeFilter.SHOW_TEXT)

    for (let knoten = lauf.nextNode(); knoten; knoten = lauf.nextNode()) {
      const text = (knoten.textContent || '').trim()
      if (text.length < 2) continue
      const el = knoten.parentElement
      if (!el || !sichtbar(el)) continue

      const stil = getComputedStyle(el)
      if (stil.overflowX === 'auto' || stil.overflowX === 'scroll') continue

      // Was in einem eigenen Rollbereich liegt, ist nicht abgeschnitten.
      let rollbar = false
      for (let n = el.parentElement; n && n !== document.body; n = n.parentElement) {
        const s2 = getComputedStyle(n)
        if (s2.overflowX === 'auto' || s2.overflowX === 'scroll') { rollbar = true; break }
      }
      if (rollbar) continue

      const bereich = document.createRange()
      bereich.selectNodeContents(knoten)
      const kaesten = [...bereich.getClientRects()]
      if (kaesten.length === 0) continue

      const kasten = el.getBoundingClientRect()
      const ueber = Math.max(
        Math.max(...kaesten.map((k) => k.right)) - kasten.right,
        kasten.left - Math.min(...kaesten.map((k) => k.left)),
      )
      if (ueber > 1.5) {
        raus.push(`${Math.round(ueber)}px  <${el.tagName.toLowerCase()}> „${text.slice(0, 60)}"`)
      }
    }

    /* Und der zweite Weg: das ELEMENT ragt ueber einen Vorfahren, der
       beschneidet. So sass der Fund auf /handy — die Ueberschrift war 426 px
       breit in einem 334 px schmalen Kopf, und `.v1-section { overflow:
       hidden }` schnitt sie ab. Der Text passte dabei brav in sein eigenes
       Element; die Pruefung darueber sieht das nicht. */
    for (const el of wurzel.querySelectorAll('h1,h2,h3,h4,p,span,strong,label,td,th,li')) {
      if (!sichtbar(el)) continue
      if ((el.textContent || '').trim().length < 3) continue

      const k = el.getBoundingClientRect()
      for (let n = el.parentElement; n && n !== document.body; n = n.parentElement) {
        const s2 = getComputedStyle(n)
        /* Ein ROLLBARER Vorfahr kommt zuerst: was dort hinausragt, rollt und ist
           nicht abgeschnitten. Die Geraetetabelle liegt in einem
           `overflow-x: auto`, und ohne diesen Abbruch meldete die Pruefung ihre
           ganze Breite als Fehler. */
        if (s2.overflowX === 'auto' || s2.overflowX === 'scroll'
          || s2.overflowY === 'auto' || s2.overflowY === 'scroll') break

        const beschneidet = s2.overflow === 'hidden' || s2.overflowX === 'hidden'
          || s2.overflow === 'clip' || s2.overflowX === 'clip'
        if (!beschneidet) continue

        const kn = n.getBoundingClientRect()
        const ueberVorfahr = Math.max(k.right - kn.right, kn.left - k.left)
        if (ueberVorfahr > 1.5) {
          raus.push(`${Math.round(ueberVorfahr)}px  <${el.tagName.toLowerCase()}> ragt aus `
            + `<${n.tagName.toLowerCase()}.${(n.className || '').toString().split(/\s+/)[0]}> `
            + `„${(el.textContent || '').trim().slice(0, 50)}"`)
        }
        break
      }
    }

    return raus
  }, SICHTBAR)
}

/** Formfehler, die keine andere Prüfung sieht. */
const MUSTER: Array<{ name: string; regex: RegExp; warum: string }> = [
  {
    name: 'Platzhalter aus dem Code',
    // NICHT „Infinity": „AC-Infinity" ist ein Herstellername.
    regex: /(?<![-\w])(undefined|NaN|\[object Object\])(?![-\w])/,
    warum: 'Ein Wert, den die Oberfläche nicht hatte, steht roh auf dem Schirm.',
  },
  {
    name: 'ISO-Zeitstempel',
    regex: /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/,
    warum: 'Ein Zeitstempel aus der Schnittstelle ist ungeformt durchgeschlagen.',
  },
  {
    name: 'gerader Bindestrich als Gedankenstrich',
    regex: /\S - \S/,
    warum: 'Im Aufgabentitel stand „Gerät - Titel" statt eines Gedankenstrichs.',
  },
  {
    name: 'Zahl mit zu vielen Nachkommastellen',
    regex: /(?<![\w.])\d+,\d{4,}(?![\d])/,
    warum: 'Eine Rechnung ist ungerundet durchgereicht worden.',
  },
]

async function formfehler(seite: Page, muster: { name: string; quelle: string }[]) {
  return seite.evaluate((liste) => {
    const raus: string[] = []
    const wurzel = document.querySelector('main') ?? document.body
    const lauf = document.createTreeWalker(wurzel, NodeFilter.SHOW_TEXT)

    for (let knoten = lauf.nextNode(); knoten; knoten = lauf.nextNode()) {
      // Geschuetztes Leerzeichen (U+00A0) auf ein gewoehnliches abbilden —
      // sonst greift kein Muster, das mit \s arbeitet.
      const text = (knoten.textContent || '').replace(/\u00a0/g, ' ')
      if (!text.trim()) continue
      const el = knoten.parentElement
      if (!el) continue
      const stil = getComputedStyle(el)
      if (stil.display === 'none' || stil.visibility === 'hidden') continue

      // Kennungen und Quelltext stehen bewusst so da.
      let kennung = false
      for (let n: Element | null = el; n && n !== document.body; n = n.parentElement) {
        const tag = n.tagName.toLowerCase()
        if (tag === 'code' || tag === 'pre' || tag === 'kbd') { kennung = true; break }
        if (/entity|kennung|mono|url|version/i.test(n.getAttribute('class') || '')) { kennung = true; break }
      }
      if (kennung) continue

      for (const m of liste) {
        if (new RegExp(m.quelle).test(text)) {
          raus.push(`${m.name}: „${text.trim().slice(0, 70)}"`)
        }
      }
    }

    return raus
  }, muster)
}

for (const pfad of TEXTSEITEN) {
  test(`${pfad} — kein Formfehler im Text`, async ({ page }) => {
    const antwort = await page.goto(pfad, { waitUntil: 'networkidle' })
    darfUeberspringen(antwort == null || antwort.status() >= 400, `${pfad} antwortet nicht.`)

    // Mengenwaechter: eine leere Seite haette nie einen Formfehler.
    const laenge = await page.evaluate(() =>
      ((document.querySelector('main') as HTMLElement | null)?.innerText || '').trim().length)
    darfUeberspringen(laenge < 200, `${pfad} zeigt fast nichts — vermutlich ein Ladezustand`)

    const funde = await formfehler(
      page,
      MUSTER.map((m) => ({ name: m.name, quelle: m.regex.source })),
    )

    expect(funde, funde.join('\n')).toEqual([])
  })
}

/* Der Zuschnitt nur an den drei Breiten, an denen der Durchgang etwas fand —
   und nur auf den Seiten mit viel Text. Ueber ALLE Seiten x 3 Breiten dauert
   das im Tor zu lange; die ausfuehrliche Fassung steht in
   zz-plausibel/visualaudit.mjs. */
for (const breite of [360, 768, 1280]) {
  test(`kein abgeschnittener Text bei ${breite} px`, async ({ page }) => {
    await page.setViewportSize({ width: breite, height: 900 })

    const funde: string[] = []
    for (const pfad of ['/handy', '/grows/1', '/zelte/1', '/geraete?reiter=inventar', '/sops', '/diagnose']) {
      const antwort = await page.goto(pfad, { waitUntil: 'networkidle' })
      if (antwort == null || antwort.status() >= 400) continue
      await page.waitForTimeout(250)
      for (const f of await abgeschnitten(page)) funde.push(`${pfad} @${breite}  ${f}`)
    }

    expect(funde,
      'Diese Texte laufen ueber ihren Kasten und werden abgeschnitten:\n' + funde.join('\n')
      + '\n\nDie SEITE rollt dabei nicht — handy-zuschnitt.spec.ts sieht das nicht.')
      .toEqual([])
  })
}
