import type { SteuerungZeltKurz } from './steuerung-typen'
import './steuerung.css'

/** Das Zelt wählen: ein Chip je Zelt, genau eines gewählt (im Muster der Steuerungs-Leiste). */
export function ZeltChips({ zelte, wert, onChange }: {
  zelte: SteuerungZeltKurz[]
  wert: number | null
  onChange: (zeltId: number) => void
}) {
  return (
    <div className="st-wechsel" role="group" aria-label="Zelt" style={{ flexWrap: 'wrap', overflowX: 'visible' }}>
      {zelte.map((z) => (
        <button key={z.id} type="button" className="st-chip" aria-pressed={z.id === wert} aria-current={z.id === wert} onClick={() => onChange(z.id)}>
          <i className={z.id === wert ? 'is-an' : ''} aria-hidden="true" />
          {z.name}
        </button>
      ))}
    </div>
  )
}
