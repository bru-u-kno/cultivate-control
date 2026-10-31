import { apiFetch } from '../../api'

/** Fork AI: Eine Reinigung, die nach einem abgeschlossenen Grow empfohlen wird (`GET /api/reinigung-nach-grow`). */
export interface ReinigungNachGrow {
  growId: number
  growName: string
  zeltName?: string | null
  sopId: string
  sopName: string
  meldung: string
  /** Läuft die Reinigung schon, steht hier ihre Instanz — dann gibt es nichts mehr zu starten. */
  aktiveInstanzId?: number | null
}

export const ladeReinigungen = () => apiFetch<ReinigungNachGrow[]>('/api/reinigung-nach-grow')

/**
 * Startet die empfohlene Reinigung für den Grow. Läuft sie schon, wird keine zweite angelegt
 * (das Backend lehnt es ohnehin ab: „Für diesen Grow ist diese SOP bereits aktiv“).
 */
export async function starteReinigung(r: ReinigungNachGrow): Promise<void> {
  if (r.aktiveInstanzId != null) return
  await apiFetch('/api/sop-instances/start', {
    method: 'POST',
    body: JSON.stringify({ growId: r.growId, sopId: r.sopId, source: 'Recommendation' }),
  })
}

/** Wo die Reinigung erledigt wird: im Reiter „SOPs“ des Grows, wo seine laufenden Routinen stehen (nicht auf der Übersicht, die davon nichts zeigt). */
export const reinigungsWeg = (r: ReinigungNachGrow) => `/sops?growId=${r.growId}`
