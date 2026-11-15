import { describe, expect, it } from 'vitest'
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * Das Aussehen steht an EINER Stelle (A-033, 15.11.2026).
 *
 * <b>Warum es diesen Test gibt.</b> Ein Stil (hell/dunkel heute, später wählbare
 * Aussehen) soll nur `tokens.css` umbelegen müssen. Vorher standen 181 Eckenradien,
 * 139 Sperrungen und 99 `uppercase` fest in 51 Stylesheets — wer den Stil ändern
 * wollte, musste jede Seite einzeln anfassen. Der Nutzer will genau das nicht mehr.
 *
 * <b>Was er verlangt.</b> Außerhalb von `tokens.css` keine feste Ecke, keine feste
 * Sperrung und kein festes `uppercase`. Erlaubt sind nur Token-Verweise und die
 * neutralen Werte (`0`, `normal`, `none`, `inherit`). Feste Schriftgrößen sind
 * ein Bestand, der nur schrumpfen darf (wie in `feste-farben.test.ts`).
 *
 * <b>Was er NICHT ist.</b> Kein Bildvergleich. Ob ein Stil gut aussieht, misst nur
 * das Ansehen der gebauten Seite.
 */

const wurzel = fileURLToPath(new URL('../..', import.meta.url))

function alle(verzeichnis: string, endung: RegExp): string[] {
  let gefunden: string[] = []
  for (const name of readdirSync(verzeichnis)) {
    const pfad = join(verzeichnis, name)
    if (statSync(pfad).isDirectory()) gefunden = gefunden.concat(alle(pfad, endung))
    else if (endung.test(name)) gefunden.push(pfad)
  }
  return gefunden
}

const kurz = (datei: string) => datei.slice(wurzel.length).split('\\').join('/').replace(/^\//, '')

/** Kommentare durch Leerraum ersetzen — Zeilennummern bleiben stimmig, Erwähnungen zählen nicht. */
const ohneKommentare = (text: string) => text.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))

type Fund = { datei: string; zeile: number; eigenschaft: string; wert: string }

const DEKLARATION = /(?:^|[;{\s])((?:-webkit-)?border(?:-(?:top|bottom)-(?:left|right))?-radius|letter-spacing|text-transform|font-size)\s*:\s*([^;{}]+?)\s*(?=[;}]|$)/gm

const cssDateien = alle(join(wurzel, 'src'), /\.css$/).filter((f) => !f.endsWith('tokens.css'))

const funde: Fund[] = cssDateien.flatMap((datei) => {
  const text = ohneKommentare(readFileSync(datei, 'utf8'))
  return [...text.matchAll(DEKLARATION)].map((m) => ({
    datei: kurz(datei),
    zeile: text.slice(0, m.index).split('\n').length,
    eigenschaft: m[1],
    wert: m[2].trim().replace(/\s+/g, ' '),
  }))
})

const TOKEN = /^var\(--[a-z0-9-]+\)$/
const ohneWichtig = (wert: string) => wert.replace(/\s*!important$/, '')

/** Ein Radiuswert ist in Ordnung, wenn jeder Bestandteil ein Token oder 0 ist. */
function eckeOk(wert: string): boolean {
  return ohneWichtig(wert).split(/\s+|\s*\/\s*/).filter(Boolean).every((teil) => teil === '0' || teil === 'inherit' || TOKEN.test(teil))
}

function nichtZentral(f: Fund): boolean {
  const w = ohneWichtig(f.wert)
  if (f.eigenschaft.endsWith('radius')) return !eckeOk(f.wert)
  if (f.eigenschaft === 'letter-spacing') return !(w === '0' || w === 'normal' || w === 'inherit' || TOKEN.test(w))
  if (f.eigenschaft === 'text-transform') return !(w === 'none' || w === 'inherit' || TOKEN.test(w))
  return false
}

/**
 * Feste Schriftgrößen vom 15.11.2026. NICHT gebilligt, nur gemessen: sie standen da,
 * als der Test entstand, und Schritt 1 von A-033 sollte nichts sichtbar ändern.
 * Die Liste darf nur kürzer werden. Wer eine Stelle auf ein --fs-Token umstellt,
 * streicht sie hier; eine neue feste Schriftgröße kommt nicht hinein.
 */
const SCHRIFT_BESTAND = [
  'src/features/addback/nachfuell-formular.css  font-size: .75rem',
  'src/features/addback/nachfuell-formular.css  font-size: .8em',
  'src/features/addback/nachfuell-formular.css  font-size: 1.05rem',
  'src/features/addback/nachfuell-formular.css  font-size: 1.45rem',
  'src/features/addback/nachfuell-formular.css  font-size: 1.5rem',
  'src/features/addback/nachfuell-formular.css  font-size: 1.6rem',
  'src/features/addback/nachfuell-formular.css  font-size: 1rem',
  'src/features/addback/plan-kasten.css  font-size: 1.4rem',
  'src/features/changeouts/changeouts.css  font-size: 0.95rem',
  'src/features/changeouts/changeouts.css  font-size: 0.9rem',
  'src/features/changeouts/changeouts.css  font-size: clamp(2rem, 8vw, 2.8rem)',
  'src/features/grows/grows-rc2.css  font-size: clamp(1.5rem, 6vw, 2.3rem)',
  'src/features/grows/grows.css  font-size: 0.85rem',
  'src/features/grows/grows.css  font-size: 0.9rem',
  'src/features/home-assistant/home-assistant.css  font-size: .72rem',
  'src/features/home-assistant/home-assistant.css  font-size: .8rem',
  'src/features/home-assistant/home-assistant.css  font-size: 1.05rem',
  'src/features/home-assistant/home-assistant.css  font-size: 1.12rem',
  'src/features/kosten/kosten.css  font-size: 1.05rem',
  'src/features/live/live-instrument.css  font-size: 22px',
  'src/features/live/live-instrument.css  font-size: 28px',
  'src/features/live/live-screen.css  font-size: 22px',
  'src/features/steuerung/steuerung.css  font-size: .92em',
  'src/features/steuerung/steuerung.css  font-size: 0.9rem',
  'src/features/steuerung/steuerung.css  font-size: 11px',
  'src/features/steuerung/steuerung.css  font-size: 2.25rem',
  'src/features/trocknung/fertig.css  font-size: .82rem',
  'src/features/trocknung/fertig.css  font-size: .85rem',
  'src/features/trocknung/fertig.css  font-size: .8rem',
  'src/features/trocknung/fertig.css  font-size: 1.1rem',
  'src/features/trocknung/fertig.css  font-size: 9px',
  'src/features/trocknung/ziel.css  font-size: .85rem',
  'src/features/trocknung/ziel.css  font-size: .8rem',
  'src/features/trocknung/ziel.css  font-size: 1.1rem',
  'src/features/zeltruhe/zeltruhe.css  font-size: 0.85rem',
  'src/features/zeltruhe/zeltruhe.css  font-size: 0.8rem',
  'src/styles/80-grow-wizard-final.css  font-size: clamp(1.25rem, 2vw, 1.8rem)',
  'src/styles/90-operations.css  font-size: 1.9rem',
  'src/styles/forkai-shell.css  font-size: 15px',
  'src/styles/forkai-shell.css  font-size: 17px',
  'src/styles/forkai-shell.css  font-size: 22px',
]

const schriftFest = funde.filter((f) => f.eigenschaft === 'font-size' && !TOKEN.test(ohneWichtig(f.wert)) && !/^(inherit|1em|100%)$/.test(f.wert))
const schluessel = (f: Fund) => `${f.datei}  ${f.eigenschaft}: ${f.wert}`

describe('Aussehen zentral (A-033)', () => {
  it('findet die Stildateien und die Deklarationen überhaupt', () => {
    expect(cssDateien.length, 'keine CSS-Dateien gefunden — stimmt der Pfad noch?').toBeGreaterThan(40)
    const ecken = funde.filter((f) => f.eigenschaft.endsWith('radius')).length
    const sperrungen = funde.filter((f) => f.eigenschaft === 'letter-spacing').length
    const schreibungen = funde.filter((f) => f.eigenschaft === 'text-transform').length
    expect(ecken, 'kaum Radien gefunden — greift der Ausdruck noch?').toBeGreaterThan(200)
    expect(sperrungen, 'kaum Sperrungen gefunden — greift der Ausdruck noch?').toBeGreaterThan(100)
    expect(schreibungen, 'kaum text-transform gefunden — greift der Ausdruck noch?').toBeGreaterThan(80)
  })

  it('erkennt einen festen Wert, wenn einer dasteht (Selbsttest der Regel)', () => {
    const probe = (eigenschaft: string, wert: string) => nichtZentral({ datei: 'x', zeile: 1, eigenschaft, wert })
    expect(probe('border-radius', '10px')).toBe(true)
    expect(probe('border-radius', '0 8px 8px 0')).toBe(true)
    expect(probe('border-top-left-radius', '50%')).toBe(true)
    expect(probe('letter-spacing', '.1em')).toBe(true)
    expect(probe('text-transform', 'uppercase')).toBe(true)
    expect(probe('border-radius', '0 var(--ecke-8) var(--ecke-8) 0')).toBe(false)
    expect(probe('letter-spacing', 'normal !important')).toBe(false)
    expect(probe('text-transform', 'var(--etikett-schreibung)')).toBe(false)
    expect(probe('-webkit-border-radius', '4px')).toBe(true)
  })

  it('kein fester Radius, keine feste Sperrung, kein festes uppercase außerhalb von tokens.css', () => {
    const fest = funde.filter(nichtZentral)
    expect(
      fest.map((f) => `${f.datei}:${f.zeile}  ${f.eigenschaft}: ${f.wert}`),
      'Fester Wert für das Aussehen. Ein Stil kann ihn nicht umbelegen. Eine Stufe aus '
        + 'tokens.css nehmen (--ecke-*, --r-*, --sperrung-*, --etikett-schreibung) oder dort eine anlegen:',
    ).toEqual([])
  })

  it('keine neue feste Schriftgröße', () => {
    const bestand = new Set(SCHRIFT_BESTAND)
    const neu = schriftFest.filter((f) => !bestand.has(schluessel(f)))
    expect(neu.map((f) => `${f.datei}:${f.zeile}  font-size: ${f.wert}`),
      'Neue feste Schriftgröße — bitte eine --fs-Stufe aus tokens.css benutzen:').toEqual([])
  })

  it('lässt den Schrift-Bestand nicht stehen, wenn er weg ist', () => {
    const vorhanden = new Set(schriftFest.map(schluessel))
    expect(SCHRIFT_BESTAND.filter((e) => !vorhanden.has(e)),
      'Diese Bestands-Einträge gibt es nicht mehr — bitte aus SCHRIFT_BESTAND streichen:').toEqual([])
  })

  it('jedes benutzte Aussehen-Token ist in tokens.css definiert', () => {
    // Ein Verweis auf ein undefiniertes Token ist still wirkungslos — genau so stand
    // var(--fs-micro, .625rem) monatelang da, ohne dass --fs-micro je existierte.
    const tokens = readFileSync(join(wurzel, 'src/styles/tokens.css'), 'utf8')
    const definiert = new Set([...tokens.matchAll(/(--[a-z0-9-]+)\s*:/g)].map((m) => m[1]))
    const quellen = [...cssDateien, ...alle(join(wurzel, 'src'), /\.tsx?$/).filter((f) => !/\.test\.tsx?$/.test(f))]
    const benutzt = new Set<string>()
    for (const datei of quellen) {
      for (const m of ohneKommentare(readFileSync(datei, 'utf8')).matchAll(/var\((--(?:ecke|sperrung|etikett|kurve|fs|r)(?:-[a-z0-9]+)*)\s*[,)]/g)) {
        benutzt.add(m[1])
      }
    }
    expect(benutzt.size, 'keine Token-Verweise gefunden — greift der Ausdruck noch?').toBeGreaterThan(20)
    expect([...benutzt].filter((t) => !definiert.has(t)).sort(), 'Verweis auf ein Token, das tokens.css nicht kennt:').toEqual([])
  })

  it('kein fester Radius und kein festes uppercase in Inline-Stilen (TS/TSX)', () => {
    const tsx = alle(join(wurzel, 'src'), /\.tsx?$/).filter((f) => !/\.test\.tsx?$/.test(f))
    expect(tsx.length).toBeGreaterThan(50)
    const fest = tsx.flatMap((datei) => ohneKommentare(readFileSync(datei, 'utf8')).split('\n').flatMap((zeile, i) => {
      const treffer: string[] = []
      for (const m of zeile.matchAll(/\b(borderRadius|letterSpacing)\s*:\s*([^,}]+)/g)) {
        const wert = m[2].trim()
        if (wert !== '0' && !/^'var\(--[a-z0-9-]+\)'$/.test(wert)) treffer.push(`${kurz(datei)}:${i + 1}  ${m[1]}: ${wert}`)
      }
      if (/textTransform\s*:\s*['"`]uppercase['"`]/.test(zeile)) treffer.push(`${kurz(datei)}:${i + 1}  textTransform: 'uppercase'`)
      return treffer
    }))
    expect(fest, 'Fester Wert im Inline-Stil — bitte ein Token als String: borderRadius: \'var(--ecke-8)\'').toEqual([])
  })
})
