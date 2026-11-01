import { useEffect, useState } from 'react'
import { apiFetch } from '../../api'
import { Link } from 'react-router-dom'
import { KlappTitel } from '../live/Einklappen'
import { classNames } from '../../utils'
import { amGeraetText, URTEIL_TEXT, zahl, type Ziel, type ZielGeraet } from './ziel-text'
import './fertig.css'
import './ziel.css'

/**
 * Karte „Trocknungs-Ziel“ an der Live-Seite — nur, solange das Zelt trocknet; zeigt nur Werte. Was die Geräte dem Plan folgen
 * lässt, stellt man unter Steuerung › Trocknung ein (A-029, Teil B).
 */
export function ZielJeZelt({ tentId, zu, onKlappe }: { tentId: number | null; zu: boolean; onKlappe?: () => void }) {
  const [geladen, setGeladen] = useState<{ tentId: number; ziel: Ziel } | null>(null)
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
              <p>Ordne unter Steuerung den Port des Entfeuchters und den der Kühlung zu. Dann zeigt der Fork hier, was der Plan vorgibt und was am Gerät steht.</p>
              <div className="fe-knoepfe">
                <Link className="ls-btn is-primary" to="/steuerung/trocknung">Unter Steuerung zuordnen ›</Link>
              </div>
              <p className="fe-hinweis">{plan}{ziel.etappe ? ` (${ziel.etappe})` : ''}.</p>
            </>
          ) : (
            <>
              <strong>{plan}</strong>
              <p className="zj-etappe">{ziel.etappe}</p>
              <p>Der Plan gegen die Grenzen an den Geräten (oberer Rand des Bands).</p>
              <div className="zj-geraete">
                {ziel.geraete.map((g) => <GeraetZeile key={g.rolle} g={g} />)}
              </div>
              {!ziel.portsErreichbar && <p className="fe-hinweis">Home Assistant ist gerade nicht erreichbar — die Werte am Gerät lassen sich nicht lesen.</p>}
              <p className="fe-hinweis">Hier stehen nur Werte. Ob die Geräte dem Plan folgen, stellst du unter Steuerung ein.</p>
              <div className="fe-knoepfe">
                <Link className="ls-btn" to="/steuerung/trocknung">Einstellen unter Steuerung ›</Link>
              </div>
            </>
          )}
        </div>
      )}
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

