import { describe, expect, it } from 'vitest'
import { brauchenEntscheidung, ergebnisText, growZeile, zeltZeile, type ZuweisungGrow } from './plan-zuweisung'
import type { TrocknungsplanZelt } from './trocknung-plan'

const grow = (teil: Partial<ZuweisungGrow> = {}): ZuweisungGrow => ({
  growId: 3, name: 'Grow 3', zelt: 'Zelt A', status: 'Running', aktuellesProgramm: 'Canna Aqua',
  zugewiesen: false, hatPlan: true, eigeneAenderungen: 0, ...teil,
})

describe('growZeile', () => {
  it('nennt Zelt, Zustand und was heute gilt — und zeigt vor dem Ankreuzen nichts Ersetztes', () => {
    const z = growZeile(grow(), false)
    expect(z.unter).toBe('Zelt A · läuft · heute: Canna Aqua')
    expect(z.zusatz).toBeUndefined()
    expect(z.angehakt).toBe(false)
  })

  it('sagt beim Ankreuzen, was ersetzt wird und dass die Wochenwerte wechseln', () => {
    expect(growZeile(grow(), true).zusatz).toBe('Ersetzt: Canna Aqua · Wochenwerte wechseln')
  })

  it('nennt eigene Änderungen am Plan in der richtigen Zahl', () => {
    expect(growZeile(grow({ eigeneAenderungen: 1 }), true).zusatz).toContain('1 eigene Änderung am Plan')
    expect(growZeile(grow({ eigeneAenderungen: 3 }), true).zusatz).toContain('3 eigene Änderungen am Plan')
  })

  it('ein geplanter Grow ohne Programm und ohne Plan hat nichts zu ersetzen', () => {
    const z = growZeile(grow({ status: 'Planning', aktuellesProgramm: null, hatPlan: false, zelt: null }), true)
    expect(z.unter).toBe('kein Zelt · geplant · heute: kein Programm')
    expect(z.zusatz).toBeUndefined()
  })

  it('ein schon zugewiesener Grow ist angekreuzt und gesperrt', () => {
    const z = growZeile(grow({ zugewiesen: true }), false)
    expect(z).toMatchObject({ angehakt: true, gesperrt: true, zusatz: 'Schon zugewiesen', zusatzTon: 'ok' })
  })
})

describe('brauchenEntscheidung', () => {
  const liste = [
    grow({ growId: 1, eigeneAenderungen: 2 }),
    grow({ growId: 2, eigeneAenderungen: 0 }),
    grow({ growId: 3, hatPlan: false, eigeneAenderungen: 4 }),
    grow({ growId: 4, eigeneAenderungen: 5, zugewiesen: true }),
  ]

  it('fragt nur bei angekreuzten Grows mit Plan, eigenen Änderungen und anderem Programm', () => {
    expect(brauchenEntscheidung(liste, new Set([1, 2, 3, 4])).map((g) => g.growId)).toEqual([1])
  })

  it('fragt nicht, wenn nichts angekreuzt ist', () => {
    expect(brauchenEntscheidung(liste, new Set())).toEqual([])
  })
})

describe('ergebnisText', () => {
  it('nennt, wer umgestellt wurde', () => {
    const r = ergebnisText([{ growId: 1, name: 'Grow 1', umgestellt: true, entfallen: 0, hinweis: null }])
    expect(r).toEqual({ text: 'Zugewiesen: Grow 1.', ton: 'ok' })
  })

  it('zählt entfallene Änderungen zusammen und meldet einen Grow, der nicht ging', () => {
    const r = ergebnisText([
      { growId: 1, name: 'Grow 1', umgestellt: true, entfallen: 2, hinweis: null },
      { growId: 2, name: 'Grow 2', umgestellt: true, entfallen: 1, hinweis: null },
      { growId: 3, name: 'Grow 3', umgestellt: false, entfallen: 0, hinweis: 'Der Grow ist abgeschlossen.' },
    ])
    expect(r.ton).toBe('warn')
    expect(r.text).toContain('Zugewiesen: Grow 1, Grow 2.')
    expect(r.text).toContain('3 eigene Änderungen am Plan entfielen')
    expect(r.text).toContain('Grow 3: Der Grow ist abgeschlossen.')
  })

  it('„Hat dieses Programm schon“ ist kein Fehler', () => {
    const r = ergebnisText([{ growId: 1, name: 'Grow 1', umgestellt: false, entfallen: 0, hinweis: 'Hat dieses Programm schon.' }])
    expect(r.ton).toBe('ok')
  })
})

describe('zeltZeile', () => {
  const plan = (teil: Partial<TrocknungsplanZelt> = {}): TrocknungsplanZelt => ({
    zeltId: 2, zeltName: 'Trockenzelt 2', istTrockenzelt: true, istStandard: true, maxTage: 21, maxEtappen: 6,
    etappen: [], standard: [], laeuft: null, ...teil,
  })

  it('das Zelt, an dem man gerade arbeitet, ist angekreuzt und gesperrt', () => {
    expect(zeltZeile({ id: 1, name: 'Zelt-Trocknung', plan: null }, true, true)).toMatchObject({ angehakt: true, gesperrt: true })
  })

  it('nennt, ob eine Charge trocknet und ob das Zelt einen eigenen Plan hat', () => {
    const z = zeltZeile({ id: 2, name: 'Trockenzelt 2', plan: plan({ istStandard: false, laeuft: { zelt: 'x', tag: 4, etappeNr: 1, etappeName: 'A', gehalten: false } }) }, true, false)
    expect(z.unter).toBe('Charge trocknet · Tag 4 · heute: eigener Plan')
    expect(z.zusatz).toBe('Ersetzt den eigenen Plan dieses Zelts')
  })

  it('ein Zelt mit Standardplan hat nichts zu ersetzen', () => {
    const z = zeltZeile({ id: 2, name: 'Trockenzelt 2', plan: plan() }, true, false)
    expect(z.unter).toBe('keine Charge · heute: Standard „langsam“')
    expect(z.zusatz).toBeUndefined()
  })

  it('ein Zelt, dessen Stand nicht lesbar war, sagt das', () => {
    expect(zeltZeile({ id: 2, name: 'Trockenzelt 2', plan: null }, false, false).unter).toBe('Stand nicht lesbar')
  })
})
