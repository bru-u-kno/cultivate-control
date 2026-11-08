import { useEffect, useState } from 'react'
import { apiFetch } from '../../api'
import { V1Card, V1LinkButton } from '../../components/v1'
import { rollenPfad } from '../geraete/rollenPfad'
import type { ControllerStand, ControllerWaechterAntwort } from './steuerung-typen'
import './steuerung.css'

function uhrzeit(utc: string): string {
  return new Date(utc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })
}

/**
 * Die Marke des Controller-Wächters auf einer Steuerungsseite (08.11.2026): „Controller online" oder
 * „offline seit 11:54", mit Weg zu den Einstellungen unter Geräte › Rollen. Der Wächter gehört zum Controller,
 * nicht zur Seite — deshalb steht hier nur, was er sieht. Ohne eingetragenen Controller erscheint nichts.
 */
export function ControllerMarke() {
  const [liste, setListe] = useState<ControllerStand[]>([])

  useEffect(() => {
    const abbruch = new AbortController()
    const laden = async () => {
      try {
        const antwort = await apiFetch<ControllerWaechterAntwort>('/api/steuerung/controller-waechter', { signal: abbruch.signal })
        if (!abbruch.signal.aborted) setListe(antwort.controller.filter((s) => s.controller.aktiv))
      } catch {
        // Die Marke ist eine Zugabe: Fehlt sie, fehlt nur sie.
      }
    }
    void laden()
    const takt = window.setInterval(() => { void laden() }, 30000)
    return () => { abbruch.abort(); window.clearInterval(takt) }
  }, [])

  return (
    <>
      {liste.map((s) => {
        const schlecht = s.befund === 'Offline' || s.befund === 'Eingefroren'
        const marke = s.befund === 'Gesund'
          ? { ton: 'is-ok', text: 'Controller online' }
          : schlecht
            ? { ton: 'is-warn', text: `${s.befund === 'Offline' ? 'Controller offline' : 'Feuchte eingefroren'}${s.seitUtc ? ` seit ${uhrzeit(s.seitUtc)}` : ''}` }
            : { ton: '', text: 'Controller nicht prüfbar' }
        const c = s.controller
        const klein = schlecht
          ? 'Die Feuchte wird nicht mehr gemessen. Der Wächter hat gemeldet.'
          : s.pausiert
            ? 'Der Wächter pausiert.'
            : `Wächter aktiv: Meldung nach ${c.meldenNachMin} Min.${c.modus === 'neustart' && c.steckdoseEntity ? `, Neustart nach ${c.neustartNachMin} Min.` : ''}`
        return (
          <V1Card key={c.id}>
            <div className="st-marken"><span className={`st-marke ${marke.ton}`.trim()}>{marke.text}</span></div>
            <div className="st-feldzeile">
              <span className="st-etikett">{c.name}<small>{klein}</small></span>
              <V1LinkButton to={rollenPfad('controller-waechter')} variant="ghost">Wächter ›</V1LinkButton>
            </div>
          </V1Card>
        )
      })}
    </>
  )
}
