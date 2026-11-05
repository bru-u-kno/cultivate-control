import { describe, expect, it } from 'vitest'
import { einbindenHinweis, erklaerung, kopfZeile, ohneEinbindungText, plakette, schalterVon, wuerdeSchreiben, type SteuerungGeraet } from './steuerung-text'

const g = (teil: Partial<SteuerungGeraet>): SteuerungGeraet => ({
  rolle: 'kuehlung', portName: 'Port K', folgt: true, modus: 'Auto', wertAmGeraet: 24, einheit: '°C', planWert: 21,
  urteil: 'weichtAb', vorher: null, zuletztGesetzt: null, ...teil,
})

describe('Trocknung: Steuerung', () => {
  it('Plakette: nicht eingebundene Geräte bleiben „nicht eingebunden“, sonst entscheidet der Zustand', () => {
    expect(plakette(g({ folgt: false }), 'melden')).toEqual({ text: 'nicht eingebunden', ton: 'grau' })
    expect(plakette(g({ folgt: false }), 'regeln').text).toBe('nicht eingebunden')
    expect(plakette(g({}), 'melden')).toEqual({ text: 'würde nachstellen', ton: 'warn' })
    expect(plakette(g({}), 'regeln')).toEqual({ text: 'stellt nach', ton: 'warn' })
    expect(plakette(g({ urteil: 'passt' }), 'regeln')).toEqual({ text: 'hält den Plan', ton: 'ok' })
    // In „Nur anzeigen“ schreibt die Steuerung nichts — dort „passt“ der Wert nur, er wird nicht „gehalten“.
    expect(plakette(g({ urteil: 'passt' }), 'melden')).toEqual({ text: 'passt zum Plan', ton: 'ok' })
    expect(plakette(g({ urteil: 'nichtZugeordnet' }), 'regeln').text).toBe('kein Port zugeordnet')
    expect(plakette(g({ urteil: 'unbekannt' }), 'regeln')).toEqual({ text: 'Wert nicht lesbar', ton: 'warn' })
    expect(plakette(g({ urteil: 'geraetAus' }), 'regeln').text).toBe('Port steht auf Aus')
    expect(plakette(g({ urteil: 'ohneAutomatik' }), 'regeln').text).toBe('Port nicht auf Auto')
    expect(plakette(g({ urteil: 'keinAusloeser' }), 'regeln').text).toBe('Auslöser fehlt')
    expect(plakette(g({ urteil: 'keinPlan' }), 'regeln')).toEqual({ text: 'kein Planwert heute', ton: 'grau' })
  })

  it('Die alten, schwer verständlichen Plaketten kommen nicht mehr vor', () => {
    const urteile = ['nichtZugeordnet', 'unbekannt', 'geraetAus', 'ohneAutomatik', 'keinAusloeser', 'keinPlan', 'passt', 'weichtAb'] as const
    const alle = urteile.flatMap((urteil) => (['melden', 'regeln'] as const).flatMap((b) => [true, false].map((folgt) => plakette(g({ urteil, folgt }), b).text)))
    for (const alt of ['wird nachgezogen', 'würde ändern', 'ohne Automatik', 'Gerät aus', 'eigener Wert', 'bleibt', 'folgt dem Plan', 'kein Plan']) {
      expect(alle).not.toContain(alt)
    }
  })

  it('Was geschrieben würde: nur Geräte, die folgen und abweichen', () => {
    const geraete = [g({ rolle: 'entfeuchter', wertAmGeraet: 50, planWert: 62 }), g({})]
    expect(wuerdeSchreiben(geraete, { entfeuchter: false, kuehlung: true })).toEqual([
      { rolle: 'kuehlung', portName: 'Port K', feld: 'Temperatur-Obergrenze', von: 24, nach: 21, einheit: '°C' },
    ])
    expect(wuerdeSchreiben(geraete, { entfeuchter: false, kuehlung: false })).toEqual([])
    expect(wuerdeSchreiben([g({ urteil: 'passt' })], { entfeuchter: false, kuehlung: true })).toEqual([])
  })

  it('Erklärung: nennt Zahlen, sagt was die Steuerung tut und dass sie den Modus nie ändert', () => {
    expect(erklaerung([g({ folgt: false })], 'regeln')).toContain('Dein Wert am Gerät bleibt')
    expect(erklaerung([g({})], 'regeln')).toBe('Am Gerät steht 24 °C, der Plan verlangt 21 °C. Die Steuerung stellt das Gerät jetzt auf 21 °C und prüft es alle 5 Minuten.')
    expect(erklaerung([g({})], 'melden')).toContain('Im Modus „Nur anzeigen“ ändert die Steuerung nichts')
    expect(erklaerung([g({ urteil: 'passt', wertAmGeraet: 21 })], 'regeln')).toBe('Am Gerät steht 21 °C — das ist der Planwert. Die Steuerung hält ihn dort.')
    expect(erklaerung([g({ urteil: 'passt', wertAmGeraet: 21 })], 'melden')).toBe('Am Gerät steht 21 °C — das ist der Planwert. Im Modus „Nur anzeigen“ stellt die Steuerung nichts nach.')
    expect(erklaerung([g({ modus: 'Off', urteil: 'geraetAus' })], 'regeln')).toContain('Stell den Port auf Auto')
    expect(erklaerung([g({ modus: 'Off', urteil: 'geraetAus' })], 'regeln')).toContain('nie selbst')
    expect(erklaerung([g({ urteil: 'keinAusloeser' })], 'regeln')).toContain('keinen Wert für diese Grenze')
    expect(erklaerung([g({ urteil: 'keinPlan' })], 'regeln')).toContain('für heute keinen Wert')
    expect(erklaerung([g({ urteil: 'nichtZugeordnet' })], 'regeln')).toContain('Ports bearbeiten')
  })

  it('Abluft auf On: Erklärung nennt den Modus und beide Planwerte', () => {
    const zeilen = [
      g({ rolle: 'abluft-feuchte', modus: 'On', urteil: 'ohneAutomatik', einheit: '%', wertAmGeraet: null, planWert: 60 }),
      g({ rolle: 'abluft-temp', modus: 'On', urteil: 'ohneAutomatik', wertAmGeraet: null, planWert: 18 }),
    ]
    const text = erklaerung(zeilen, 'regeln')
    expect(text).toContain('Der Port steht nicht auf Auto (aktuell: On)')
    expect(text).not.toContain('dauerhaft')
    expect(text).toContain('Luftfeuchte und Temperatur beim Planwert (60 % und 18 °C)')
  })

  it('Haken „Gerät einbinden“: der Hinweis sagt, was er bewirkt', () => {
    expect(einbindenHinweis('entfeuchter', true, 'melden')).toContain('Im Modus „Nur anzeigen“ zeigt die Steuerung, was sie einstellen würde')
    expect(einbindenHinweis('entfeuchter', true)).toBe('Eingebunden: Die Steuerung versucht, die Luftfeuchte auf den Planwert zu bringen und dort zu halten.')
    expect(einbindenHinweis('abluft', false)).toBe('Setze den Haken, damit die Steuerung Luftfeuchte und Temperatur auf den Planwert bringt und dort hält.')
  })

  it('Nicht eingebunden: ohne gemerkten Wert bleibt der Wert des Nutzers, mit Merker der zuletzt gesetzte — der alte kommt über „Alte Werte zurück“', () => {
    expect(ohneEinbindungText([g({ folgt: false })])).toBe('Die Steuerung fasst dieses Gerät nicht an. Dein Wert am Gerät bleibt, wie er ist.')
    const mit = ohneEinbindungText([g({ folgt: false, vorher: 28 })])
    expect(mit).toContain('Der zuletzt gesetzte Wert bleibt am Gerät')
    expect(mit).toContain('deinen alten Wert (28 °C) stellt „Alte Werte zurück“ wieder her')
    expect(mit).not.toContain('Dein Wert am Gerät bleibt')
  })

  it('Kopfzeile bei der Abluft: ein Problem der einen Grenze geht vor „passt“ der anderen', () => {
    const passt = g({ rolle: 'abluft-feuchte', urteil: 'passt' })
    const kaputt = g({ rolle: 'abluft-temp', urteil: 'keinAusloeser' })
    expect(kopfZeile([passt, kaputt])).toBe(kaputt)
    expect(plakette(kopfZeile([passt, kaputt]), 'regeln').text).toBe('Auslöser fehlt')
  })

  it('Kopfzeile: die abweichende Zeile bestimmt die Karte, sonst die erste', () => {
    const a = g({ rolle: 'abluft-feuchte', urteil: 'passt' })
    const b = g({ rolle: 'abluft-temp', urteil: 'weichtAb' })
    expect(kopfZeile([a, b])).toBe(b)
    expect(kopfZeile([a, g({ rolle: 'abluft-temp', urteil: 'passt' })])).toBe(a)
  })

  it('Abluft: ein Schalter für beide Grenzen, jede Zeile erscheint in der Rückfrage', () => {
    expect(schalterVon('abluft-feuchte')).toBe('abluft')
    expect(schalterVon('abluft-temp')).toBe('abluft')
    expect(schalterVon('kuehlung')).toBe('kuehlung')
    const geraete = [
      g({ rolle: 'abluft-feuchte', portName: 'Port A', wertAmGeraet: 55, planWert: 62, einheit: '%' }),
      g({ rolle: 'abluft-temp', portName: 'Port A', wertAmGeraet: 24, planWert: 21 }),
    ]
    expect(wuerdeSchreiben(geraete, { abluft: true })).toEqual([
      { rolle: 'abluft-feuchte', portName: 'Port A', feld: 'Luftfeuchte-Obergrenze', von: 55, nach: 62, einheit: '%' },
      { rolle: 'abluft-temp', portName: 'Port A', feld: 'Temperatur-Obergrenze', von: 24, nach: 21, einheit: '°C' },
    ])
    expect(wuerdeSchreiben(geraete, { entfeuchter: true, kuehlung: true })).toEqual([])
  })
})
