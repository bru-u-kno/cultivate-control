import { Link } from 'react-router-dom'
import { useKlappGemerkt } from '../../components/klapp-zustand'
import { ModulZeile } from './ModulZeile'
import { gliedernNachZelten, pfadFuer, zeltZusammenfassung } from './nach-zelten'
import type { ZeltGruppe } from './nach-zelten'
import type { SteuerungModul, SteuerungZeltKurz } from './steuerung-typen'
import { istChillerInstanz, istEntfeuchterInstanz, istLichtInstanz, istZuluftInstanz } from './steuerung-typen'
import './steuerung.css'

/**
 * Die Übersicht nach Zelten: ein einklappbarer Kopf je Zelt (im Muster der Live-Seite, ohne Score), darunter die Steuerungen
 * dieses Zeltes in den gewohnten Zeilen. Mit weniger als zwei Zelten zeigt die Seite stattdessen die eine Liste wie bisher.
 */
export function SteuerungNachZelten({ module, zelte, onOeffnen }: {
  module: SteuerungModul[]
  zelte: SteuerungZeltKurz[] | undefined
  onOeffnen: (pfad: string) => void
}) {
  const gruppen = gliedernNachZelten(module, zelte)
  if (!gruppen) {
    return (
      <>
        <div className="st-liste">
          {module.map((m) => <ModulZeile key={m.kennung} modul={m} onOeffnen={() => onOeffnen(pfadFuer(m, module))} />)}
        </div>
        {/* Bei einem Zelt nur, wenn es schon weitere Wasserkühler gibt — sonst steht der Weg auf der Seite des Wasserkühlers. */}
        {module.some((m) => istChillerInstanz(m.kennung)) && (
          <p className="st-fuss st-fuss-link"><Link to="/steuerung/chiller-neu">+ Weiteren Wasserkühler anlegen</Link></p>
        )}
        {module.some((m) => istEntfeuchterInstanz(m.kennung)) && (
          <p className="st-fuss st-fuss-link"><Link to="/steuerung/entfeuchter-neu">+ Weiteren Entfeuchter anlegen</Link></p>
        )}
        {module.some((m) => istZuluftInstanz(m.kennung)) && (
          <p className="st-fuss st-fuss-link"><Link to="/steuerung/zuluft-neu">+ Weitere Zuluft anlegen</Link></p>
        )}
        {module.some((m) => istLichtInstanz(m.kennung)) && (
          <p className="st-fuss st-fuss-link"><Link to="/steuerung/licht-neu">+ Weitere Lampe anlegen</Link></p>
        )}
      </>
    )
  }
  return (
    <div data-audit="steuerung-nach-zelten">
      {gruppen.map((g) => <ZeltBlock key={g.zelt?.id ?? 'ohne'} gruppe={g} alle={module} onOeffnen={onOeffnen} />)}
    </div>
  )
}

function ZeltBlock({ gruppe, alle, onOeffnen }: { gruppe: ZeltGruppe; alle: SteuerungModul[]; onOeffnen: (pfad: string) => void }) {
  const name = gruppe.zelt?.name ?? 'Noch keinem Zelt zugewiesen'
  const [gemerkt, setze] = useKlappGemerkt(`steuerung:zelt:${gruppe.zelt?.id ?? 'ohne'}`)
  const offen = gemerkt ?? true
  const inhaltId = `steuerung-zelt-${gruppe.zelt?.id ?? 'ohne'}`
  const anzahl = `${gruppe.module.length} ${gruppe.module.length === 1 ? 'Steuerung' : 'Steuerungen'}`
  return (
    <section className={offen ? 'ls-zb' : 'ls-zb zu'} aria-label={name} data-audit="steuerung-zelt" style={{ marginBottom: 14 }}>
      <h2 className="ls-zb-ueberschrift">
        <button type="button" className="ls-zb-kopf" aria-expanded={offen} aria-controls={offen ? inhaltId : undefined} onClick={() => setze(!offen)}>
          <span className="ls-zb-zeile">
            <span className="ls-fold-pfeil" aria-hidden="true">{offen ? '▾' : '▸'}</span>
            <strong>{name}</strong>
            <span className="ls-zb-chip">{anzahl}</span>
          </span>
          <span className="ls-zb-sub">{gruppe.zelt ? zeltZusammenfassung(gruppe.module) : 'Zelt wählen unter „Steuerungen Zelten zuweisen“'}</span>
        </button>
      </h2>
      {offen && (
        <div id={inhaltId} className="st-liste" style={{ marginTop: 6 }}>
          {gruppe.module.map((m) => <ModulZeile key={`${m.kennung}:${m.zeltId ?? ''}`} modul={m} onOeffnen={() => onOeffnen(pfadFuer(m, alle))} />)}
        </div>
      )}
      {offen && gruppe.zelt && gruppe.module.some((m) => m.kennung === 'chiller' || istChillerInstanz(m.kennung)) && (
        <p className="st-fuss st-fuss-link" style={{ margin: '-4px 0 12px 8px' }}>
          <Link to={`/steuerung/chiller-neu?zelt=${gruppe.zelt.id}`}>+ Weiteren Wasserkühler in {gruppe.zelt.name}</Link>
        </p>
      )}
      {offen && gruppe.zelt && gruppe.module.some((m) => m.kennung === 'entfeuchter' || m.kennung === 'entfeuchtung' || istEntfeuchterInstanz(m.kennung)) && (
        <p className="st-fuss st-fuss-link" style={{ margin: '-4px 0 12px 8px' }}>
          <Link to={`/steuerung/entfeuchter-neu?zelt=${gruppe.zelt.id}`}>+ Weiteren Entfeuchter in {gruppe.zelt.name}</Link>
        </p>
      )}
      {offen && gruppe.zelt && gruppe.module.some((m) => m.kennung === 'licht' || istLichtInstanz(m.kennung)) && (
        <p className="st-fuss st-fuss-link" style={{ margin: '-4px 0 12px 8px' }}>
          <Link to={`/steuerung/licht-neu?zelt=${gruppe.zelt.id}`}>+ Weitere Lampe in {gruppe.zelt.name}</Link>
        </p>
      )}
      {offen && gruppe.zelt && gruppe.module.some((m) => m.kennung === 'zuluft' || istZuluftInstanz(m.kennung)) && (
        <p className="st-fuss st-fuss-link" style={{ margin: '-4px 0 12px 8px' }}>
          <Link to={`/steuerung/zuluft-neu?zelt=${gruppe.zelt.id}`}>+ Weitere Zuluft in {gruppe.zelt.name}</Link>
        </p>
      )}
    </section>
  )
}
