import { useEffect, useState } from 'react'
import { apiFetch, ApiRequestError } from '../../api'
import { V1Sheet } from '../../components/V1Sheet'
import './fertig.css'

interface Ports {
  erreichbar: boolean
  entfeuchter: string | null
  kuehlung: string | null
  abluft: string | null
  verfuegbar: Array<{ deviceId: string; name: string }>
}

/** Blatt „Ports zuordnen“: welcher Port des Controllers ist am Trockenzelt der Entfeuchter, welcher die Kühlung, welcher die Abluft (Steuerung › Trocknung). */
export function PortBlatt({ open, onClose, tentId, onGespeichert }: {
  open: boolean
  onClose: () => void
  tentId: number | null
  onGespeichert: () => void
}) {
  const [ports, setPorts] = useState<Ports | null>(null)
  const [entfeuchter, setEntfeuchter] = useState('')
  const [kuehlung, setKuehlung] = useState('')
  const [abluft, setAbluft] = useState('')
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
        setAbluft(antwort.abluft ?? '')
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
        body: JSON.stringify({ entfeuchter: entfeuchter || null, kuehlung: kuehlung || null, abluft: abluft || null }),
      })
    } catch (caught) {
      setFehler(caught instanceof ApiRequestError ? caught.message : 'Die Ports konnten nicht gespeichert werden.')
      setSpeichert(false)
      return
    }
    setSpeichert(false)
    onGespeichert()
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
        {auswahl(abluft, setAbluft, 'Port der Abluft')}
        {fehler && <p className="fe-fehler" role="alert">{fehler}</p>}
      </div>
    </V1Sheet>
  )
}
