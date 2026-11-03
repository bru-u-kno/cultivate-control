import { HaltBand } from './HaltBand'
import type { SteuerungModul } from './steuerung-typen'
import './steuerung.css'

/** Eine Zeile der Steuerungs-Übersicht: Punkt, Titel mit Kurzauskunft links, Wert mit Unterzeile rechts. */
export function ModulZeile({ modul, onOeffnen }: { modul: SteuerungModul; onOeffnen: () => void }) {
  const punkt = modul.status === 'an' ? 'is-an' : modul.status === 'warn' ? 'is-warn' : 'is-aus'
  // Fork AI (forkai.238): Hat ein Modul einen Zustand (Zuluft), steht er als eigene Zeile über die volle
  // Breite — Schild, Grund mit Zahlen, bei „zu wenig Unterschied" das Band. Rechts bleibt dann nur der Wert;
  // die anderen Module behalten ihre kleine Unterzeile unter dem Wert.
  const zustand = modul.zustand ?? null
  return (
    <button type="button" className={zustand ? 'st-zeile hat-zustand' : 'st-zeile'} onClick={onOeffnen} disabled={!modul.hatDetail}>
      <span className="st-zeile-links">
        <i className={`st-punkt ${punkt}`} aria-hidden="true" />
        <span className="st-titel">
          {modul.titel}
          <small>{modul.kurz}</small>
        </span>
      </span>
      <span className="st-wert">
        {modul.wert}
        {!zustand && <small>{modul.unterzeile}</small>}
      </span>
      {zustand && (
        <span className="st-zustand">
          <span className="st-zustand-zeile">
            <span className={`st-schild ton-${zustand.ton}`}>{zustand.wort}</span>
            <span className="st-zustand-satz">{zustand.satz}</span>
          </span>
          {modul.balken && (
            <HaltBand
              hb={{ anAb: modul.balken.anAb, ausUnter: modul.balken.ausUnter }}
              differenz={modul.balken.ist}
              kompakt
            />
          )}
        </span>
      )}
    </button>
  )
}
