import { readFileSync, readdirSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

/**
 * Jeder Löschweg der API hat auch einen Weg in der Oberfläche.
 *
 * <b>Der Anlass (25.08.2026).</b> Nach der Kritik „CRUD ist grundlegend und
 * das befolgst du nicht" bekamen neun Controller einen Löschweg. Sieben davon
 * hatte danach **kein Knopf** — die Funktion war gebaut und niemand konnte sie
 * erreichen. Der Prüfer hat es aufgeschrieben und ein Beispiel genannt, das
 * die Sache auf den Punkt bringt: die Wächter-Meldung „Das ist der einzige
 * Lichtplan dieses Zelts" konnte kein Nutzer je zu sehen bekommen.
 *
 * <b>Dieselbe Klasse wie zweimal vorher.</b> Die Einkaufsliste (beta.42) und
 * das Messprotokoll waren fertig gebaut und unauffindbar. Dagegen steht
 * `menue-vollstaendig.node.test.ts` — diese Zählung hier ist ihr Gegenstück
 * für Aktionen statt für Seiten.
 *
 * <b>Eine Erwähnung ist keine Verwendung.</b> Gesucht wird nach einem echten
 * Aufruf mit `method: 'DELETE'`, und Kommentarzeilen werden vorher entfernt —
 * sonst belegt ein Kommentar, der einen Weg nennt, sich selbst.
 */
describe('Löschwege sind erreichbar', () => {
  const backendOrdner = new URL('../../GrowDiary.Web/Api/Controllers/', import.meta.url)

  /**
   * Bewusst ohne Knopf — mit ausgeschriebenem Grund.
   *
   * Ein Eintrag ohne Grund ist keine Ausnahme, sondern eine Lücke mit Deckel.
   */
  const gewollteAusnahmen = new Map<string, string>([
    ['api/error', 'Kein echter Weg: der Fehler-Controller dient nur dem Vertragstest der API-Fehlerform.'],
    ['api/auto-measurements/configs',
      'Die Seite /regeln?tab=automatik bietet zwei feste Vorlagen mit An/Aus. '
      + 'Ausschalten IST dort der Weg zurueck; ein Loeschknopf wuerde eine Zeile '
      + 'entfernen, die die Seite beim naechsten Einschalten neu anlegt. Den '
      + 'Loeschweg gibt es fuer Vorlagen, die ueber die API entstanden sind.'],
  ])

  /** Alle DELETE-Wege, die das Backend anbietet. */
  function loeschwege(): { datei: string, weg: string }[] {
    const gefunden: { datei: string, weg: string }[] = []
    for (const datei of readdirSync(backendOrdner)) {
      if (!datei.endsWith('.cs')) continue
      const text = readFileSync(new URL(datei, backendOrdner), 'utf8')
      const basis = /\[Route\("([^"]+)"\)\]/.exec(text)?.[1]
      if (!basis) continue
      for (const treffer of text.matchAll(/\[HttpDelete(?:\("([^"]*)"\))?\]/g)) {
        const vorlage = treffer[1] ?? ''
        gefunden.push({ datei, weg: vorlage ? `${basis}/${vorlage}` : basis })
      }
    }
    return gefunden
  }

  /** Der Quelltext der Oberfläche, OHNE Kommentare. */
  function oberflaeche(): string {
    const teile: string[] = []
    const sammeln = (ordner: URL) => {
      for (const eintrag of readdirSync(ordner, { withFileTypes: true })) {
        const pfad = new URL(eintrag.name + (eintrag.isDirectory() ? '/' : ''), ordner)
        if (eintrag.isDirectory()) { sammeln(pfad); continue }
        if (!/\.(ts|tsx)$/.test(eintrag.name)) continue
        if (/\.(test|spec)\./.test(eintrag.name)) continue
        teile.push(readFileSync(pfad, 'utf8'))
      }
    }
    sammeln(new URL('./', import.meta.url))
    return teile.join('\n')
      .replace(/\/\*[\s\S]*?\*\//g, ' ')
      .split('\n')
      .filter((zeile) => !zeile.trimStart().startsWith('//'))
      .join('\n')
  }

  /**
   * Die Wege, die die Oberfläche wirklich löscht.
   *
   * <b>Nicht am Namen des Aufrufers festmachen.</b> Die erste Fassung suchte
   * `apiFetch(<weg>, { … method: 'DELETE' })` und meldete zwei Wege als
   * fehlend, die es längst gab: `HydroPage` benutzt das nackte `fetch`, und in
   * `SetpointProfilesPage` steht im Pfad-Ausdruck ein Anführungszeichen
   * (`.replace('custom:', '')`), an dem die Zeichenkette vorzeitig endete.
   * Eine Zählung, die Falsches meldet, führt zu einem zweiten Knopf neben dem
   * vorhandenen.
   *
   * Deshalb umgekehrt: erst jedes `method: 'DELETE'` suchen, dann im Stück
   * davor den letzten `/api/…`-Pfad lesen. Eingesetzte Ausdrücke werden vorher
   * durch `:id` ersetzt, damit Anführungszeichen darin nicht stören.
   */
  function geloeschteWege(quelle: string): Set<string> {
    const wege = new Set<string>()
    for (const treffer of quelle.matchAll(/method:\s*'DELETE'/g)) {
      const fenster = quelle
        .slice(Math.max(0, (treffer.index ?? 0) - 300), treffer.index)
        .replace(/\$\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}/g, ':id')
      const letzter = [...fenster.matchAll(/\/api\/[A-Za-z0-9\-_:/]+/g)].at(-1)?.[0]
      if (letzter) wege.add(letzter.replace(/^\//, '').replace(/\/$/, ''))
    }
    return wege
  }

  /**
   * Passt ein Backend-Weg zu einem, den die Oberfläche aufruft?
   *
   * <b>Nur der Backend-Platzhalter ist ein Joker.</b> Die erste Fassung liess
   * auch ein `:id` auf der Oberflächenseite jedes feste Stück treffen — und
   * ein zusammengesetzter Pfad wie `` `/api/${pfad}/${id}` `` wurde zu
   * `api/:id/:id` und deckte damit JEDEN dreiteiligen Weg ab, auch
   * `api/setups/{id}`. Die Zählung meldete den Setup-Löschweg als erreichbar,
   * obwohl es dafür keinen Knopf gab. Ein Fehlbefund in diese Richtung ist
   * schlimmer als gar keine Zählung.
   */
  function wirdAufgerufen(backendWeg: string, aufrufe: Set<string>): boolean {
    const teile = (weg: string) => weg.split('/').filter(Boolean)
    const soll = teile(backendWeg)
    for (const aufruf of aufrufe) {
      const ist = teile(aufruf)
      if (ist.length !== soll.length) continue
      const passt = soll.every((stueck, i) => stueck.startsWith('{') || stueck === ist[i])
      if (passt) return true
    }
    return false
  }

  it('sieht ihre Grundmenge', () => {
    // Ohne diesen Wächter liefe die Zählung bei leerer Menge null Mal durch
    // und wäre grün — die Falle, die in CLAUDE.md ausgeschrieben steht.
    const wege = loeschwege()
    expect(wege.length, `Nur ${wege.length} DELETE-Wege gefunden — die Zählung läuft ins Leere.`)
      .toBeGreaterThanOrEqual(18)

    const aufrufe = geloeschteWege(oberflaeche())
    expect(aufrufe.size, 'Kein einziger DELETE-Aufruf in der Oberfläche gefunden — die Erkennung greift nicht.')
      .toBeGreaterThanOrEqual(8)
  })

  it('jeder Löschweg hat einen Knopf', () => {
    const aufrufe = geloeschteWege(oberflaeche())
    const ohneKnopf = loeschwege()
      .filter(({ weg }) => !wirdAufgerufen(weg, aufrufe))
      .filter(({ weg }) => ![...gewollteAusnahmen.keys()].some((a) => weg.startsWith(a)))
      .map(({ datei, weg }) => `${weg}  (${datei})`)

    expect(ohneKnopf,
      `${ohneKnopf.length} Löschwege gibt es nur in der API — niemand kann sie erreichen:\n  `
      + `${ohneKnopf.join('\n  ')}\n\n`
      + 'Entweder einen Knopf dazu, oder eine Ausnahme MIT Grund.')
      .toEqual([])
  })

  it('keine Ausnahme zeigt ins Leere', () => {
    const wege = loeschwege().map((x) => x.weg)
    for (const [ausnahme, grund] of gewollteAusnahmen) {
      expect(wege.some((w) => w.startsWith(ausnahme)),
        `Ausnahme für '${ausnahme}' — diesen Löschweg gibt es nicht (mehr).`).toBe(true)
      expect(grund.length, `Der Grund für '${ausnahme}' ist zu kurz, um einer zu sein.`)
        .toBeGreaterThanOrEqual(40)
    }
  })
})
