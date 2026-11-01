import type { ReactNode } from 'react'
import { KlappBereich } from '../../components/Klapp'
import './gilt-fuer.css'

/** Eine Zeile der Liste: ein Grow oder ein Zelt, dem der Plan zugewiesen werden kann. */
export type GiltZeile = {
  id: number
  name: string
  /** Zweite Zeile: wo, in welchem Zustand, was heute gilt. */
  unter: string
  /** Dritte Zeile in Auszeichnung — was die Zuweisung ersetzt, oder dass sie schon besteht. */
  zusatz?: string
  zusatzTon?: 'warn' | 'ok'
  angehakt: boolean
  /** Schon zugewiesen oder der Plan, an dem man gerade arbeitet: nichts mehr anzukreuzen. */
  gesperrt?: boolean
}

/**
 * Plan › „Gilt für …“: die Liste, an der man einen Plan Grows (Düngeplan) oder Trockenzelten (Trocknungsplan)
 * zuweist. EINE Darstellung für beide Pläne — nur die Zeilen und die Knöpfe darunter unterscheiden sich.
 * Einklappbar im Muster der übrigen Seiten (`KlappBereich`, Zustand je Gerät gemerkt).
 */
export function GiltFuer({ titel, merken, audit, zeilen, onUmschalten, leer, hinweis, children }: {
  titel: string
  /** Kennung, unter der das Ein- und Ausklappen am Gerät gemerkt wird. */
  merken: string
  audit: string
  zeilen: readonly GiltZeile[]
  onUmschalten: (id: number) => void
  /** Satz, wenn es keine Zeile gibt (kein Grow, kein weiteres Trockenzelt). */
  leer: string
  hinweis?: ReactNode
  children?: ReactNode
}) {
  const angehakt = zeilen.filter((z) => z.angehakt).length
  return (
    <KlappBereich
      titel={titel}
      kurz={zeilen.length === 0 ? undefined : `${angehakt} von ${zeilen.length}`}
      merken={merken}
      offen
      audit={audit}
      className="gf-bereich"
    >
      {zeilen.length === 0 ? (
        <p className="gf-hinweis">{leer}</p>
      ) : (
        <ul className="gf-liste" aria-label={titel}>
          {zeilen.map((z) => (
            <li key={z.id}>
              <label className="gf-zeile" data-audit="gilt-zeile">
                <input
                  type="checkbox"
                  checked={z.angehakt}
                  disabled={z.gesperrt}
                  onChange={() => onUmschalten(z.id)}
                />
                <span className="gf-text">
                  <b>{z.name}</b>
                  <small>{z.unter}</small>
                  {z.zusatz && <small className={z.zusatzTon === 'ok' ? 'gf-zusatz is-ok' : 'gf-zusatz is-warn'}>{z.zusatz}</small>}
                </span>
              </label>
            </li>
          ))}
        </ul>
      )}
      {hinweis && <p className="gf-hinweis">{hinweis}</p>}
      {children && <div className="gf-knoepfe">{children}</div>}
    </KlappBereich>
  )
}
