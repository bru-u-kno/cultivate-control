import { describe, expect, it } from 'vitest'
import {
  entwurfAus, ergebnisText, etappeEinfuegen, etappeEntfernen, etappenAus, fehler, feldSetzen, istGeaendert, laeuftText, speichernAnfrage, summeTage,
  type Trocknung, type TrockenEtappe, vorlageHinweis,
} from './trocknung-plan'

const e = (name: string, dauer: number, temp = 19, ziel = 58, max = 65): TrockenEtappe =>
  ({ name, dauerTage: dauer, lufttemperaturC: temp, luftfeuchteZiel: ziel, luftfeuchteMax: max })

const STANDARD = [e('Anfang', 3, 20, 60), e('Haupt', 4), e('Ausklang', 3, 18, 55)]

describe('Trocknung: Entwurf', () => {
  it('zeigt Zahlen deutsch, ohne Tausenderpunkt, und liest sie zurück', () => {
    const entwurf = entwurfAus([e('A', 3, 19.5, 57.5)])

    expect(entwurf[0]).toEqual({ name: 'A', dauer: '3', temp: '19,5', ziel: '57,5', max: '65' })
    expect(etappenAus(entwurf)).toEqual([e('A', 3, 19.5, 57.5)])
  })

  it('liefert nichts, solange ein Feld keine Zahl ist — und nimmt das Komma beim Tippen als Zwischenstand', () => {
    const e1 = entwurfAus([e('A', 3)])

    expect(etappenAus(feldSetzen(e1, 0, 'temp', 'abc'))).toBeNull()
    expect(etappenAus(feldSetzen(e1, 0, 'temp', ''))).toBeNull()
    expect(etappenAus(feldSetzen(e1, 0, 'temp', '18,5'))?.[0].lufttemperaturC).toBe(18.5)
  })

  it('schneidet den Namen ab, ohne den Entwurf zu verändern', () => {
    const entwurf = feldSetzen(entwurfAus([e('A', 3)]), 0, 'name', '  Meine  ')

    expect(etappenAus(entwurf)?.[0].name).toBe('Meine')
    expect(entwurf[0].name).toBe('  Meine  ')
  })

  it('zählt ein nur angetipptes Feld nicht als Änderung', () => {
    const entwurf = entwurfAus(STANDARD)

    expect(istGeaendert(entwurf, STANDARD)).toBe(false)
    expect(istGeaendert(feldSetzen(entwurf, 1, 'temp', '19,0'), STANDARD)).toBe(false) // derselbe Wert, andere Schreibweise
    expect(istGeaendert(feldSetzen(entwurf, 1, 'temp', '17'), STANDARD)).toBe(true)
    expect(istGeaendert(feldSetzen(entwurf, 0, 'name', 'Neu'), STANDARD)).toBe(true)
    expect(istGeaendert(etappeEntfernen(entwurf, 2), STANDARD)).toBe(true)
    expect(istGeaendert(feldSetzen(entwurf, 0, 'dauer', 'x'), STANDARD)).toBe(true) // unlesbar ist eine Änderung — `fehler` meldet sie
  })

  it('summiert die Tage und zählt Unlesbares als null', () => {
    expect(summeTage(entwurfAus(STANDARD))).toBe(10)
    expect(summeTage(feldSetzen(entwurfAus(STANDARD), 0, 'dauer', 'x'))).toBe(7)
  })
})

describe('Trocknung: Etappen anlegen und entfernen', () => {
  it('fügt eine neue Etappe HINTER der gewählten ein, mit deren Werten und einem eigenen Namen', () => {
    const neu = etappeEinfuegen(entwurfAus(STANDARD), 0)

    expect(neu).toHaveLength(4)
    expect(neu[1].name).toBe('Etappe 4')
    expect({ ...neu[1], name: '' }).toEqual({ ...entwurfAus(STANDARD)[0], name: '' })
    expect(neu.map((x) => x.name)).toEqual(['Anfang', 'Etappe 4', 'Haupt', 'Ausklang'])
  })

  it('fängt aus einer leeren Liste mit einer gültigen Etappe an', () => {
    const neu = etappeEinfuegen([], -1)

    expect(neu).toHaveLength(1)
    expect(fehler(neu, 21, 8)).toEqual([])
  })

  it('entfernt die gewählte Etappe, aber nie die letzte', () => {
    expect(etappeEntfernen(entwurfAus(STANDARD), 1).map((x) => x.name)).toEqual(['Anfang', 'Ausklang'])
    expect(etappeEntfernen(entwurfAus([e('Allein', 5)]), 0)).toHaveLength(1)
  })

  it('verändert den Entwurf nicht, aus dem es baut', () => {
    const entwurf = entwurfAus(STANDARD)
    etappeEinfuegen(entwurf, 1)
    etappeEntfernen(entwurf, 0)
    feldSetzen(entwurf, 0, 'name', 'X')

    expect(entwurf.map((x) => x.name)).toEqual(['Anfang', 'Haupt', 'Ausklang'])
  })
})

describe('Trocknung: was die Oberfläche selbst sagen kann', () => {
  it('lässt eine gültige Folge durch', () => {
    expect(fehler(entwurfAus(STANDARD), 21, 8)).toEqual([])
  })

  it('meldet Pflichtfelder, Unlesbares und halbe Tage mit Etappe und Feld', () => {
    const entwurf = [
      { name: ' ', dauer: '3', temp: '19', ziel: '58', max: '65' },
      { name: 'B', dauer: '2,5', temp: 'abc', ziel: '', max: '65' },
    ]

    const saetze = fehler(entwurf, 21, 8)

    expect(saetze).toContain('Etappe 1: Der Name fehlt.')
    expect(saetze).toContain('Etappe 2: Die Dauer zählt in ganzen Tagen.')
    expect(saetze).toContain('Etappe 2: „abc“ ist bei Temperatur keine Zahl.')
    expect(saetze).toContain('Etappe 2: Luftfeuchte fehlt.')
  })

  it('meldet zu viele Tage und zu viele Etappen mit den Grenzen aus dem Backend', () => {
    const lang = entwurfAus([e('a', 12), e('b', 12)])
    expect(fehler(lang, 21, 8)).toContain('Zusammen 24 Tage — das Trocknungsfenster endet 21 Tage nach der Ernte.')

    const viele = entwurfAus(Array.from({ length: 9 }, (_, i) => e(`e${i}`, 1)))
    expect(fehler(viele, 21, 8)).toContain('Höchstens 8 Etappen.')
  })

  it('nimmt genau 21 Tage an und meldet 22 — die Grenze des Backends', () => {
    expect(fehler(entwurfAus([e('a', 7), e('b', 7), e('c', 7)]), 21, 8)).toEqual([])
    expect(fehler(entwurfAus([e('a', 7), e('b', 7), e('c', 8)]), 21, 8)).toEqual([
      'Zusammen 22 Tage — das Trocknungsfenster endet 21 Tage nach der Ernte.',
    ])
  })

  it('nimmt genau 8 Etappen an und meldet 9', () => {
    expect(fehler(entwurfAus(Array.from({ length: 8 }, (_, i) => e(`e${i}`, 1))), 21, 8)).toEqual([])
    expect(fehler(entwurfAus(Array.from({ length: 9 }, (_, i) => e(`e${i}`, 1))), 21, 8)).toEqual(['Höchstens 8 Etappen.'])
  })

  it('meldet eine leere Liste', () => {
    expect(fehler([], 21, 8)).toContain('Mindestens eine Etappe muss bleiben.')
  })
})

describe('Trocknung: Zeile „trocknet gerade“', () => {
  it('nennt Zelt, Tag und Etappe — und sagt, wenn der Plan abgelaufen ist', () => {
    const l = { zelt: 'Trockenzelt', tag: 4, etappeNr: 2, etappeName: 'Hauptphase', gehalten: false }

    expect(laeuftText(l)).toBe('Trocknet gerade: Trockenzelt · Tag 4 · Etappe 2 „Hauptphase“')
    expect(laeuftText({ ...l, tag: 15, gehalten: true })).toContain('die letzte Etappe gilt weiter')
  })
})

describe('Trocknung: Speichern', () => {
  const dto = (name: string): Trocknung => ({
    programmId: 'eigen-x', programmName: name, eigen: true, istStandard: false, maxTage: 21, maxEtappen: 8,
    etappen: [], standard: [], laeuft: [],
  })

  it('berichtet, ob eine Kopie entstand', () => {
    expect(ergebnisText({ trocknung: dto('Meins'), neuAngelegt: true }))
      .toBe('Trocknung gespeichert — als neues eigenes Programm „Meins“. Die Vorlage bleibt unverändert.')
    expect(ergebnisText({ trocknung: dto('Meins'), neuAngelegt: false })).toBe('Trocknung gespeichert in „Meins“.')
  })

  it('schickt den Namen nur mit, wenn eine Kopie entsteht und einer eingetragen ist', () => {
    const liste = [e('A', 3)]
    expect(speichernAnfrage(liste, false, '  Meins  ')).toEqual({ etappen: liste, name: 'Meins' })
    expect(speichernAnfrage(liste, false, '   ')).toEqual({ etappen: liste })
    // Ein eigenes Programm behält seinen Namen — auch wenn im Feld noch Text stand.
    expect(speichernAnfrage(liste, true, 'Meins')).toEqual({ etappen: liste })
  })
})

describe('Trocknung: Hinweis bei einer Vorlage, nach der gerade getrocknet wird', () => {
  const laeuft = [{ zelt: 'Trockenzelt', tag: 4, etappeNr: 2, etappeName: 'Haupt', gehalten: false }]

  it('warnt nur bei einer Vorlage, nach der ein Zelt trocknet', () => {
    expect(vorlageHinweis({ eigen: false, laeuft })).toContain('Das Zelt „Trockenzelt“ trocknet gerade nach dieser Vorlage.')
    expect(vorlageHinweis({ eigen: true, laeuft })).toBeNull()        // ein eigenes wird an Ort und Stelle geändert
    expect(vorlageHinweis({ eigen: false, laeuft: [] })).toBeNull()   // niemand trocknet danach
  })

  it('nennt mehrere Zelte', () => {
    const zwei = [...laeuft, { ...laeuft[0], zelt: 'Zelt 2' }]
    expect(vorlageHinweis({ eigen: false, laeuft: zwei })).toContain('„Trockenzelt“ und „Zelt 2“')
  })
})
