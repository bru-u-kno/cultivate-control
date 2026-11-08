import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1LinkButton, V1Page, V1Section, V1Skeleton, V1Stat, V1Switch, V1Tabs } from '../../components/v1'
import { V1Sheet } from '../../components/V1Sheet'
import { Kurzmeldung, type KurzmeldungTon } from '../../components/Kurzmeldung'
import { SteuerungWechsel } from '../steuerung/SteuerungWechsel'
import { ZuluftTrocknungAnzeige } from '../steuerung/ZuluftTrocknungAnzeige'
import { ControllerMarke } from '../steuerung/ControllerMarke'
import type { SteuerungModul } from '../steuerung/steuerung-typen'
import '../steuerung/steuerung.css'
import './ziel.css'
import { PortBlatt } from './PortBlatt'
import {
  ARBEIT_SCHRITTE, ARBEIT_TITEL, betriebText, einbindenHinweis, erklaerung, kopfZeile, ohneEinbindungText, plakette, ROLLE, SCHALTER, schalterVon,
  wuerdeSchalten, wuerdeSchreiben, zahl,
  type ArbeitPhase, type Betrieb, type Folgt, type Schalter, type SteuerungGeraet, type SteuerungSchalten, type SteuerungStufe, type TrocknungSteuerung,
} from './steuerung-text'

/**
 * Steuerung › Trocknung (A-029, Etappe 3, Teil B): welche Geräte am Trockenzelt bindet der Nutzer ein?
 *
 * Eingebundene Geräte bringt die Steuerung auf die Planwerte des Trocknungsplans und hält sie dort. Technisch ändert der Fork nur
 * die Grenzen der Auslöser an den Ports des Controllers; der Controller regelt weiter selbst. Eine Ausnahme ist der Entfeuchter
 * (06.11.2026): Ihn schaltet der Fork selbst ein und aus (On/Off des Ports, nie Auto), weil die Cloud des Controllers das Schreiben
 * der Grenzen im Modus Auto ablehnt. „Nur anzeigen“
 * schreibt nichts und zeigt, was geschrieben würde. Auf „Regeln“ stellt man erst nach einer Rückfrage, die genau auflistet,
 * was geschrieben wird.
 *
 * Bedienung (cc.333): Der Haken erscheint sofort beim Antippen. Solange der Fork speichert und mit dem Controller abgleicht, zeigt
 * die Karte die Phase (und sperrt sich); dasselbe sehen andere Benutzer über `inArbeit` im Stand. Am Ende steht „Fertig“ oder
 * der Grund, warum der Haken zurückgesprungen ist.
 */
const SCHNELL_MS = 1500
const RUHIG_MS = 15000

const neueKennung = () => `s-${Math.random().toString(36).slice(2, 10)}`

type Vorgang = { schalter: Schalter | 'betrieb'; betrieb: Betrieb; an: boolean | null }
type Band = { schalter: Schalter | 'betrieb'; ton: 'ok' | 'critical'; text: string }

export default function TrocknungDetail({ module, aktiv, onWechsel }: {
  module: SteuerungModul[]
  aktiv: string
  onWechsel: (kennung: string) => void
}) {
  const zeltId = module.find((m) => m.kennung === 'trocknung')?.zeltId ?? null
  const [stand, setStand] = useState<TrocknungSteuerung | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)
  const [laedt, setLaedt] = useState(true)
  const [rueckfrage, setRueckfrage] = useState<{ betrieb: Betrieb; folgt: Folgt; schalter: Schalter | null } | null>(null)
  const [portBlatt, setPortBlatt] = useState(false)
  // Eigener Vorgang: gesetzter Haken sofort sichtbar, bis der Fork fertig ist (oder den Haken zurückstellt).
  const [vorgang, setVorgang] = useState<Vorgang | null>(null)
  const [lokal, setLokal] = useState<Folgt | null>(null)
  const [band, setBand] = useState<Band | null>(null)
  const [kurz, setKurz] = useState<{ text: string; ton: KurzmeldungTon; nr: number } | null>(null)
  const [kennung] = useState(neueKennung)
  // Eine ältere Antwort darf einen neueren Stand nicht überschreiben (Abfrage vor dem Speichern, Antwort danach).
  const ladeNr = useRef(0)

  const laden = useCallback(async () => {
    if (zeltId == null) return
    const nr = ++ladeNr.current
    const geladen = await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`)
    if (nr === ladeNr.current) setStand(geladen)
  }, [zeltId])

  useEffect(() => {
    if (zeltId == null) return
    const controller = new AbortController()
    const erstLaden = async () => {
      try {
        const geladen = await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`, { signal: controller.signal })
        if (!controller.signal.aborted) { setStand(geladen); setFehler(null) }
      } catch (caught) {
        if (!controller.signal.aborted) setFehler(formatApiError(caught, 'Die Steuerung der Trocknung konnte nicht geladen werden.'))
      } finally {
        if (!controller.signal.aborted) setLaedt(false)
      }
    }
    void erstLaden()
    return () => controller.abort()
  }, [zeltId])

  // Takt: Der Wert am Gerät ändert sich auch ohne uns (Controller, Etappenwechsel, ein anderer Benutzer). Läuft ein Vorgang
  // (eigener oder fremder), fragen wir dichter nach, damit Phase und Ende sichtbar werden.
  const vorgangLaeuft = vorgang != null || stand?.inArbeit != null
  useEffect(() => {
    const uhr = window.setInterval(() => { void laden().catch(() => undefined) }, vorgangLaeuft ? SCHNELL_MS : RUHIG_MS)
    return () => window.clearInterval(uhr)
  }, [laden, vorgangLaeuft])

  // Das „Fertig“-Band verschwindet nach einigen Sekunden von selbst; der Fehler bleibt, bis etwas Neues passiert.
  useEffect(() => {
    if (band?.ton !== 'ok') return undefined
    const t = window.setTimeout(() => setBand(null), 6000)
    return () => window.clearTimeout(t)
  }, [band])

  const meldeKurz = useCallback((text: string, ton: KurzmeldungTon = 'ok') => setKurz((alt) => ({ text, ton, nr: (alt?.nr ?? 0) + 1 })), [])
  const kurzSchliessen = useCallback(() => setKurz(null), [])

  const setzen = async (betrieb: Betrieb, folgt: Folgt, schalter: Schalter | null, minStufe?: number) => {
    if (zeltId == null) return
    const bezug: Schalter | 'betrieb' = schalter ?? 'betrieb'
    setVorgang({ schalter: bezug, betrieb, an: schalter ? folgt[schalter] : null })
    setLokal(folgt); setBand(null); setFehler(null)
    ladeNr.current++
    try {
      const neu = await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`, {
        method: 'PUT',
        body: JSON.stringify({ betrieb, entfeuchterFolgt: folgt.entfeuchter, kuehlungFolgt: folgt.kuehlung, abluftFolgt: folgt.abluft, ...(minStufe != null ? { abluftMinStufe: minStufe } : {}), clientId: kennung }),
      })
      ladeNr.current++
      setStand(neu)
      const zeilenNeu = schalter ? neu.geraete.filter((g) => schalterVon(g.rolle) === schalter) : []
      const ziel = schalter ? ROLLE[schalter === 'abluft' ? 'abluft-feuchte' : schalter].titel : null
      const status = schalter ? plakette(kopfZeile(zeilenNeu), neu.betrieb).text : null
      // Nur sagen, was feststeht: gespeichert und abgeglichen — ob am Gerät etwas geschrieben wurde, zeigt die Plakette.
      const text = minStufe != null
        ? (minStufe === 0 ? 'Keine Mindeststufe für die Abluft.' : `Mindeststufe der Abluft: Stufe ${minStufe}. ${status ?? ''}`.trim())
        : schalter
        ? (folgt[schalter]
          ? `${ziel} ist eingebunden — ${status}.`
          : `${ziel} ist nicht mehr eingebunden. ${ohneEinbindungText(zeilenNeu)}`)
        : `Betriebsart: ${betriebText(betrieb)}. ${betrieb === 'regeln' ? 'Die Steuerung gleicht die eingebundenen Geräte jetzt mit dem Plan ab.' : 'Die Steuerung ändert nichts.'}`
      setBand({ schalter: bezug, ton: 'ok', text: `Fertig. ${text}` })
      meldeKurz(text)
    } catch (caught) {
      // Der Haken springt auf den Stand des Servers zurück; die Karte sagt, warum.
      const grund = formatApiError(caught, 'Speichern fehlgeschlagen.')
      setBand({ schalter: bezug, ton: 'critical', text: `Nicht abgeschlossen. Der Haken zeigt wieder den gespeicherten Stand. ${grund}` })
      meldeKurz('Nicht abgeschlossen — der Haken zeigt wieder den gespeicherten Stand.', 'critical')
      void laden().catch(() => undefined)
    } finally {
      setLokal(null)
      setVorgang(null)
    }
  }

  const zuruecksetzen = async () => {
    if (zeltId == null) return
    setArbeitet(true); setBand(null)
    try {
      const antwort = await apiFetch<{ zurueck: number; gescheitert: number; stand: TrocknungSteuerung }>(
        `/api/tents/${zeltId}/trocknung/steuerung/zuruecksetzen`, { method: 'POST' })
      ladeNr.current++
      setStand(antwort.stand); setFehler(null)
      const text = antwort.gescheitert > 0
        ? `${antwort.zurueck} Wert zurückgesetzt, ${antwort.gescheitert} ließ sich nicht zurücksetzen. Die Steuerung fasst nichts mehr an.`
        : 'Die alten Werte stehen wieder am Gerät.'
      setBand({ schalter: 'betrieb', ton: 'ok', text })
      meldeKurz(text)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Zurücksetzen fehlgeschlagen.'))
    } finally {
      setArbeitet(false)
    }
  }

  const kopf = { eyebrow: 'Steuerung', title: 'Trocknung', vorKopf: <SteuerungWechsel module={module} aktiv={aktiv} onWechsel={onWechsel} /> }
  if (zeltId == null) {
    return <V1Page {...kopf}><V1Alert tone="warn" title="Kein Trockenzelt" message="Es gibt noch kein Zelt mit dem Zweck „Trocknung“." /></V1Page>
  }
  if (laedt && !stand) return <V1Page {...kopf}><V1Skeleton rows={4} tiles={2} label="Wird geladen" /></V1Page>
  if (!stand) return <V1Page {...kopf}>{fehler && <V1Alert tone="critical" message={fehler} />}</V1Page>

  const serverFolgt: Folgt = {
    entfeuchter: stand.geraete.find((g) => g.rolle === 'entfeuchter')?.folgt ?? false,
    kuehlung: stand.geraete.find((g) => g.rolle === 'kuehlung')?.folgt ?? false,
    abluft: stand.geraete.find((g) => schalterVon(g.rolle) === 'abluft')?.folgt ?? false,
  }
  // Angezeigt wird, was der Nutzer gerade gewählt hat (Rückfrage offen, Vorgang läuft) — sonst der Stand vom Server.
  // Bei offener Rückfrage gilt nur der eine gewählte Haken als „noch nicht gespeichert“; die anderen zeigen den Stand vom Server.
  const mitRueckfrage: Folgt = rueckfrage?.schalter ? { ...serverFolgt, [rueckfrage.schalter]: rueckfrage.folgt[rueckfrage.schalter] } : serverFolgt
  const folgt: Folgt = rueckfrage ? mitRueckfrage : lokal ?? serverFolgt
  const betriebAngezeigt: Betrieb = vorgang?.betrieb ?? stand.betrieb
  const zugeordnet = SCHALTER.filter((s) => stand.geraete.some((g) => schalterVon(g.rolle) === s && g.urteil !== 'nichtZugeordnet')).length
  const regeln = betriebAngezeigt === 'regeln'
  const hatMerker = stand.geraete.some((g) => g.vorher != null)
  const fremd = vorgang == null && stand.inArbeit != null && stand.inArbeit.clientId !== kennung
  const phase = stand.inArbeit?.phase ?? 'speichern'
  const gesperrt = vorgang != null || fremd || arbeitet

  // Wer „Regeln“ einschaltet oder im Betrieb „Regeln“ ein Gerät dazunimmt, bekommt vorher gezeigt, was geschrieben wird.
  const anfrage = (betrieb: Betrieb, neu: Folgt, schalter: Schalter | null) => {
    if (betrieb === 'regeln' && wuerdeSchreiben(stand.geraete, neu).length > 0) {
      setRueckfrage({ betrieb, folgt: neu, schalter })
      return
    }
    void setzen(betrieb, neu, schalter)
  }

  const untertitel = stand.trocknet
    ? `Etappe „${stand.etappe ?? '–'}“, Tag ${stand.tag} von ${stand.tageGeplant}`
    : 'Es trocknet gerade nichts'
  const bandText = (von: number | null, bis: number | null, einheit: string) =>
    von == null || bis == null ? '–' : von === bis ? `${zahl(von)} ${einheit}` : `${zahl(von)}–${zahl(bis)} ${einheit}`

  // Bestätigt wird mit dem frischen Stand: Hat inzwischen jemand anderes einen anderen Haken geändert, bleibt das erhalten.
  const bestaetigen = () => {
    const r = rueckfrage
    setRueckfrage(null)
    if (!r) return
    if (gesperrt) { meldeKurz('Gerade läuft ein anderer Vorgang — bitte gleich noch einmal einbinden.', 'warn'); return }
    const neu: Folgt = r.schalter ? { ...serverFolgt, [r.schalter]: r.folgt[r.schalter] } : serverFolgt
    void setzen(r.betrieb, neu, r.schalter)
  }

  const rueckfrageName = rueckfrage?.schalter ? ROLLE[rueckfrage.schalter === 'abluft' ? 'abluft-feuchte' : rueckfrage.schalter].titel : null
  const rueckfrageAbbrechen = () => {
    if (rueckfrageName) meldeKurz(`Abgebrochen — ${rueckfrageName} bleibt nicht eingebunden.`, 'warn')
    setRueckfrage(null)
  }

  return (
    <V1Page
      eyebrow="Steuerung"
      title="Trocknung"
      subtitle={untertitel}
      vorKopf={kopf.vorKopf}
    >
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {vorgang?.schalter === 'betrieb' && (
        <div className="zj-band is-warn" role="status" data-audit="trocknung-vorgang-betrieb">
          <b>Betriebsart wird gespeichert …</b> Bis die Steuerung fertig ist, sind die Karten gesperrt.
          <ul className="zj-schritte">
            {ARBEIT_SCHRITTE.map((x, i) => (
              <li key={x.phase} className={i < ARBEIT_SCHRITTE.findIndex((y) => y.phase === phase) ? 'is-fertig' : x.phase === phase ? 'is-jetzt' : ''}>{x.text}</li>
            ))}
          </ul>
        </div>
      )}
      {band?.schalter === 'betrieb' && !vorgang && <V1Alert tone={band.ton} message={band.text} />}
      {fremd && (
        <V1Alert
          tone="warn"
          title="Jemand anderes ändert gerade etwas"
          message={`Die Steuerung ${ARBEIT_TITEL[phase]}. Die Karten sind gesperrt, bis der Vorgang fertig ist.`}
        />
      )}
      {!stand.portsErreichbar && <V1Alert tone="warn" title="Home Assistant nicht erreichbar" message="Die Werte an den Geräten lassen sich gerade nicht lesen. Die Steuerung schreibt in dieser Zeit nichts." />}
      <V1Alert
        tone={regeln ? 'ok' : 'neutral'}
        title={regeln ? 'Regeln: die eingebundenen Geräte folgen dem Plan' : 'Nur anzeigen'}
        message={regeln
          ? 'Die Steuerung versucht mit den eingebundenen Geräten, die Planwerte zu erreichen und zu halten.'
          : 'Es wird nichts an die Geräte geschrieben.'}
      />

      <ControllerMarke />

      <section className="v1-kpi-grid">
        <V1Stat label="Plan Luft" value={bandText(stand.tempMin, stand.tempMax, '°C')} wortwert />
        <V1Stat label="Plan Luftfeuchte" value={bandText(stand.feuchteMin, stand.feuchteMax, '%')} wortwert />
      </section>

      <V1Card>
        <div className="st-feldzeile">
          <span className="st-etikett">
            Die Etappen änderst du im Plan.
            <small>Zielwert je Etappe; für einen Wert pro Tag lege Etappen von einem Tag an.</small>
          </span>
          <V1LinkButton to={`/plan?art=trocknung&zelt=${zeltId}`} variant="ghost">Plan ›</V1LinkButton>
        </div>
      </V1Card>

      <V1Section title="Geräte einbinden">
        <V1Card>
          <div className="st-feldzeile">
            <span className="st-etikett">
              Wie arbeitet die Steuerung?
              <small>
                {regeln
                  ? 'Die Steuerung stellt die eingebundenen Geräte auf den Trocknungsplan und hält sie dort — beim Start, bei jedem Etappenwechsel und alle 5 Minuten. Den Entfeuchter schaltet sie jede Minute, die Mindeststufe der Abluft prüft sie jede Minute.'
                  : 'Die Steuerung ändert nichts. Sie zeigt nur, was sie an den eingebundenen Geräten einstellen würde.'}
              </small>
            </span>
          </div>
          <V1Tabs
            items={[{ value: 'melden' as Betrieb, label: 'Nur anzeigen' }, { value: 'regeln' as Betrieb, label: 'Regeln' }]}
            active={betriebAngezeigt}
            onChange={(b) => { if (b !== stand.betrieb && !gesperrt) { if (b === 'melden') void setzen('melden', folgt, null); else anfrage('regeln', folgt, null) } }}
            label="Betriebsart"
          />
        </V1Card>

        {SCHALTER.map((schalter) => {
          const zeilen = stand.geraete.filter((g) => schalterVon(g.rolle) === schalter)
          if (zeilen.length === 0) return null
          return (
            <GeraetKarte
              key={schalter}
              schalter={schalter}
              zeilen={zeilen}
              betrieb={betriebAngezeigt}
              eingebunden={folgt[schalter]}
              wartetAufBestaetigung={rueckfrage?.schalter === schalter}
              eigenerVorgang={vorgang?.schalter === schalter ? { phase } : null}
              band={band?.schalter === schalter ? band : null}
              gesperrt={gesperrt}
              minStufe={stand.abluftMinStufe ?? 0}
              onStufe={(n) => void setzen(stand.betrieb, folgt, 'abluft', n)}
              onFolgt={(an) => {
                const neu = { ...folgt, [schalter]: an }
                if (regeln && an) anfrage('regeln', neu, schalter)
                else void setzen(stand.betrieb, neu, schalter)
              }}
            />
          )
        })}

        <ZuluftTrocknungAnzeige />

        <div className="zj-aktionen">
          {!regeln && <V1Button variant="primary" disabled={gesperrt || !SCHALTER.some((s) => folgt[s])} onClick={() => anfrage('regeln', folgt, null)}>Auf „Regeln“ stellen…</V1Button>}
          {hatMerker && <V1Button variant="ghost" disabled={gesperrt} onClick={() => void zuruecksetzen()}>Alte Werte zurück</V1Button>}
        </div>
      </V1Section>

      <div className="st-geraete-zeile">
        <span>Ports <b>{zugeordnet} / {SCHALTER.length}</b> zugeordnet</span>
        <V1Button variant="ghost" onClick={() => setPortBlatt(true)}>Ports bearbeiten ›</V1Button>
      </div>

      <V1Sheet
        open={rueckfrage != null}
        onClose={rueckfrageAbbrechen}
        title={rueckfrage?.schalter && stand.betrieb === 'regeln' ? 'Gerät einbinden?' : 'Auf „Regeln“ stellen?'}
        footer={(
          <>
            <button type="button" className="ls-btn" onClick={rueckfrageAbbrechen}>Abbrechen</button>
            <button type="button" className="ls-btn is-primary" onClick={() => bestaetigen()}>
              {rueckfrage?.schalter && stand.betrieb === 'regeln' ? 'Ja, einbinden' : 'Ja, Regeln einschalten'}
            </button>
          </>
        )}
      >
        <div className="zj-rueckfrage">
          <p>Die Steuerung stellt die Geräte ab jetzt auf den Planwert:</p>
          {rueckfrage && (() => {
            const w = wuerdeSchalten(stand.geraete, rueckfrage.folgt)
            return w ? (
              <div className="zj-wert" data-audit="rueckfrage-entfeuchter-schalten">
                <span>{w.portName} · schaltet nach der Luftfeuchte</span>
                <strong>ein ab {zahl(w.einAb)} %, aus unter {zahl(w.ausUnter)} %</strong>
              </div>
            ) : null
          })()}
          {rueckfrage && wuerdeSchreiben(stand.geraete, rueckfrage.folgt).map((w) => (
            <div key={w.rolle} className="zj-wert" data-audit={`rueckfrage-${w.rolle}`}>
              <span>{w.portName} · {w.feld}</span>
              <strong>{zahl(w.von)} → {zahl(w.nach)} {w.einheit}</strong>
            </div>
          ))}
          <p className="fe-hinweis">
            Nicht angefasst: alle Geräte, die nicht eingebunden sind. Den Modus eines Ports ändert die Steuerung nur beim Entfeuchter (On und Off, nie Auto).
            Bei den übrigen merkt sie sich die alten Werte; „Alte Werte zurück“ setzt sie wieder ein. {betriebText('regeln')} gilt, bis du auf „Nur anzeigen“ zurückstellst.
          </p>
        </div>
      </V1Sheet>
      <PortBlatt
        key={portBlatt ? 'offen' : 'zu'}
        open={portBlatt}
        onClose={() => setPortBlatt(false)}
        tentId={zeltId}
        onGespeichert={() => { setPortBlatt(false); void laden().catch(() => undefined) }}
      />
      <Kurzmeldung text={kurz?.text ?? null} ton={kurz?.ton} nr={kurz?.nr} onSchliessen={kurzSchliessen} />
    </V1Page>
  )
}

/** Die Abluft mit Mindeststufe: Stepper für die gewählte Stufe und die Stufe jetzt. */
function StufeZeilen({ mindest, stufe, gesperrt, onStufe }: { mindest: number; stufe: SteuerungStufe | null; gesperrt: boolean; onStufe: (n: number) => void }) {
  return (
    <div data-audit="trocknung-stufe">
      <div className="st-feldzeile">
        <span className="st-etikett">Mindeststufe<small>Die Abluft läuft nie darunter. 0 = keine Mindeststufe.</small></span>
        <span className="zj-stufe">
          <V1Button variant="ghost" ariaLabel="Eine Stufe weniger" disabled={gesperrt || mindest <= 0} onClick={() => onStufe(mindest - 1)}>−</V1Button>
          <b data-audit="trocknung-stufe-wert">{mindest}</b>
          <V1Button variant="ghost" ariaLabel="Eine Stufe mehr" disabled={gesperrt || mindest >= 10} onClick={() => onStufe(mindest + 1)}>+</V1Button>
        </span>
      </div>
      {stufe && (
        <div className="st-feldzeile">
          <span className="st-etikett">Jetzt<small>{stufe.modus === 'On' ? 'Port steht auf On' : stufe.modus ? `Port steht auf ${stufe.modus === 'Off' ? 'Aus' : stufe.modus}` : 'Modus nicht lesbar'}</small></span>
          <span className="st-nurlesen">{stufe.jetzt == null ? '–' : `Stufe ${zahl(stufe.jetzt)}`}</span>
        </div>
      )}
    </div>
  )
}

/** Die drei Zeilen des Entfeuchters, den der Fork schaltet: Schwellen, Luftfeuchte jetzt, letzter Schaltvorgang. */
function SchaltZeilen({ s }: { s: SteuerungSchalten }) {
  const seit = s.zuletztUtc ? new Date(s.zuletztUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) : null
  const jetzt = s.modus === 'On' ? 'an' : s.modus === 'Off' ? 'aus' : (s.modus ?? '–')
  return (
    <div data-audit="trocknung-schalten">
      <div className="st-feldzeile">
        <span className="st-etikett">Schaltet ein ab</span>
        <span className="st-nurlesen">{s.einAb == null ? '–' : `${zahl(s.einAb)} %`}</span>
      </div>
      <div className="st-feldzeile">
        <span className="st-etikett">Schaltet aus unter</span>
        <span className="st-nurlesen">{s.ausUnter == null ? '–' : `${zahl(s.ausUnter)} %`}</span>
      </div>
      <div className="st-feldzeile">
        <span className="st-etikett">Jetzt<small>{s.feuchte == null ? 'Luftfeuchte im Zelt nicht lesbar' : `Luftfeuchte im Zelt ${zahl(s.feuchte)} %`}</small></span>
        <span className="st-nurlesen">{seit && s.zuletztAktion === (s.modus === 'On' ? 'ein' : 'aus') ? `${jetzt} seit ${seit}` : jetzt}</span>
      </div>
    </div>
  )
}

/**
 * Eine Karte je Gerät. Die Abluft hat zwei Zeilen (Feuchte- und Temperatur-Grenze), die anderen eine.
 * Der Haken zeigt die Wahl des Nutzers sofort; solange ein Vorgang läuft, sagt die Karte, was gerade passiert.
 */
function GeraetKarte({ schalter, zeilen, betrieb, eingebunden, wartetAufBestaetigung, eigenerVorgang, band, gesperrt, onFolgt, minStufe, onStufe }: {
  schalter: Schalter
  zeilen: SteuerungGeraet[]
  betrieb: Betrieb
  eingebunden: boolean
  wartetAufBestaetigung: boolean
  eigenerVorgang: { phase: ArbeitPhase } | null
  band: Band | null
  gesperrt: boolean
  onFolgt: (an: boolean) => void
  /** Mindeststufe der Abluft (nur bei der Abluft von Belang). */
  minStufe: number
  onStufe: (stufe: number) => void
}) {
  // Plakette und Erklärung zeigen den Zustand, den der Haken jetzt wählt — nicht den, den der Server noch hat.
  const angezeigt = zeilen.map((z) => ({ ...z, folgt: eingebunden }))
  const g = kopfZeile(angezeigt)
  const r = ROLLE[g.rolle]
  const p = plakette(g, betrieb)
  const nichtZugeordnet = g.urteil === 'nichtZugeordnet'
  const schritt = eigenerVorgang ? ARBEIT_SCHRITTE.findIndex((x) => x.phase === eigenerVorgang.phase) : -1
  return (
    <V1Card>
      <div className="st-feldzeile" data-audit={`trocknung-${schalter}`}>
        <span className="st-etikett">
          {r.titel}
          <small>{g.portName ? `${g.portName}${g.modus ? ` · ${g.modus === 'Off' ? 'Modus Aus' : g.modus}` : ''}` : 'kein Port zugeordnet'}</small>
        </span>
        {eigenerVorgang
          ? <span className="zj-chip is-warn" data-audit="trocknung-vorgang"><span className="v1-dreher" aria-hidden="true" />{ARBEIT_TITEL[eigenerVorgang.phase]}</span>
          : wartetAufBestaetigung
            ? <span className="zj-chip is-warn">wartet auf Bestätigung</span>
            : <span className={`zj-chip is-${p.ton}`}>{p.text}</span>}
      </div>
      {eigenerVorgang && (
        <div className="zj-band is-warn" role="status">
          <b>{eigenerVorgang.phase === 'speichern' ? 'Wird gespeichert …' : 'Die Steuerung arbeitet …'}</b> Der Haken ist schon gesetzt. Bis die Steuerung fertig ist, ist diese Karte gesperrt.
          <ul className="zj-schritte">
            {ARBEIT_SCHRITTE.map((x, i) => (
              <li key={x.phase} className={i < schritt ? 'is-fertig' : i === schritt ? 'is-jetzt' : ''}>{x.text}</li>
            ))}
          </ul>
        </div>
      )}
      {wartetAufBestaetigung && !eigenerVorgang && (
        <div className="zj-band is-warn" role="status"><b>Noch nicht gespeichert.</b> Der Haken gilt erst, wenn du unten „Ja, einbinden“ tippst.</div>
      )}
      {band && !eigenerVorgang && <div className={`zj-band ${band.ton === 'ok' ? 'is-ok' : 'is-fehler'}`} role="status">{band.text}</div>}
      {!nichtZugeordnet && (
        <>
          <V1Switch
            label="Gerät einbinden"
            checked={eingebunden}
            onChange={onFolgt}
            hint={einbindenHinweis(schalter, eingebunden, betrieb, minStufe)}
            gesperrt={gesperrt || eigenerVorgang != null || wartetAufBestaetigung}
          />
          {!eigenerVorgang && !wartetAufBestaetigung && (
            <p className="zj-erklaerung" data-audit={`trocknung-erklaerung-${schalter}`}><b>Was das jetzt heißt:</b> {erklaerung(angezeigt, betrieb)}</p>
          )}
          {g.schalten && g.urteil !== 'nichtZugeordnet' && (
            <SchaltZeilen s={g.schalten} />
          )}
          {schalter === 'abluft' && eingebunden && (
            <StufeZeilen
              mindest={minStufe}
              stufe={zeilen.find((z) => z.stufe)?.stufe ?? null}
              gesperrt={gesperrt || eigenerVorgang != null || wartetAufBestaetigung}
              onStufe={onStufe}
            />
          )}
          {!g.schalten && !g.stufe && zeilen.map((z) => (
            <div key={z.rolle}>
              <div className="st-feldzeile">
                <span className="st-etikett">{ROLLE[z.rolle].feld.replace('-Obergrenze', '')}: Planwert</span>
                <span className="st-nurlesen">{z.planWert == null ? '–' : `${zahl(z.planWert)} ${ROLLE[z.rolle].einheit}`}</span>
              </div>
              <div className="st-feldzeile">
                <span className="st-etikett">{ROLLE[z.rolle].feld.replace('-Obergrenze', '')}: am Gerät</span>
                <span className="st-nurlesen">{z.wertAmGeraet == null ? '–' : `${zahl(z.wertAmGeraet)} ${ROLLE[z.rolle].einheit}`}</span>
              </div>
            </div>
          ))}
          {!g.schalten && !g.stufe && g.zuletztGesetzt && (
            <div className="st-feldzeile">
              <span className="st-etikett">Zuletzt gesetzt<small>{g.vorher != null ? `vorher ${zahl(g.vorher)} ${r.einheit} (gemerkt)` : null}</small></span>
              <span className="st-nurlesen">{new Date(g.zuletztGesetzt).toLocaleString('de-DE', { dateStyle: 'short', timeStyle: 'short' })}</span>
            </div>
          )}
        </>
      )}
    </V1Card>
  )
}
