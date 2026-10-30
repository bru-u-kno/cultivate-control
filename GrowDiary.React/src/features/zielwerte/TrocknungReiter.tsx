import { useEffect, useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Badge, V1Button, V1Skeleton } from '../../components/v1'
import { classNames } from '../../utils'
import '../wochenplan/wochenplan.css'
import { NAME_MAX, kopieName, nameFehler, speichernText } from './programm-plan'
import {
  entwurfAus, ergebnisText, etappeEinfuegen, etappeEntfernen, etappenAus, fehler, feldSetzen, istGeaendert, laeuftText,
  speichernAnfrage, summeTage, vorlageHinweis,
  type EtappeEntwurf, type Trocknung, type TrocknungGespeichert,
} from './trocknung-plan'

/** Eine Zelle des Blatts: Beschriftung, Eingabe, darunter ein Satz. */
function Zelle({ titel, einheit, wert, audit, name, unter, onChange }: {
  titel: string
  einheit: string
  wert: string
  audit: string
  name: string
  unter?: string
  onChange: (text: string) => void
}) {
  return (
    <div className="wp-zelle">
      <div className="wp-zelle-n">
        {titel}
        <span className="wp-einheit"> {einheit}</span>
      </div>
      <div className="wp-eingaben">
        <span className="wp-eingabe-paar">
          <input
            className="wp-eingabe"
            type="text"
            inputMode="decimal"
            aria-label={`${name}: ${titel}`}
            data-audit={audit}
            value={wert}
            onChange={(e) => onChange(e.target.value)}
          />
        </span>
      </div>
      {unter && <div className="wp-plan">{unter}</div>}
    </div>
  )
}

/**
 * Fork AI (A-029, Etappe 2): die Trocknung eines Programms — frei editierbare Etappen mit Dauer,
 * Temperatur- und Feuchteziel.
 *
 * Bis dahin standen die Trocknungs-Werte fest im Code (18–20 °C, 55–60 %). Jetzt führt sie das
 * Programm, und die Live-Kacheln des trocknenden Zelts folgen ihr. Ohne eigene Etappen gilt der
 * Standard („langsam trocknen“). Eine mitgelieferte Vorlage bleibt unverändert: Speichern legt eine
 * Kopie als eigenes Programm an, wie bei den Wochenwerten.
 */
export function TrocknungReiter({ programmId, startMeldung, onKopie }: {
  programmId: string
  startMeldung?: string | null
  onKopie: (neueId: string, text: string) => void
}) {
  const [daten, setDaten] = useState<Trocknung | null>(null)
  const [laden, setLaden] = useState(true)
  const [entwurf, setEntwurf] = useState<EtappeEntwurf[]>([])
  const [index, setIndex] = useState(0)
  const [name, setName] = useState('')
  const [speichert, setSpeichert] = useState(false)
  const [meldung, setMeldung] = useState<{ text: string; ton: 'ok' | 'critical' | 'neutral' } | null>(
    startMeldung ? { text: startMeldung, ton: 'ok' } : null,
  )

  useEffect(() => {
    let aktiv = true
    async function holen() {
      try {
        const antwort = await apiFetch<Trocknung>(`/api/programme/${encodeURIComponent(programmId)}/trocknung`)
        if (!aktiv) return
        setDaten(antwort)
        setEntwurf(entwurfAus(antwort.etappen))
        // Mit der Etappe aufmachen, die gerade läuft — sonst mit der ersten.
        setIndex(Math.max(0, (antwort.laeuft[0]?.etappeNr ?? 1) - 1))
        setName(kopieName(antwort.programmName))
      } catch (caught) {
        if (aktiv) setMeldung({ ton: 'critical', text: formatApiError(caught, 'Die Trocknung konnte nicht geladen werden.') })
      } finally {
        if (aktiv) setLaden(false)
      }
    }
    void holen()
    return () => { aktiv = false }
  }, [programmId])

  const geaendert = useMemo(() => (daten ? istGeaendert(entwurf, daten.etappen) : false), [daten, entwurf])
  const probleme = useMemo(() => {
    if (!daten) return []
    const sammlung = fehler(entwurf, daten.maxTage, daten.maxEtappen)
    const nameProblem = !daten.eigen && geaendert ? nameFehler(name) : null
    return nameProblem ? [...sammlung, nameProblem] : sammlung
  }, [daten, entwurf, geaendert, name])

  if (laden) return <V1Skeleton rows={6} label="Lade Trocknung" />
  if (!daten) return meldung ? <V1Alert tone={meldung.ton} message={meldung.text} /> : null

  const aktuell = Math.min(index, Math.max(0, entwurf.length - 1))
  const etappe = entwurf[aktuell]
  const letzte = entwurf.length - 1
  const summe = summeTage(entwurf)
  const nr = `Etappe ${aktuell + 1}`
  const laeuftHier = daten.laeuft.find((l) => l.etappeNr - 1 === aktuell)
  const aendern = (feld: keyof EtappeEntwurf) => (text: string) => {
    setEntwurf((alt) => feldSetzen(alt, aktuell, feld, text))
    setMeldung(null)
  }

  async function speichern() {
    if (!daten) return
    const etappen = etappenAus(entwurf)
    if (etappen === null) return
    setSpeichert(true)
    setMeldung(null)
    try {
      const antwort = await apiFetch<TrocknungGespeichert>(`/api/programme/${encodeURIComponent(daten.programmId)}/trocknung`, {
        method: 'POST',
        body: JSON.stringify(speichernAnfrage(etappen, daten.eigen, name)),
      })
      const text = ergebnisText(antwort)
      if (antwort.neuAngelegt) {
        // Die Kopie ist jetzt das Programm, das man bearbeitet; die Vorlage bleibt, wie sie war.
        onKopie(antwort.trocknung.programmId, text)
      } else {
        setDaten(antwort.trocknung)
        setEntwurf(entwurfAus(antwort.trocknung.etappen))
        setMeldung({ ton: 'ok', text })
      }
    } catch (caught) {
      setMeldung({ ton: 'critical', text: formatApiError(caught, 'Speichern hat nicht geklappt.') })
    } finally {
      setSpeichert(false)
    }
  }

  return (
    <div className="wp-bearbeiten" data-audit="trocknung-reiter">
      <p className="pr-kopf">
        {daten.eigen ? 'Eigenes Programm' : 'Vorlage'} · <b>{daten.programmName}</b>
        {daten.istStandard && <> <V1Badge tone="neutral">Standard</V1Badge></>}
      </p>

      {!daten.eigen && (
        <V1Alert tone="warn" message="Mitgelieferte Vorlage: sie bleibt unverändert. Speichern legt ein eigenes Programm an." />
      )}
      {daten.istStandard && (
        <V1Alert
          tone="neutral"
          message="Dieses Programm führt noch keine eigenen Etappen — es gilt der Standard „langsam trocknen“. Ändere die Werte und speichere, dann gelten deine."
        />
      )}
      {daten.laeuft.map((l) => (
        <V1Alert key={l.zelt} tone="neutral" message={laeuftText(l)} />
      ))}
      {vorlageHinweis(daten) && <V1Alert tone="warn" message={vorlageHinweis(daten)!} />}
      {meldung && <V1Alert tone={meldung.ton} message={meldung.text} />}

      <div className="wp-blatt">
        <div className="wp-blatt-kopf">
          <button type="button" className="wp-pfeil" aria-label="Vorige Etappe" disabled={aktuell === 0} onClick={() => setIndex(aktuell - 1)}>‹</button>
          <div className="wp-blatt-titel">
            <b>{nr} von {entwurf.length}</b>
            {laeuftHier && <V1Badge tone="accent">läuft</V1Badge>}
          </div>
          <button type="button" className="wp-pfeil" aria-label="Nächste Etappe" disabled={aktuell === letzte} onClick={() => setIndex(aktuell + 1)}>›</button>
        </div>

        <div className="pr-dosis" data-audit="trocknung-etappenname">
          <label className="wp-leise" htmlFor="trocknung-etappenname">Name der Etappe</label>
          <input
            id="trocknung-etappenname"
            className="wp-eingabe pr-name"
            type="text"
            value={etappe.name}
            maxLength={NAME_MAX}
            onChange={(e) => aendern('name')(e.target.value)}
          />
        </div>

        <div className="wp-gitter wp-gitter-edit">
          <Zelle titel="Dauer" einheit="Tage" wert={etappe.dauer} audit="trocknung-dauer" name={nr}
            unter={`Zusammen ${summe} von höchstens ${daten.maxTage} Tagen`} onChange={aendern('dauer')} />
          <Zelle titel="Lufttemperatur" einheit="°C" wert={etappe.temp} audit="trocknung-temp" name={nr} onChange={aendern('temp')} />
          <Zelle titel="Luftfeuchte" einheit="%" wert={etappe.ziel} audit="trocknung-ziel" name={nr} onChange={aendern('ziel')} />
          <Zelle titel="RH-Obergrenze" einheit="%" wert={etappe.max} audit="trocknung-max" name={nr}
            unter="Schutz: darüber wächst das Schimmelrisiko" onChange={aendern('max')} />
        </div>

        <div className="pr-dosis">
          <V1Button
            variant="ghost"
            disabled={entwurf.length <= 1}
            onClick={() => { setEntwurf((alt) => etappeEntfernen(alt, aktuell)); setIndex(Math.max(0, aktuell - 1)); setMeldung(null) }}
          >
            ✕ Etappe löschen
          </V1Button>
          <V1Button
            variant="secondary"
            disabled={entwurf.length >= daten.maxEtappen}
            onClick={() => { setEntwurf((alt) => etappeEinfuegen(alt, aktuell)); setIndex(aktuell + 1); setMeldung(null) }}
            audit="trocknung-etappe-danach"
          >
            + Etappe danach
          </V1Button>
        </div>
      </div>

      <div className="wp-punkte" role="tablist" aria-label="Etappen">
        {entwurf.map((x, i) => (
          <button
            key={i}
            type="button"
            role="tab"
            aria-selected={i === aktuell}
            aria-label={`Etappe ${i + 1}: ${x.name}`}
            title={x.name}
            className={classNames('wp-punkt', i === aktuell && 'ist-aktiv', daten.laeuft.some((l) => l.etappeNr - 1 === i) && 'ist-jetzt')}
            onClick={() => setIndex(i)}
          />
        ))}
      </div>

      {!daten.eigen && geaendert && (
        <div className="pr-dosis" data-audit="trocknung-programmname">
          <label className="wp-leise" htmlFor="trocknung-programmname">Name des eigenen Programms</label>
          <input
            id="trocknung-programmname"
            className="wp-eingabe pr-name"
            type="text"
            value={name}
            maxLength={NAME_MAX + 20}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
      )}

      {probleme.length > 0 && <V1Alert tone="critical" message={probleme[0]} />}

      <p className="wp-leise pr-hinweis">
        Die Kacheln des trocknenden Zelts zeigen das Zielband der laufenden Etappe (Temperatur ±1 °C, Luftfeuchte ±2 Prozentpunkte).
        Hinter der letzten Etappe gilt sie weiter, bis das Trockengewicht eingetragen ist.
        {!daten.istStandard && (
          <>
            {' '}
            <button
              type="button"
              className="wp-plan-zurueck"
              onClick={() => { setEntwurf(entwurfAus(daten.standard)); setIndex(0); setMeldung(null) }}
            >
              Auf den Standard zurücksetzen
            </button>
          </>
        )}
      </p>

      <div className="wp-balken-platz" aria-hidden="true" />
      <div className="wp-speicherbalken" data-audit="trocknung-speicherbalken">
        <span className="wp-speicherbalken-zahl">{geaendert ? 'Ungespeicherte Änderungen' : 'Keine Änderungen'}</span>
        <V1Button variant="ghost" onClick={() => { setEntwurf(entwurfAus(daten.etappen)); setIndex(0); setMeldung(null) }} disabled={speichert || !geaendert}>
          Verwerfen
        </V1Button>
        <V1Button variant="primary" onClick={() => void speichern()} disabled={speichert || !geaendert || probleme.length > 0} audit="trocknung-speichern">
          {speichernText(daten.eigen)}
        </V1Button>
      </div>
    </div>
  )
}
