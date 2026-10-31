import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { apiFetch, ApiRequestError } from '../../api'
import { V1Sheet } from '../../components/V1Sheet'
import { KlappTitel } from '../live/Einklappen'
import { formatNumber, classNames } from '../../utils'
import { unlesbarMeldung, unlesbareFelder, zahlOderNull } from '../../zahlenfeld'
import { ueberschrift, verlaufSatz } from './fertig-text'
import './fertig.css'

/** Antwort von `GET /api/tents/{id}/trocknung/fertig` (A-029, Etappe 4). */
export interface FertigPunkt { id: number; tag: number; prozent: number; gewichtG: number; datum: string }
export interface FertigStand {
  trocknet: boolean
  growId: number | null
  tag: number | null
  stufe: 'keine' | 'erste' | 'sinkt' | 'haeltSich' | 'ziel' | null
  bezugFrischgewicht: boolean
  zielProzent: number | null
  referenzG: number | null
  chargeWiegbar: boolean
  aenderungPunkte: number | null
  naechsteWaegung: string | null
  waegungFaellig: boolean
  punkte: FertigPunkt[]
}

interface TrocknungKopf { trocknet: boolean; tageGeplant: number | null }

/** Heute als „JJJJ-MM-TT“ in Ortszeit. */
function heuteIso(): string {
  const jetzt = new Date()
  return `${jetzt.getFullYear()}-${String(jetzt.getMonth() + 1).padStart(2, '0')}-${String(jetzt.getDate()).padStart(2, '0')}`
}

/** „2026-11-08“ → „So 08.11.“ — Ortszeit, ohne UTC-Verrutschen. */
function kurzDatum(iso: string | null): string {
  if (!iso) return '–'
  const [j, m, t] = iso.split('-').map(Number)
  const datum = new Date(j, m - 1, t)
  const wochentag = new Intl.DateTimeFormat('de-DE', { weekday: 'short' }).format(datum).replace('.', '')
  return `${wochentag} ${String(t).padStart(2, '0')}.${String(m).padStart(2, '0')}.`
}

/** Die Fertig-Erkennung als Karte an der Live-Seite — nur, solange das Zelt trocknet. */
export function FertigErkennung({ tentId, zu, onKlappe }: { tentId: number | null; zu: boolean; onKlappe?: () => void }) {
  // Der Stand gehört zu einem Zelt: Wechselt die Auswahl, zeigt die Karte nichts Fremdes, bis die neue Auskunft da ist.
  const [geladen, setGeladen] = useState<{ tentId: number; stand: FertigStand; kopf: TrocknungKopf } | null>(null)
  const [blatt, setBlatt] = useState(false)
  const [loeschFehler, setLoeschFehler] = useState<string | null>(null)
  const stand = geladen && geladen.tentId === tentId ? geladen.stand : null
  const kopf = geladen && geladen.tentId === tentId ? geladen.kopf : null
  // Die Antwort gehört zu dem Zelt, das beim Absenden gewählt war — wechselt die Auswahl dazwischen, bleibt sie ohne Wirkung.
  const setStand = (neu: FertigStand, fuerZelt: number | null = tentId) => setGeladen((cur) => (cur && cur.tentId === fuerZelt ? { ...cur, stand: neu } : cur))

  useEffect(() => {
    if (tentId == null) return
    const controller = new AbortController()
    // Beigabe: Die Live-Seite steht auch ohne diese Karte — ein Fehler zeigt sie einfach nicht.
    Promise.all([
      apiFetch<FertigStand>(`/api/tents/${tentId}/trocknung/fertig`, { signal: controller.signal }),
      apiFetch<TrocknungKopf>(`/api/tents/${tentId}/trocknung`, { signal: controller.signal }),
    ]).then(([fertig, trocknung]) => setGeladen({ tentId, stand: fertig, kopf: trocknung }))
      .catch(() => { /* keine Karte */ })
    return () => controller.abort()
  }, [tentId])

  if (!stand?.trocknet || !stand.stufe) return null

  const letzte = stand.punkte[stand.punkte.length - 1]
  const prozent = letzte ? formatNumber(letzte.prozent, 0) : null
  const fertigZeigen = stand.stufe === 'haeltSich' || stand.stufe === 'ziel'

  const loeschen = async () => {
    if (!letzte || !window.confirm(`Die letzte Wägung (${formatNumber(letzte.gewichtG, 1)} g, ${kurzDatum(letzte.datum)}) löschen?`)) return
    const zelt = tentId
    try {
      setStand(await apiFetch<FertigStand>(`/api/tents/${zelt}/trocknung/waegungen/${letzte.id}`, { method: 'DELETE' }), zelt)
      setLoeschFehler(null)
    } catch (caught) {
      setLoeschFehler(caught instanceof ApiRequestError ? caught.message : 'Die Wägung konnte nicht gelöscht werden.')
    }
  }

  return (
    <article className={classNames('ls-panel', zu && 'is-zu')} data-audit="live-fertig">
      <div className="ls-panel-head">
        <KlappTitel zu={zu} onUmschalten={onKlappe}><span className="ls-label">Fertig-Erkennung</span></KlappTitel>
        <span className="ls-panel-meta">Tag {stand.tag}{kopf?.tageGeplant ? ` von ${kopf.tageGeplant}` : ''}</span>
      </div>
      {!zu && (
        <div className="ls-panel-body fe-body">
          <strong>{ueberschrift(stand)}</strong>
          {stand.stufe === 'keine' && <p>Wiege eine Probe (zum Beispiel einen Zweig oder einen Beutel mit ein paar Blüten) und danach alle 2 Tage. Ohne Frischgewicht ist die erste Wägung dein Startwert.</p>}
          {stand.stufe === 'erste' && <p>Das ist dein Startwert. Die Kurve beginnt mit der Wägung an einem weiteren Tag.</p>}
          {stand.stufe === 'sinkt' && <p>{verlaufSatz(stand, prozent)}</p>}
          {stand.stufe === 'haeltSich' && <p>Über die letzten zwei Wägungen hat sich das Gewicht um weniger als 1 % geändert. Mach den Knack-Test am Stiel: Bricht er, ist es fertig.</p>}
          {stand.stufe === 'ziel' && <p>Die Charge wiegt {prozent} % des Frischgewichts (Ziel etwa {stand.zielProzent} %). Mach den Knack-Test am Stiel: Bricht er, ist es fertig.</p>}

          {new Set(stand.punkte.map((p) => p.datum)).size >= 2 && <Kurve punkte={stand.punkte} ziel={stand.bezugFrischgewicht ? stand.zielProzent : null} haelt={stand.stufe === 'haeltSich' || stand.stufe === 'ziel'} />}

          {letzte && (
            <div className="fe-fakten">
              <div><span>Letzte Wägung</span><strong>{kurzDatum(letzte.datum)} · {formatNumber(letzte.gewichtG, 1)} g</strong></div>
              <div>
                <span>Nächste Wägung</span>
                <strong>{kurzDatum(stand.naechsteWaegung)}{stand.waegungFaellig && <em className="fe-faellig"> fällig</em>}</strong>
              </div>
            </div>
          )}

          <div className="fe-knoepfe">
            <button type="button" className={classNames('ls-btn', !fertigZeigen && 'is-primary')} onClick={() => setBlatt(true)}>
              {stand.punkte.length === 0 ? 'Erste Wägung eintragen' : stand.bezugFrischgewicht ? 'Charge wiegen' : 'Probe wiegen'}
            </button>
            {stand.punkte.length > 0 && (
              <Link className={classNames('ls-btn', fertigZeigen && 'is-primary')} to={`/grows/${stand.growId}/harvest?fertig=1`}>Fühlt sich fertig an</Link>
            )}
          </div>

          <p className="fe-hinweis">
            {stand.punkte.length === 0
              ? 'Immer dieselbe Probe auf derselben Waage. Das Frischgewicht aus der Ernte brauchst du nicht — trägst du es später nach und wiegst die ganze Charge, rechnet die Kurve mit ihm.'
              : 'Ob es sich „hält“ (weniger als 1 % an zwei Wägetagen) und das Ziel von etwa 22 % des Frischgewichts sind Faustregeln, keine Garantie. Entscheiden tust du.'}
          </p>
          {letzte && <button type="button" className="fe-link" onClick={() => void loeschen()}>Letzte Wägung löschen</button>}
          {loeschFehler && <p className="fe-fehler" role="alert">{loeschFehler}</p>}
        </div>
      )}
      <WaegeBlatt key={blatt ? 'offen' : 'zu'} open={blatt} onClose={() => setBlatt(false)} tentId={tentId} stand={stand} onGespeichert={(neu, zelt) => { setStand(neu, zelt); setBlatt(false) }} />
    </article>
  )
}

function WaegeBlatt({ open, onClose, tentId, stand, onGespeichert }: {
  open: boolean
  onClose: () => void
  tentId: number | null
  stand: FertigStand
  onGespeichert: (neu: FertigStand, fuerZelt: number | null) => void
}) {
  const [text, setText] = useState('')
  const [charge, setCharge] = useState(false)
  // Gewogen am — Voreinstellung heute; wer gestern gewogen hat und erst heute einträgt, stellt es hier um.
  const [datum, setDatum] = useState(() => heuteIso())
  const [fehler, setFehler] = useState<string | null>(null)
  const [speichert, setSpeichert] = useState(false)
  const erste = stand.punkte.length === 0
  const letzte = stand.punkte[stand.punkte.length - 1]
  const start = stand.punkte[0]

  const speichern = async () => {
    const unlesbar = unlesbarMeldung(unlesbareFelder([[text, 'Gewicht']]))
    const gramm = zahlOderNull(text)
    if (unlesbar || gramm == null || gramm <= 0) {
      setFehler(unlesbar ?? 'Trag das Gewicht in Gramm ein.')
      return
    }
    setSpeichert(true)
    setFehler(null)
    const zelt = tentId
    try {
      const neu = await apiFetch<FertigStand>(`/api/tents/${zelt}/trocknung/waegungen`, {
        method: 'POST',
        body: JSON.stringify({
          gewichtG: gramm,
          ganzeCharge: erste ? charge && stand.chargeWiegbar : stand.bezugFrischgewicht,
          datum: datum === heuteIso() ? undefined : datum,
        }),
      })
      setText('')
      setDatum(heuteIso())
      onGespeichert(neu, zelt)
    } catch (caught) {
      setFehler(caught instanceof ApiRequestError ? caught.message : 'Die Wägung konnte nicht gespeichert werden.')
    } finally {
      setSpeichert(false)
    }
  }

  return (
    <V1Sheet
      open={open}
      onClose={onClose}
      title={erste ? 'Erste Wägung' : stand.bezugFrischgewicht ? 'Charge wiegen' : 'Probe wiegen'}
      subtitle={`Trocknungstag ${stand.tag ?? ''} · heute`}
      footer={(
        <>
          <button type="button" className="ls-btn" onClick={onClose}>Abbrechen</button>
          <button type="button" className="ls-btn is-primary" disabled={speichert} onClick={() => void speichern()}>{speichert ? 'Speichert…' : 'Speichern'}</button>
        </>
      )}
    >
      <div className="fe-blatt">
        <p>{erste ? 'Immer dieselbe Probe auf derselben Waage.' : 'Dieselbe Probe wie beim letzten Mal, auf derselben Waage.'}</p>
        {erste && stand.chargeWiegbar && (
          <label className="fe-feld">
            <span>Was wiegst du?</span>
            <select value={charge ? 'charge' : 'probe'} onChange={(e) => setCharge(e.target.value === 'charge')}>
              <option value="probe">Eine Probe (Bezug: erste Wägung)</option>
              <option value="charge">Die ganze Charge (Bezug: Frischgewicht der Ernte)</option>
            </select>
          </label>
        )}
        <label className="fe-feld">
          <span>Gewogen am</span>
          <input type="date" value={datum} max={heuteIso()} onChange={(e) => setDatum(e.target.value || heuteIso())} aria-label="Gewogen am" />
        </label>
        <label className="fe-feld">
          <span>Gewicht in Gramm</span>
          <input inputMode="decimal" autoComplete="off" value={text} onChange={(e) => setText(e.target.value)} aria-label="Gewicht in Gramm" />
        </label>
        {letzte && start && (
          <p className="fe-hinweis">
            Letzte Wägung: {formatNumber(letzte.gewichtG, 1)} g · Startwert: {formatNumber(start.gewichtG, 1)} g
          </p>
        )}
        {fehler && <p className="fe-fehler" role="alert">{fehler}</p>}
      </div>
    </V1Sheet>
  )
}

/** Die Kurve des Restgewichts in Prozent — ohne Bibliothek, mit den Farben der App. */
function Kurve({ punkte, ziel, haelt }: { punkte: FertigPunkt[]; ziel: number | null; haelt: boolean }) {
  const B = 320, H = 130, L = 34, R = 20, T = 10, U = 24
  const werte = punkte.map((p) => p.prozent).concat(ziel ?? [])
  const ymin = Math.max(0, Math.floor((Math.min(...werte) - 5) / 10) * 10)
  const ymax = Math.max(ymin + 10, Math.ceil(Math.max(...werte) / 10) * 10)
  const tage = punkte.map((p) => p.tag)
  const tmin = Math.min(...tage), tmax = Math.max(...tage)
  const x = (tag: number) => L + ((tag - tmin) / (tmax - tmin || 1)) * (B - L - R)
  const y = (wert: number) => T + (1 - (wert - ymin) / (ymax - ymin)) * (H - T - U)
  const achse = [ymin, (ymin + ymax) / 2, ymax]
  const schritt = Math.max(1, Math.ceil(punkte.length / 6))
  const ruhe = haelt && punkte.length >= 3 ? x(punkte[punkte.length - 3].tag) : null
  return (
    <svg className="fe-kurve" viewBox={`0 0 ${B} ${H}`} role="img" aria-label="Kurve des Gewichts in Prozent">
      {achse.map((a) => (
        <g key={a}>
          <line x1={L} x2={B - R} y1={y(a)} y2={y(a)} className="fe-gitter" />
          <text x={L - 4} y={y(a) + 3} textAnchor="end" className="fe-achse">{formatNumber(a, 0)}%</text>
        </g>
      ))}
      {ruhe != null && <rect x={ruhe - 4} y={T} width={B - R - ruhe + 4} height={H - T - U} rx={4} className="fe-ruhe" />}
      {ziel != null && (
        <>
          <line x1={L} x2={B - R} y1={y(ziel)} y2={y(ziel)} className="fe-ziel" />
          <text x={L + 4} y={y(ziel) - 4} textAnchor="start" className="fe-ziel-text">Ziel ~{formatNumber(ziel, 0)} %</text>
        </>
      )}
      <polyline className="fe-linie" points={punkte.map((p) => `${x(p.tag).toFixed(1)},${y(p.prozent).toFixed(1)}`).join(' ')} />
      {punkte.map((p) => <circle key={p.id} className="fe-punkt" cx={x(p.tag)} cy={y(p.prozent)} r={3.2} />)}
      {punkte.map((p, i) => (i % schritt === 0 || i === punkte.length - 1) && punkte.findIndex((q) => q.tag === p.tag) === i && (
        <text key={`t${p.id}`} x={x(p.tag)} y={H - 8} textAnchor="middle" className="fe-achse">Tag {p.tag}</text>
      ))}
    </svg>
  )
}
