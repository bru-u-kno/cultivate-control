/* src/useLetzteSeite.ts — Cultivate Control
   Merkt sich die geöffnete Seite und stellt sie nach einem Neuladen des iframes wieder her (Begründung: letzte-seite.ts). */

import { useEffect, useRef } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { beruehreSeite, merkeSeite, wiederherstellungsZiel } from './letzte-seite'

/** Ein Ingress-Panel mit ?index= in der HA-Adresse hat ein eigenes Ziel (useHaTiefenlink) — dann nichts wiederherstellen. */
function haHatEigenesZiel(): boolean {
  if (window.parent === window) return false
  try {
    return new URLSearchParams(window.parent.location.search).has('index')
  } catch {
    return false
  }
}

export function useLetzteSeite(): void {
  const { pathname, search, hash } = useLocation()
  const navigate = useNavigate()
  const start = useRef(true)
  // Schon im ersten Render lesen: useHaTiefenlink löscht ?index= in seinem Effekt aus der HA-Adresse, noch bevor
  // der Effekt hier läuft (Prüfer cc.295). Renderphase kommt vor allen Effekten.
  const haZiel = useRef<boolean | null>(null)
  if (haZiel.current === null) haZiel.current = haHatEigenesZiel()

  useEffect(() => {
    if (start.current) {
      start.current = false
      const ziel = haZiel.current ? null : wiederherstellungsZiel(pathname, search, hash)
      // Der Wechsel löst diesen Effekt mit der neuen Adresse erneut aus und merkt sie dann selbst.
      if (ziel) { navigate(ziel, { replace: true }); return }
    }
    merkeSeite(pathname + search)
  }, [pathname, search, hash, navigate])

  // Die Zeit der letzten Nutzung mitführen: wer lange auf einer Seite liest, soll nach dem Neuladen dort bleiben.
  // Gezählt wird echte Bedienung, nicht Sichtbarkeit — beim Neuladen selbst feuern „hidden“ und „pagehide“, und ein
  // Neuladen nach Stunden im Hintergrund würde sich sonst als „gerade benutzt“ ausgeben.
  useEffect(() => {
    let zuletzt = 0
    const beruehren = () => {
      const jetzt = Date.now()
      if (jetzt - zuletzt < 15_000) return
      zuletzt = jetzt
      beruehreSeite(jetzt)
    }
    const ereignisse = ['pointerdown', 'keydown', 'scroll'] as const
    for (const e of ereignisse) window.addEventListener(e, beruehren, { passive: true, capture: true })
    return () => { for (const e of ereignisse) window.removeEventListener(e, beruehren, { capture: true }) }
  }, [])
}
