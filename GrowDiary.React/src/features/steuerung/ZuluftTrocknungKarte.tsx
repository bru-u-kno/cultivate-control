import { useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1Switch } from '../../components/v1'
import { formatNumber } from '../../utils'
import '../trocknung/ziel.css'
import { useZuluftTrocknung } from './zuluft-trocknung-laden'
import { zuluftErklaerung, zuluftHinweis, zuluftPlakette, type ZuluftTrocknung } from './zuluft-trocknung-text'

/**
 * Die Zuluft folgt dem Trocknungsplan (cc.337). Die Karte steht auf der Zuluft-Seite über der Regel und erscheint nur, wenn es ein Zelt mit dem
 * Zweck „Trocknung“ gibt. Geschaltet wird im Fork (jede Minute); die Karte hält den Schalter, die Höchststufe und den Abstand unter dem
 * Planwert und zeigt, was der Fork gerade entscheidet.
 */
export function ZuluftTrocknungKarte() {
  const { stand, setStand, fehler, setFehler } = useZuluftTrocknung()
  const [lokal, setLokal] = useState<boolean | null>(null)

  if (!stand?.trockenzelt) return fehler ? <V1Alert tone="warn" title="Trocknungsbetrieb" message={fehler} /> : null

  const setzen = async (teil: { folgt?: boolean; hoechststufe?: number; abstandProzent?: number }) => {
    if (teil.folgt != null) setLokal(teil.folgt)
    setFehler(null)
    try {
      setStand(await apiFetch<ZuluftTrocknung>('/api/trocknung/zuluft', { method: 'PUT', body: JSON.stringify(teil) }))
    } catch (caught) {
      setFehler(formatApiError(caught, 'Speichern fehlgeschlagen.'))
    } finally {
      setLokal(null)
    }
  }

  const eingebunden = lokal ?? stand.folgt
  // Plakette und Satz zeigen den Zustand, den der Haken jetzt wählt — nicht den, den der Server noch hat.
  const angezeigt = { ...stand, folgt: eingebunden }
  const p = zuluftPlakette(angezeigt)
  const zahl = (wert: number | null, stellen = 1) => formatNumber(wert, stellen)
  return (
    <V1Card>
      <div className="st-feldzeile" data-audit="zuluft-trocknung">
        <span className="st-etikett">Trocknungsbetrieb<small>Zuluft von {stand.zeltName ?? 'dem Trockenzelt'}</small></span>
        <span className={`zj-chip is-${p.ton}`}>{p.text}</span>
      </div>
      {fehler && <V1Alert tone="warn" title="Nicht gespeichert" message={fehler} />}
      <V1Switch label="Folgt dem Trocknungsplan" checked={eingebunden} ausstehend={lokal != null} hint={zuluftHinweis(eingebunden)} onChange={(an) => void setzen({ folgt: an })} />
      <p className="zj-erklaerung" data-audit="zuluft-trocknung-erklaerung"><b>Was das jetzt heißt:</b> {zuluftErklaerung(angezeigt)}</p>
      {eingebunden && (
        <>
          <div className="st-feldzeile"><span className="st-etikett">Zeltluft jetzt<small>bei {zahl(stand.zeltTempC)} °C</small></span><span className="st-nurlesen">{zahl(stand.zeltRhProzent)} % · {zahl(stand.zeltAbsolutGm3)} g/m³</span></div>
          <div className="st-feldzeile"><span className="st-etikett">Planwert heute<small>{stand.tag != null ? `Tag ${stand.tag}${stand.tageGeplant != null ? ` von ${stand.tageGeplant}` : ''}` : 'kein Plan'}</small></span><span className="st-nurlesen">{zahl(stand.planRhProzent, 0)} % · {zahl(stand.planTempC, 0)} °C</span></div>
          <div className="st-feldzeile"><span className="st-etikett">Außenluft absolut</span><span className="st-nurlesen">{zahl(stand.aussenAbsolutGm3)} g/m³</span></div>
          <div className="st-feldzeile">
            <span className="st-etikett">Höchststufe<small>Stärker saugt sie im Trocknungsbetrieb nie.</small></span>
            <span className="zj-stufe">
              <V1Button variant="ghost" ariaLabel="Eine Stufe weniger" disabled={stand.hoechststufe <= 1} onClick={() => void setzen({ hoechststufe: stand.hoechststufe - 1 })}>−</V1Button>
              <b data-audit="zuluft-trocknung-hoechststufe">{stand.hoechststufe}</b>
              <V1Button variant="ghost" ariaLabel="Eine Stufe mehr" disabled={stand.hoechststufe >= 10} onClick={() => void setzen({ hoechststufe: stand.hoechststufe + 1 })}>+</V1Button>
            </span>
          </div>
          <div className="st-feldzeile">
            <span className="st-etikett">Abstand unter Plan<small>Liegt das Zelt mehr als so viele Prozentpunkte unter dem Planwert, bleibt die Zuluft aus.</small></span>
            <span className="zj-stufe">
              <V1Button variant="ghost" ariaLabel="Einen Prozentpunkt weniger" disabled={stand.abstandProzent <= 0} onClick={() => void setzen({ abstandProzent: stand.abstandProzent - 1 })}>−</V1Button>
              <b data-audit="zuluft-trocknung-abstand">{stand.abstandProzent}</b>
              <V1Button variant="ghost" ariaLabel="Einen Prozentpunkt mehr" disabled={stand.abstandProzent >= 20} onClick={() => void setzen({ abstandProzent: stand.abstandProzent + 1 })}>+</V1Button>
            </span>
          </div>
          <div className="st-feldzeile"><span className="st-etikett">Jetzt</span><span className="st-nurlesen">{stand.laeuftStufe != null && stand.laeuftStufe > 0 ? `Stufe ${zahl(stand.laeuftStufe, 0)}` : 'aus'}</span></div>
        </>
      )}
    </V1Card>
  )
}
