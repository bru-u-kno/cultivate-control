import { useEffect, useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Badge, V1Button, V1Skeleton } from '../../components/v1'
import { classNames } from '../../utils'
import '../wochenplan/wochenplan.css'
import {
  NAME_MAX, entwurfAus, etappeEinfuegen, etappeEntfernen, etappenAus, fehler, feldSetzen, istGeaendert, laeuftText, summeTage,
  type EtappeEntwurf, type TrocknungsplanZelt,
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
 * Plan › Trocknung (A-029): der Trocknungsplan des Trockenzelts — frei editierbare Etappen mit Dauer, Temperatur- und Feuchteziel.
 *
 * Der Plan gehört dem Zelt und ist unabhängig vom Düngeprogramm: Er gilt für jede Charge im Trockenzelt, egal, womit sie gewachsen
 * ist. Die Live-Kacheln und die Steuerung der Geräte folgen ihm. Ohne eigene Etappen gilt der Standard „langsam trocknen“.
 */
export function TrocknungsplanReiter({ zeltId }: { zeltId: number }) {
  const [daten, setDaten] = useState<TrocknungsplanZelt | null>(null)
  const [laden, setLaden] = useState(true)
  const [entwurf, setEntwurf] = useState<EtappeEntwurf[]>([])
  const [index, setIndex] = useState(0)
  const [speichert, setSpeichert] = useState(false)
  const [meldung, setMeldung] = useState<{ text: string; ton: 'ok' | 'critical' | 'neutral' } | null>(null)

  useEffect(() => {
    let aktiv = true
    async function holen() {
      try {
        const antwort = await apiFetch<TrocknungsplanZelt>(`/api/tents/${zeltId}/trocknung/plan`)
        if (!aktiv) return
        setDaten(antwort)
        setEntwurf(entwurfAus(antwort.etappen))
        // Mit der Etappe aufmachen, die gerade läuft — sonst mit der ersten.
        setIndex(Math.max(0, (antwort.laeuft?.etappeNr ?? 1) - 1))
      } catch (caught) {
        if (aktiv) setMeldung({ ton: 'critical', text: formatApiError(caught, 'Der Trocknungsplan konnte nicht geladen werden.') })
      } finally {
        if (aktiv) setLaden(false)
      }
    }
    void holen()
    return () => { aktiv = false }
  }, [zeltId])

  const geaendert = useMemo(() => (daten ? istGeaendert(entwurf, daten.etappen) : false), [daten, entwurf])
  const probleme = useMemo(() => (daten ? fehler(entwurf, daten.maxTage, daten.maxEtappen) : []), [daten, entwurf])

  if (laden) return <V1Skeleton rows={6} label="Lade Trocknungsplan" />
  if (!daten) return meldung ? <V1Alert tone={meldung.ton} message={meldung.text} /> : null

  const aktuell = Math.min(index, Math.max(0, entwurf.length - 1))
  const etappe = entwurf[aktuell]
  const letzte = entwurf.length - 1
  const summe = summeTage(entwurf)
  const nr = `Etappe ${aktuell + 1}`
  const laeuftHier = daten.laeuft != null && daten.laeuft.etappeNr - 1 === aktuell
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
      const antwort = await apiFetch<TrocknungsplanZelt>(`/api/tents/${daten.zeltId}/trocknung/plan`, {
        method: 'PUT',
        body: JSON.stringify({ etappen }),
      })
      setDaten(antwort)
      setEntwurf(entwurfAus(antwort.etappen))
      setMeldung({ ton: 'ok', text: `Trocknungsplan gespeichert — er gilt ab sofort für „${antwort.zeltName}“.` })
    } catch (caught) {
      setMeldung({ ton: 'critical', text: formatApiError(caught, 'Speichern hat nicht geklappt.') })
    } finally {
      setSpeichert(false)
    }
  }

  return (
    <div className="wp-bearbeiten" data-audit="trocknung-reiter">
      <p className="pr-kopf">
        Trocknungsplan · <b>{daten.zeltName}</b>
        {daten.istStandard && <> <V1Badge tone="neutral">Standard</V1Badge></>}
      </p>

      <V1Alert
        tone="neutral"
        message={`Eigener Plan, unabhängig vom Düngeplan. Er gilt für jede Charge, die im Trockenzelt hängt — egal, mit welchem Düngeprogramm sie gewachsen ist.${daten.istStandard ? ' Noch keine eigenen Etappen: Es gilt der Standard „langsam trocknen“. Ändere die Werte und speichere, dann gelten deine.' : ''}`}
      />
      {daten.laeuft && <V1Alert tone="neutral" message={laeuftText(daten.laeuft)} />}
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
            className={classNames('wp-punkt', i === aktuell && 'ist-aktiv', daten.laeuft?.etappeNr === i + 1 && 'ist-jetzt')}
            onClick={() => setIndex(i)}
          />
        ))}
      </div>

      {probleme.length > 0 && <V1Alert tone="critical" message={probleme[0]} />}

      <p className="wp-leise pr-hinweis">
        Die Kacheln des trocknenden Zelts zeigen das Zielband der laufenden Etappe (Temperatur ±1 °C, Luftfeuchte ±2 Prozentpunkte).
        Hinter der letzten Etappe gilt sie weiter, bis das Trockengewicht eingetragen ist. Für einen Wert pro Tag lege Etappen von einem Tag Dauer an.
      </p>
      {!daten.istStandard && (
        <div className="pr-dosis" data-audit="trocknung-standard">
          <V1Button variant="ghost" onClick={() => { setEntwurf(entwurfAus(daten.standard)); setIndex(0); setMeldung(null) }} disabled={speichert}>
            Standard „langsam trocknen“ wiederherstellen
          </V1Button>
        </div>
      )}

      <div className="wp-balken-platz" aria-hidden="true" />
      <div className="wp-speicherbalken" data-audit="trocknung-speicherbalken">
        <span className="wp-speicherbalken-zahl">{geaendert ? 'Ungespeicherte Änderungen' : 'Keine Änderungen'}</span>
        <V1Button variant="ghost" onClick={() => { setEntwurf(entwurfAus(daten.etappen)); setIndex(0); setMeldung(null) }} disabled={speichert || !geaendert}>
          Verwerfen
        </V1Button>
        <V1Button variant="primary" onClick={() => void speichern()} disabled={speichert || !geaendert || probleme.length > 0} audit="trocknung-speichern">
          Speichern
        </V1Button>
      </div>
    </div>
  )
}
