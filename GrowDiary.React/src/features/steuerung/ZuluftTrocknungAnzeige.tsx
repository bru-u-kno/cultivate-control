import { Link } from 'react-router-dom'
import { V1Card } from '../../components/v1'
import { formatNumber } from '../../utils'
import '../trocknung/ziel.css'
import { useZuluftTrocknung } from './zuluft-trocknung-laden'
import { zuluftErklaerung, zuluftPlakette } from './zuluft-trocknung-text'

/**
 * Steuerung › Trocknung: Die Zuluft des Trockenzelts nur als Anzeige (cc.337). Eingestellt wird sie auf der Zuluft-Seite; hier steht,
 * ob sie dem Plan folgt und was der Fork gerade entscheidet. Erscheint nur, wenn die Zuluft dem Plan folgt.
 */
export function ZuluftTrocknungAnzeige() {
  const { stand } = useZuluftTrocknung()
  if (!stand?.trockenzelt || !stand.folgt) return null
  const p = zuluftPlakette(stand)
  return (
    <V1Card>
      <div className="st-feldzeile" data-audit="trocknung-zuluft">
        <span className="st-etikett">Zuluft<small>Steuerung › Zuluft</small></span>
        <span className={`zj-chip is-${p.ton}`}>{p.text}</span>
      </div>
      <p className="zj-erklaerung" data-audit="trocknung-erklaerung-zuluft"><b>Was das jetzt heißt:</b> {zuluftErklaerung(stand)}</p>
      <div className="st-feldzeile"><span className="st-etikett">Zeltluft jetzt<small>Planwert {formatNumber(stand.planRhProzent, 0)} %</small></span><span className="st-nurlesen">{formatNumber(stand.zeltRhProzent)} %</span></div>
      <div className="st-feldzeile"><span className="st-etikett">Zuluft jetzt<small>Höchststufe {stand.hoechststufe}</small></span><span className="st-nurlesen">{stand.laeuftStufe != null && stand.laeuftStufe > 0 ? `Stufe ${formatNumber(stand.laeuftStufe, 0)}` : 'aus'}</span></div>
      <p className="st-fuss st-fuss-link"><Link to="/steuerung/zuluft">Zuluft-Steuerung öffnen ›</Link></p>
    </V1Card>
  )
}
