/* src/changelog-kein-cc.node.test.ts
   Das Kürzel „CC" steht in keinem Release-Eintrag.

   <b>Der Anlass (01.11.2026).</b> Die Regel „CC darf in nichts stehen, was Anwender zu sehen bekommen" gilt seit dem 31.10.
   (Gedächtnis `feedback-kein-cc-in-oberflaeche`). Der Eintrag cc.294 trug es trotzdem zweimal — die Regel stand nur im
   Gedächtnis der Sitzungen, und wer sie noch nicht geladen hatte, schrieb „der CC". Home Assistant zeigt genau diesen Text beim
   Update. Eine Regel, die niemand misst, wird beim nächsten Eintrag wieder gebrochen.

   <b>Was gemessen wird.</b> Nur der NEUESTE Eintrag (wie bei `release-notes-deutsch`): ältere sind Geschichte. Die technische
   Versionsnummer `2.0.0-cc.N` ist erlaubt, sie steht klein geschrieben und ist kein „CC" als eigenes Wort.
*/

import { readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const WURZEL = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..')

function neuesterEintrag(): { kopf: string; text: string } {
  const datei = readFileSync(join(WURZEL, 'grow-os', 'CHANGELOG.md'), 'utf8')
  const teile = datei.split(/^## /m).slice(1)
  const eintrag = teile[0] ?? ''
  return { kopf: eintrag.split('\n')[0], text: eintrag }
}

/** Das Kürzel als eigenes Wort: „CC", nicht „cc.294" und nicht „CC-BY". */
const KUERZEL = /(?<![\w.-])CC(?![\w-])/g

describe('Changelog ohne das Kürzel CC', () => {
  it('der neueste Eintrag nennt das Kürzel nirgends', () => {
    const { kopf, text } = neuesterEintrag()
    expect(kopf, 'Der Changelog muss mit einem Eintrag beginnen.').toMatch(/^2\.0\.0-cc\.\d+/)
    const treffer = [...text.matchAll(KUERZEL)].map((m) => text.slice(Math.max(0, (m.index ?? 0) - 40), (m.index ?? 0) + 30).replace(/\n/g, ' '))
    expect(treffer, `In „${kopf}" steht das Kürzel CC (für Anwender „Cultivate Control" oder „die App" schreiben): ${treffer.join(' | ')}`).toEqual([])
  })

  it('die Prüfung erkennt das Kürzel (Selbsttest)', () => {
    expect('Beim Start zieht der CC den Bestand nach.'.match(KUERZEL)).toHaveLength(1)
    expect('Version 2.0.0-cc.294 und CC-BY-4.0 und cc.294'.match(KUERZEL)).toBeNull()
    expect('(CC)'.match(KUERZEL)).toHaveLength(1)
  })
})
