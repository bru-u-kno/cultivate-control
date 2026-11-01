import { useEffect, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1Skeleton, V1Switch } from '../../components/v1'
import { KlappBereich } from '../../components/Klapp'
import type { LichtKalibrierung, LichtPlanStand } from './steuerung-typen'
import { zahlAusFeld } from './feld-fehler'
import './steuerung.css'

const URL = '/api/steuerung/licht/plan'

const zahl = (wert: number | null | undefined) => (wert == null ? '–' : wert.toLocaleString('de-DE', { maximumFractionDigits: 0 }))

/**
 * Steuerung › Licht › Lichtplan (unter den Zeiten): welche Phase gilt, was der Plan je Woche an Licht verlangt, und welche
 * Lampenstufe dazu passt.
 *
 * Die Phase kommt allein aus dem Grow-Plan, es gibt keinen Schalter am Zelt. Der Stufenvorschlag ist ein Vorschlag; die
 * Automatik, die ihn setzt, ist aus, bis sie jemand einschaltet, und weicht jeder Handstellung bis zur nächsten Planwoche.
 */
export default function LichtPlan() {
  const [stand, setStand] = useState<LichtPlanStand | null>(null)
  const [entwurf, setEntwurf] = useState<LichtKalibrierung | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    void (async () => {
      try {
        const geladen = await apiFetch<LichtPlanStand>(URL, { signal: controller.signal })
        if (controller.signal.aborted) return
        setStand(geladen)
        setEntwurf(geladen.kalibrierung)
      } catch (caught) {
        if (!controller.signal.aborted) setFehler(formatApiError(caught, 'Der Lichtplan konnte nicht geladen werden.'))
      }
    })()
    return () => controller.abort()
  }, [])

  const speichern = async (neu: LichtKalibrierung) => {
    setArbeitet(true)
    try {
      const gespeichert = await apiFetch<LichtPlanStand>('/api/steuerung/licht/kalibrierung', { method: 'PUT', body: JSON.stringify(neu) })
      setStand(gespeichert)
      setEntwurf(gespeichert.kalibrierung)
      setFehler(null)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Die Kalibrierung konnte nicht gespeichert werden.'))
    } finally {
      setArbeitet(false)
    }
  }

  if (!stand || !entwurf) return fehler ? <V1Alert tone="critical" message={fehler} /> : <V1Skeleton />

  const geaendert = JSON.stringify(entwurf) !== JSON.stringify(stand.kalibrierung)
  const setz = (teil: Partial<LichtKalibrierung>) => setEntwurf({ ...entwurf, ...teil })
  const messpunkt = (i: number, teil: Partial<{ stufe: number; ppfd: number }>) =>
    setz({ messpunkte: entwurf.messpunkte.map((m, j) => (j === i ? { ...m, ...teil } : m)) })
  const phaseText = stand.phase === 'bluete' ? 'Blüte' : stand.phase === 'veggie' ? 'Vegetativ' : null

  return (
    <>
      {fehler && <V1Alert tone="critical" message={fehler} />}

      <KlappBereich
        titel="Lichtleistung im Plan"
        kurz={phaseText ? `Phase ${phaseText} · ${stand.programm ?? 'ohne Programm'}` : 'kein Grow läuft'}
        offen
        merken="licht-plan-wochen"
      >
        <V1Card>
          <p className="st-hinweis">
            {phaseText
              ? <>Aus dem Grow-Plan gilt jetzt die Phase <b>{phaseText}</b> — die Zeiten oben für diese Phase prüft der Wächter.</>
              : 'Es läuft gerade kein Grow mit Plan; deshalb gibt es keine Phase und der Wächter prüft nichts.'}
          </p>
          {stand.wochen.length === 0 ? (
            <p className="st-hinweis">Der Plan dieses Grows hat keine Wochen mit Lichtwert.</p>
          ) : (
            <table className="st-plantabelle">
              <thead>
                <tr><th>Woche</th><th>Plan µmol/m²s</th><th>Stufe</th></tr>
              </thead>
              <tbody>
                {stand.wochen.map((w) => (
                  <tr key={w.id} className={w.jetzt ? 'is-jetzt' : undefined}>
                    <td>{w.label}{w.jetzt ? ' · jetzt' : ''}</td>
                    <td>{zahl(w.ppfd)}</td>
                    <td>{w.vorschlagStufe == null ? '–' : `${w.vorschlagStufe}\u00a0(≈\u00a0${zahl(w.erreicht)})`}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {!stand.kalibriert && stand.wochen.length > 0 && (
            <p className="st-hinweis">Ohne Kalibrierung gibt es keinen Stufenvorschlag — trage unten die Herstellerangabe oder Messpunkte ein.</p>
          )}
        </V1Card>
      </KlappBereich>

      <KlappBereich
        titel="Kalibrierung der Lichtleistung"
        kurz={stand.kalibriert ? `Stufe 1 bis 10 = ${zahl(stand.kurve[0]?.ppfd)} bis ${zahl(stand.kurve[9]?.ppfd)} µmol/m²s` : 'noch nicht kalibriert'}
        offen
        merken="licht-plan-kalibrierung"
      >
        <V1Card>
          <div className="st-feldzeile is-gestapelt">
            <span className="st-etikett">
              Herstellerangabe
              <small>Lichtwert bei der höchsten Stufe (10) und der Abstand dazu — steht im Datenblatt der Lampe.</small>
            </span>
            <span className="st-eingaben">
              <input
                type="number" inputMode="numeric" aria-label="Herstellerangabe Lichtwert" placeholder="1200"
                value={entwurf.herstellerPpfd ?? ''}
                onChange={(e) => setz({ herstellerPpfd: e.target.value === '' ? null : zahlAusFeld(e.target.value) })}
              />
              <span className="st-einheit">µmol/m²s bei</span>
              <input
                type="number" inputMode="numeric" aria-label="Abstand der Herstellerangabe" placeholder="30"
                value={entwurf.herstellerAbstandCm ?? ''}
                onChange={(e) => setz({ herstellerAbstandCm: e.target.value === '' ? null : zahlAusFeld(e.target.value) })}
              />
              <span className="st-einheit">cm</span>
            </span>
          </div>

          <div className="st-feldzeile is-gestapelt">
            <span className="st-etikett">
              Eigene Messpunkte
              <small>Mit dem Messgerät auf Pflanzenhöhe: eine Stufe einstellen, den Wert eintragen — zum Beispiel Stufe 5 und Stufe 3. Mehr Punkte machen die Kurve genauer.</small>
            </span>
            <span className="st-eingaben st-messpunkte">
              {entwurf.messpunkte.map((m, i) => (
                <span key={i} className="st-messpunkt">
                  <span className="st-einheit">Stufe</span>
                  <input
                    type="number" inputMode="numeric" min={1} max={10} aria-label={`Messpunkt ${i + 1} Stufe`}
                    value={m.stufe}
                    onChange={(e) => messpunkt(i, { stufe: zahlAusFeld(e.target.value) ?? 0 })}
                  />
                  <input
                    type="number" inputMode="numeric" aria-label={`Messpunkt ${i + 1} Lichtwert`}
                    value={m.ppfd || ''}
                    onChange={(e) => messpunkt(i, { ppfd: zahlAusFeld(e.target.value) ?? 0 })}
                  />
                  <span className="st-einheit">µmol/m²s</span>
                  <V1Button variant="ghost" aria-label={`Messpunkt ${i + 1} entfernen`} onClick={() => setz({ messpunkte: entwurf.messpunkte.filter((_, j) => j !== i) })}>−</V1Button>
                </span>
              ))}
              <V1Button
                variant="ghost"
                disabled={entwurf.messpunkte.length >= 10}
                onClick={() => setz({ messpunkte: [...entwurf.messpunkte, { stufe: 5, ppfd: 0 }] })}
              >
                + Messpunkt
              </V1Button>
            </span>
          </div>

          {geaendert && (
            <div className="st-eingaben">
              <V1Button variant="primary" disabled={arbeitet} onClick={() => void speichern(entwurf)}>{arbeitet ? 'Speichert …' : 'Kalibrierung speichern'}</V1Button>
              <V1Button variant="ghost" disabled={arbeitet} onClick={() => setEntwurf(stand.kalibrierung)}>Verwerfen</V1Button>
            </div>
          )}

          {stand.kalibriert && (
            <table className="st-plantabelle">
              <thead>
                <tr><th>Stufe</th><th>µmol/m²s</th><th>Herkunft</th></tr>
              </thead>
              <tbody>
                {stand.kurve.map((p) => (
                  <tr key={p.stufe}>
                    <td>{p.stufe}</td>
                    <td>{zahl(p.ppfd)}</td>
                    <td>{p.herkunft === 'gemessen' ? <b>gemessen</b> : p.herkunft}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <p className="st-hinweis">
            Zwischen den Punkten verbindet der Fork gerade; ohne Messung gilt nur die Herstellerangabe als grobe Schätzung, denn Lampen dimmen oft nicht linear.
            Mit wachsender Pflanze ändert sich der Abstand — die Messung lässt sich jederzeit wiederholen.
          </p>
        </V1Card>
      </KlappBereich>

      <KlappBereich
        titel="Stufe nach Plan"
        kurz={stand.kalibrierung.automatikStufe ? 'AN' : 'AUS'}
        offen
        merken="licht-plan-automatik"
      >
        <V1Card>
          <V1Switch
            label="Stufe automatisch nach Plan setzen"
            checked={stand.kalibrierung.automatikStufe}
            onChange={(an) => {
              // Nur auf gespeicherter Kalibrierung und nur mit Kurve einschalten; ausschalten geht immer.
              if (arbeitet || (an && (geaendert || !stand.kalibriert))) return
              void speichern({ ...stand.kalibrierung, automatikStufe: an })
            }}
            hint="Voreinstellung: aus. Eingeschaltet setzt der Fork die Stufe zu Beginn jeder neuen Planwoche (nicht sofort). Stellst du die Stufe selbst um, zum Beispiel gegen Wärme, wartet die Automatik bis zur nächsten Planwoche. Bei ausgeschaltetem Licht oder ruhendem Zelt passiert nichts."
          />
          <p className="st-hinweis">{stand.automatikHinweis}</p>
          {geaendert && <p className="st-hinweis">Erst die Kalibrierung speichern, dann die Automatik einschalten.</p>}
          {!stand.kalibriert && <p className="st-hinweis">Ohne Kalibrierung lässt sich die Automatik nicht einschalten.</p>}
        </V1Card>
      </KlappBereich>
    </>
  )
}
