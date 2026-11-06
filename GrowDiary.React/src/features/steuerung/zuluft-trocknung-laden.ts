import { useCallback, useEffect, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import type { ZuluftTrocknung } from './zuluft-trocknung-text'

/** Lädt den Stand der Zuluft im Trocknungsbetrieb und frischt ihn jede Minute auf (der Fork prüft selbst im Minutentakt). */
export function useZuluftTrocknung() {
  const [stand, setStand] = useState<ZuluftTrocknung | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)

  const laden = useCallback(async (signal?: AbortSignal) => {
    try {
      const geladen = await apiFetch<ZuluftTrocknung>('/api/trocknung/zuluft', { signal })
      if (!signal?.aborted) { setStand(geladen); setFehler(null) }
    } catch (caught) {
      if (!signal?.aborted) setFehler(formatApiError(caught, 'Die Zuluft im Trocknungsbetrieb ließ sich nicht laden.'))
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    const erster = window.setTimeout(() => { void laden(controller.signal) }, 0)
    const uhr = window.setInterval(() => { void laden(controller.signal) }, 60000)
    return () => { controller.abort(); window.clearTimeout(erster); window.clearInterval(uhr) }
  }, [laden])

  return { stand, setStand, fehler, setFehler }
}
