import { classNames } from '../../utils'
import {
  GRUPPEN, alsText, feldText, weichtAb,
  type Entwurf, type WochenwertFeld, type WochenwertSpalte,
} from '../wochenplan/wochenwerte-bearbeiten'
import '../wochenplan/wochenplan.css'

type Props = {
  woche: WochenwertSpalte
  entwurf: Entwurf
  /** „Nachts gelten die Tageswerte“: die Nachtzellen zeigen dann nur den Tageswert. */
  wieTag: boolean
  /**
   * Woher ein Nachtwert kommt (`standard` = aus Tag − 4 K). Nur der Plan eines
   * Grows führt das mit; ein Programm ohne Grow lässt es weg.
   */
  herkunft?: Record<string, string>
  onAendern: (feld: WochenwertFeld, text: string) => void
  /** Eine Gruppe zurück auf den Planwert (die Feldnamen der Gruppe). */
  onAufPlan: (felder: readonly string[]) => void
}

/**
 * Die Werte EINER Woche als Gitter — wie im freigegebenen Mockup „Werte bearbeiten“.
 *
 * <b>Warum ein eigener Baustein.</b> Das Gitter stand im Plan-Reiter eines Grows.
 * Seit A-029 öffnet die Seite „Plan“ auch ein Programm ohne Grow; ein zweites
 * Gitter dafür liefe mit der Zeit auseinander (Regel „Komponenten zentral“,
 * der Nutzer, 11.10.2026). Beide Seiten reichen hier nur ihren Entwurf und ihre
 * Handler herein.
 */
export function WochenGitter({ woche, entwurf, wieTag, herkunft, onAendern, onAufPlan }: Props) {
  return (
    <div className="wp-gitter wp-gitter-edit">
      {GRUPPEN.map((gruppe) => {
        const felder = gruppe.felder
          .map((name) => woche.felder.find((f) => f.feld === name))
          .filter((f) => f !== undefined)
        if (felder.length === 0) return null
        if (gruppe.nacht && wieTag) {
          // Ruht: nachts gilt der Tageswert — zeigen, welcher, statt ein Feld anzubieten.
          const tagFeld = woche.felder.find((f) => f.feld === gruppe.nacht?.tag)
          const tagText = tagFeld ? (feldText(entwurf, woche.id, tagFeld) || alsText(tagFeld.plan)) : ''
          return (
            <div key={gruppe.titel} className="wp-zelle pr-wie-tag" data-audit={`plan-${felder[0].feld}-wie-tag`}>
              <div className="wp-zelle-n">{gruppe.titel}</div>
              <div className="pr-wie-tag-wert">= {tagText || '–'}{felder[0].einheit ? ` ${felder[0].einheit}` : ''}</div>
              <div className="wp-plan">wie tags</div>
            </div>
          )
        }
        const eigen = felder.some((f) => weichtAb(entwurf, woche.id, f))
        return (
          <div key={gruppe.titel} className={classNames('wp-zelle', eigen && 'ist-eigen')}>
            <div className="wp-zelle-n">
              {gruppe.titel}
              {felder[0].einheit && <span className="wp-einheit"> {felder[0].einheit}</span>}
            </div>
            <div className="wp-eingaben">
              {felder.map((f, i) => (
                <span key={f.feld} className="wp-eingabe-paar">
                  {i > 0 && <span className="wp-leise">–</span>}
                  <input
                    className="wp-eingabe"
                    type="text"
                    inputMode="decimal"
                    aria-label={`${woche.label}: ${f.bezeichnung}`}
                    data-audit={`plan-${f.feld}`}
                    value={feldText(entwurf, woche.id, f)}
                    placeholder={alsText(f.plan) || (gruppe.nacht ? 'wie tags' : 'eintragen')}
                    onChange={(e) => onAendern(f, e.target.value)}
                  />
                </span>
              ))}
            </div>
            <div className="wp-plan">
              {gruppe.nacht && felder.every((f) => f.plan == null)
                ? 'leer = wie tags'
                : gruppe.nacht && felder.every((f) => herkunft?.[f.feld] === 'standard')
                  ? 'aus Tag − 4 K'
                  : <>Start {felder.map((f) => alsText(f.plan) || '–').join('–')}</>}
              {eigen && (
                <>
                  {' · '}
                  <button type="button" className="wp-plan-zurueck" onClick={() => onAufPlan(gruppe.felder)}>
                    zurück
                  </button>
                </>
              )}
            </div>
          </div>
        )
      })}
    </div>
  )
}
