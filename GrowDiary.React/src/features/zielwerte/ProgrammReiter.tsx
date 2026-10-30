import { useEffect, useMemo, useRef, useState, type PointerEvent } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Badge, V1Button, V1Skeleton } from '../../components/v1'
import { classNames } from '../../utils'
import {
  aenderungen, aufPlan, fehler, punkt, schluessel, startIndex,
  type Entwurf,
} from '../wochenplan/wochenwerte-bearbeiten'
import '../wochenplan/wochenplan.css'
import { WochenGitter } from './WochenGitter'
import {
  NAME_MAX, ergebnisText, kopieName, nameFehler, speichernAnfrage, speichernText, wirktAufGrows,
  type ProgrammGespeichert, type ProgrammWerte,
} from './programm-plan'

const WISCH_SCHWELLE = 50

/**
 * Fork AI (A-029, Etappe 1): ein Düngeprogramm auf der Seite „Plan“ — ohne
 * laufenden Grow.
 *
 * Bis dahin ließ sich ein Plan nur über einen Grow öffnen; zwischen zwei
 * Grows blieb die Seite leer, und Vorlagen oder eigene Programme waren
 * nirgends änderbar. Hier gilt: Ein mitgeliefertes Programm bleibt
 * unverändert, Speichern legt eine Kopie als eigenes Programm an. Ein eigenes
 * wird an Ort und Stelle geändert.
 *
 * Nur die Wochenwerte (Ziele je Woche). Die Dosierung gehört weiter dem Plan
 * eines Grows.
 */
export function ProgrammReiter({ programmId, startMeldung, onKopie }: {
  programmId: string
  /** Der Satz, den die Seite beim Öffnen zeigt (nach dem Anlegen einer Kopie). */
  startMeldung?: string | null
  /** Eine Kopie ist entstanden: die Seite wechselt auf sie und reicht den Satz weiter. */
  onKopie: (neueId: string, text: string) => void
}) {
  const [werte, setWerte] = useState<ProgrammWerte | null>(null)
  const [laden, setLaden] = useState(true)
  const [index, setIndex] = useState(0)
  const [entwurf, setEntwurf] = useState<Entwurf>({})
  const [name, setName] = useState('')
  const [speichert, setSpeichert] = useState(false)
  const [meldung, setMeldung] = useState<{ text: string; ton: 'ok' | 'critical' | 'neutral' } | null>(
    startMeldung ? { text: startMeldung, ton: 'ok' } : null,
  )
  const wischStart = useRef<{ x: number; y: number } | null>(null)

  useEffect(() => {
    let aktiv = true
    async function holen() {
      try {
        const antwort = await apiFetch<ProgrammWerte>(`/api/programme/${encodeURIComponent(programmId)}/werte`)
        if (!aktiv) return
        setWerte(antwort)
        setIndex(startIndex(antwort))
        setName(kopieName(antwort.programmName))
      } catch (caught) {
        if (aktiv) setMeldung({ ton: 'critical', text: formatApiError(caught, 'Das Programm konnte nicht geladen werden.') })
      } finally {
        if (aktiv) setLaden(false)
      }
    }
    void holen()
    return () => { aktiv = false }
  }, [programmId])

  const offen = useMemo(() => (werte ? aenderungen(werte, entwurf) : []), [werte, entwurf])
  const probleme = useMemo(() => {
    if (!werte) return []
    const sammlung = fehler(werte, entwurf)
    const nameProblem = !werte.eigen && offen.length > 0 ? nameFehler(name) : null
    return nameProblem ? [...sammlung, nameProblem] : sammlung
  }, [werte, entwurf, offen.length, name])

  if (laden) return <V1Skeleton rows={6} label="Lade Programm" />
  if (!werte) return meldung ? <V1Alert tone={meldung.ton} message={meldung.text} /> : null

  const woche = werte.spalten[index]
  const letzte = werte.spalten.length - 1
  const blaettern = (richtung: -1 | 1) => setIndex((i) => Math.min(letzte, Math.max(0, i + richtung)))
  const wirkung = wirktAufGrows(werte)

  function wischBeginn(event: PointerEvent<HTMLDivElement>) {
    const ziel = event.target as HTMLElement
    if (ziel.closest('button')) return
    if (event.pointerType === 'mouse' && ziel.closest('input')) return
    wischStart.current = { x: event.clientX, y: event.clientY }
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  function wischEnde(event: PointerEvent<HTMLDivElement>) {
    const start = wischStart.current
    wischStart.current = null
    if (!start) return
    const dx = event.clientX - start.x
    const dy = event.clientY - start.y
    if (Math.abs(dx) < WISCH_SCHWELLE || Math.abs(dx) < Math.abs(dy) * 1.5) return
    blaettern(dx < 0 ? 1 : -1)
  }

  async function speichern() {
    if (!werte) return
    setSpeichert(true)
    setMeldung(null)
    try {
      const antwort = await apiFetch<ProgrammGespeichert>(`/api/programme/${encodeURIComponent(werte.programmId)}/werte`, {
        method: 'POST',
        body: JSON.stringify(speichernAnfrage(offen, werte.eigen, name)),
      })
      const anzahl = offen.length
      setEntwurf({})
      const text = ergebnisText(antwort, anzahl)
      if (antwort.neuAngelegt) {
        // Die Kopie ist jetzt das Programm, das man bearbeitet; die Vorlage bleibt, wie sie war.
        onKopie(antwort.werte.programmId, text)
      } else {
        setWerte(antwort.werte)
        setMeldung({ ton: 'ok', text })
      }
    } catch (caught) {
      setMeldung({ ton: 'critical', text: formatApiError(caught, 'Speichern hat nicht geklappt.') })
    } finally {
      setSpeichert(false)
    }
  }

  return (
    <div className="wp-bearbeiten" data-audit="programm-reiter">
      <p className="pr-kopf">
        {werte.eigen ? 'Eigenes Programm' : 'Vorlage'} · <b>{werte.programmName}</b>
      </p>

      {!werte.eigen && (
        <V1Alert
          tone="warn"
          message="Mitgelieferte Vorlage: sie bleibt unverändert. Speichern legt ein eigenes Programm an."
        />
      )}
      {wirkung && <V1Alert tone="neutral" message={wirkung} />}
      {meldung && <V1Alert tone={meldung.ton} message={meldung.text} />}

      <div className="wp-blatt" onPointerDown={wischBeginn} onPointerUp={wischEnde} onPointerCancel={() => (wischStart.current = null)}>
        <div className="wp-blatt-kopf">
          <button type="button" className="wp-pfeil" aria-label="Vorige Woche" disabled={index === 0} onClick={() => blaettern(-1)}>‹</button>
          <div className="wp-blatt-titel">
            <b>{woche.label}</b>
          </div>
          <button type="button" className="wp-pfeil" aria-label="Nächste Woche" disabled={index === letzte} onClick={() => blaettern(1)}>›</button>
        </div>

        <WochenGitter
          woche={woche}
          entwurf={entwurf}
          wieTag={false}
          onAendern={(f, text) => { setEntwurf((alt) => ({ ...alt, [schluessel(woche.id, f.feld)]: text })); setMeldung(null) }}
          onAufPlan={(felder) => setEntwurf((alt) => aufPlan(alt, woche, felder))}
        />
      </div>

      <div className="wp-punkte" role="tablist" aria-label="Wochen">
        {werte.spalten.map((s, i) => {
          const zustand = punkt(werte, entwurf, s)
          return (
            <button
              key={s.id}
              type="button"
              role="tab"
              aria-selected={i === index}
              aria-label={s.label}
              title={s.label}
              className={classNames('wp-punkt', i === index && 'ist-aktiv', zustand.eigen && 'ist-eigen', zustand.offen && 'ist-offen')}
              onClick={() => setIndex(i)}
            />
          )
        })}
      </div>

      {!werte.eigen && offen.length > 0 && (
        <div className="pr-dosis" data-audit="programm-name">
          <label className="wp-leise" htmlFor="programm-name-feld">Name des eigenen Programms</label>
          <input
            id="programm-name-feld"
            className="wp-eingabe pr-name"
            type="text"
            value={name}
            maxLength={NAME_MAX}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
      )}

      {probleme.length > 0 && (
        <V1Alert tone="critical" message={probleme[0]} />
      )}

      <p className="wp-leise pr-hinweis">
        Hier stehen die Ziele je Woche. Die Dosierung (ml je Liter) bearbeitest du im Plan eines Grows.
        {' '}<V1Badge tone="neutral">{werte.spalten.length} Wochen</V1Badge>
      </p>

      <div className="wp-balken-platz" aria-hidden="true" />
      <div className="wp-speicherbalken" data-audit="programm-speicherbalken">
        <span className="wp-speicherbalken-zahl">
          {offen.length === 0 ? 'Keine Änderungen' : `${offen.length} Änderung${offen.length === 1 ? '' : 'en'}`}
        </span>
        <V1Button variant="ghost" onClick={() => { setEntwurf({}); setMeldung(null) }} disabled={speichert || offen.length === 0}>
          Verwerfen
        </V1Button>
        <V1Button variant="primary" onClick={() => void speichern()} disabled={speichert || offen.length === 0 || probleme.length > 0} audit="programm-speichern">
          {speichernText(werte.eigen)}
        </V1Button>
      </div>
    </div>
  )
}
