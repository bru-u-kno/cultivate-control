import { useKlappGemerkt } from '../../components/klapp-zustand'
import { ModulZeile } from './ModulZeile'
import { gliedernNachZelten, pfadFuer, zeltZusammenfassung } from './nach-zelten'
import type { ZeltGruppe } from './nach-zelten'
import type { SteuerungModul, SteuerungZeltKurz } from './steuerung-typen'
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
      <div className="st-liste">
        {module.map((m) => <ModulZeile key={m.kennung} modul={m} onOeffnen={() => onOeffnen(pfadFuer(m, module))} />)}
      </div>
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
    </section>
  )
}
