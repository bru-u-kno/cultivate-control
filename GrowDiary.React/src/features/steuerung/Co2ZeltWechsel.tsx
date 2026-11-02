import type { Co2ZeltKurz } from './steuerung-typen'
import './steuerung.css'

/**
 * CO₂ je Zelt: Der Zeltwechsel der CO₂-Seite. Er steht unter der Überschrift und erscheint nur, wenn mehr als ein Zelt
 * eine eigene CO₂-Steuerung hat — mit einem Zelt sieht die Seite aus wie immer.
 */
export function Co2ZeltWechsel({ zelte, aktiv, onWechsel }: {
  zelte: Co2ZeltKurz[]
  aktiv: number | null
  onWechsel: (zeltId: number) => void
}) {
  if (zelte.length < 2) return null
  return (
    <div className="st-wechsel" role="tablist" aria-label="Zelt wechseln" data-audit="co2-zeltwechsel" style={{ margin: '14px 0 6px' }}>
      {zelte.map((z) => (
        <button
          key={z.id}
          type="button"
          role="tab"
          className="st-chip"
          aria-current={z.id === aktiv}
          onClick={() => onWechsel(z.id)}
        >
          <i className="is-an" aria-hidden="true" />
          {z.name}
        </button>
      ))}
    </div>
  )
}
