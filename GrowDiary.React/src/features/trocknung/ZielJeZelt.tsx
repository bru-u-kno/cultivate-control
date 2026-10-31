import { useEffect, useState } from 'react'
import { apiFetch, ApiRequestError } from '../../api'
import { V1Sheet } from '../../components/V1Sheet'
import { KlappTitel } from '../live/Einklappen'
import { classNames } from '../../utils'
import { amGeraetText, URTEIL_TEXT, zahl, type Ziel, type ZielGeraet } from './ziel-text'
import './fertig.css'
import './ziel.css'

interface Ports {
  erreichbar: boolean
  entfeuchter: string | null
  kuehlung: string | null
  verfuegbar: Array<{ deviceId: string; name: string }>
}

/** Karte „Trocknungs-Ziel“ an der Live-Seite — nur, solange das Zelt trocknet; schreibt nichts. */
export function ZielJeZelt({ tentId, zu, onKlappe }: { tentId: number | null; zu: boolean; onKlappe?: () => void }) {
  const [geladen, setGeladen] = useState<{ tentId: number; ziel: Ziel } | null>(null)
  const [blatt, setBlatt] = useState(false)
  const ziel = geladen && geladen.tentId === tentId ? geladen.ziel : null

  useEffect(() => {
    if (tentId == null) return
    const controller = new AbortController()
    // Beigabe: Die Live-Seite steht auch ohne diese Karte — ein Fehler zeigt sie einfach nicht.
    apiFetch<Ziel>(`/api/tents/${tentId}/trocknung/ziel`, { signal: controller.signal })
      .then((antwort) => setGeladen({ tentId, ziel: antwort }))
      .catch(() => { /* keine Karte */ })
    return () => controller.abort()
  }, [tentId])

  if (!ziel?.trocknet) return null

  const leer = ziel.geraete.every((g) => g.urteil === 'nichtZugeordnet')
  const plan = ziel.tempZiel != null && ziel.feuchteZiel != null
    ? `Plan: ${zahl(ziel.tempZiel)} °C · ${zahl(ziel.feuchteZiel)} % Luftfeuchte`
    : 'Kein Plan für diesen Tag'

  return (
    <article className={classNames('ls-panel', zu && 'is-zu')} data-audit="live-trocknungs-ziel">
      <div className="ls-panel-head">
        <KlappTitel zu={zu} onUmschalten={onKlappe}><span className="ls-label">Trocknungs-Ziel</span></KlappTitel>
        <span className="ls-panel-meta">Tag {ziel.tag}</span>
      </div>
      {!zu && (
        <div className="ls-panel-body fe-body">
          {leer ? (
            <>
              <strong>Noch keine Geräte zugeordnet</strong>
              <p>Ordne den Port des Entfeuchters und den der Kühlung zu. Dann zeigt der Fork, was der Plan vorgibt und was am Gerät steht — ohne etwas zu ändern.</p>
              <div className="fe-knoepfe">
                <button type="button" className="ls-btn is-primary" onClick={() => setBlatt(true)}>Ports zuordnen</button>
              </div>
              <p className="fe-hinweis">{plan}{ziel.etappe ? ` (${ziel.etappe})` : ''}.</p>
            </>
          ) : (
            <>
              <strong>{plan}</strong>
              <p className="zj-etappe">{ziel.etappe}</p>
              <p>Ein Vorschlag, wie der Plan an den Geräten aussähe (oberer Rand des Bands). Der Fork schreibt dabei noch nichts — das hier ist nur der Vergleich.</p>
              <div className="zj-geraete">
                {ziel.geraete.map((g) => <GeraetZeile key={g.rolle} g={g} />)}
              </div>
              {!ziel.portsErreichbar && <p className="fe-hinweis">Home Assistant ist gerade nicht erreichbar — die Werte am Gerät lassen sich nicht lesen.</p>}
              <p className="fe-hinweis">Deine Werte an den Geräten bleiben, wie sie sind. Dem Plan folgen sollen sie nur, wenn du es später einschaltest.</p>
              <div className="fe-knoepfe">
                <button type="button" className="ls-btn" onClick={() => setBlatt(true)}>Ports ändern</button>
              </div>
            </>
          )}
        </div>
      )}
      <PortBlatt
        key={blatt ? 'offen' : 'zu'}
        open={blatt}
        onClose={() => setBlatt(false)}
        tentId={tentId}
        onGespeichert={(neu) => { setGeladen(neu == null || tentId == null ? null : { tentId, ziel: neu }); setBlatt(false) }}
      />
    </article>
  )
}

function GeraetZeile({ g }: { g: ZielGeraet }) {
  const titel = g.rolle === 'entfeuchter' ? 'Entfeuchter' : 'Kühlung'
  const feld = g.rolle === 'entfeuchter' ? 'Luftfeuchte-Obergrenze am Gerät' : 'Temperatur-Obergrenze am Gerät'
  const einheit = g.rolle === 'entfeuchter' ? '%' : '°C'
  const u = URTEIL_TEXT[g.urteil]
  return (
    <div className="zj-zeile" data-audit={`ziel-${g.rolle}`}>
      <div className="zj-kopf">
        <strong>{titel}</strong>
        <span className={classNames('zj-chip', u.ton && `is-${u.ton}`)}>{u.text}</span>
      </div>
      <div className="zj-sub">{g.portName ? `${g.portName}${g.modus ? ` · ${g.modus}` : ''}` : 'kein Port zugeordnet'}</div>
      {g.urteil !== 'nichtZugeordnet' && (
        <>
          <div className="zj-wert"><span>{feld}</span><strong>{amGeraetText(g)}</strong></div>
          {g.planWert != null && <div className="zj-wert"><span>Plan (oben im Band)</span><strong>{zahl(g.planWert)} {einheit}</strong></div>}
        </>
      )}
    </div>
  )
}

function PortBlatt({ open, onClose, tentId, onGespeichert }: {
  open: boolean
  onClose: () => void
  tentId: number | null
  onGespeichert: (neu: Ziel | null) => void
}) {
  const [ports, setPorts] = useState<Ports | null>(null)
  const [entfeuchter, setEntfeuchter] = useState('')
  const [kuehlung, setKuehlung] = useState('')
  const [fehler, setFehler] = useState<string | null>(null)
  const [speichert, setSpeichert] = useState(false)

  useEffect(() => {
    if (!open || tentId == null) return
    const controller = new AbortController()
    apiFetch<Ports>(`/api/tents/${tentId}/trocknung/ports`, { signal: controller.signal })
      .then((antwort) => {
        setPorts(antwort)
        setEntfeuchter(antwort.entfeuchter ?? '')
        setKuehlung(antwort.kuehlung ?? '')
      })
      .catch(() => { /* das Blatt zeigt dann „nicht erreichbar“ */ })
    return () => controller.abort()
  }, [open, tentId])

  const speichern = async () => {
    setSpeichert(true)
    setFehler(null)
    try {
      await apiFetch<Ports>(`/api/tents/${tentId}/trocknung/ports`, {
        method: 'PUT',
        body: JSON.stringify({ entfeuchter: entfeuchter || null, kuehlung: kuehlung || null }),
      })
    } catch (caught) {
      setFehler(caught instanceof ApiRequestError ? caught.message : 'Die Ports konnten nicht gespeichert werden.')
      setSpeichert(false)
      return
    }
    // Gespeichert ist gespeichert: Schlägt nur das Nachlesen der Karte fehl, bleibt es dabei — die Karte lädt beim nächsten Öffnen neu.
    try {
      onGespeichert(await apiFetch<Ziel>(`/api/tents/${tentId}/trocknung/ziel`))
    } catch {
      onGespeichert(null)
    } finally {
      setSpeichert(false)
    }
  }

  const auswahl = (wert: string, setzen: (w: string) => void, label: string) => (
    <label className="fe-feld">
      <span>{label}</span>
      <select value={wert} onChange={(e) => setzen(e.target.value)} aria-label={label} disabled={!ports?.erreichbar}>
        <option value="">— keiner —</option>
        {wert && ports && !ports.verfuegbar.some((p) => p.deviceId === wert) && <option value={wert}>Gespeicherter Port (gerade nicht gefunden)</option>}
        {ports?.verfuegbar.map((p) => <option key={p.deviceId} value={p.deviceId}>{p.name}</option>)}
      </select>
    </label>
  )

  return (
    <V1Sheet
      open={open}
      onClose={onClose}
      title="Ports zuordnen"
      footer={(
        <>
          <button type="button" className="ls-btn" onClick={onClose}>Abbrechen</button>
          <button type="button" className="ls-btn is-primary" disabled={speichert || !ports?.erreichbar} onClick={() => void speichern()}>{speichert ? 'Speichert…' : 'Speichern'}</button>
        </>
      )}
    >
      <div className="fe-blatt">
        <p>Angeboten werden nur Ports mit eigener Automatik (Auslöser für Temperatur und Luftfeuchte).</p>
        {ports && !ports.erreichbar && <p className="fe-fehler" role="alert">Home Assistant ist gerade nicht erreichbar — die Ports lassen sich nicht auswählen.</p>}
        {ports?.erreichbar && ports.verfuegbar.length === 0 && <p className="fe-hinweis">Home Assistant kennt keinen Port mit eigener Automatik.</p>}
        {auswahl(entfeuchter, setEntfeuchter, 'Port des Entfeuchters')}
        {auswahl(kuehlung, setKuehlung, 'Port der Kühlung')}
        {fehler && <p className="fe-fehler" role="alert">{fehler}</p>}
      </div>
    </V1Sheet>
  )
}
