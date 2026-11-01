import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1LinkButton, V1Skeleton } from '../../components/v1'
import { V1Select } from '../../components/V1Select'
import type { TentDto } from '../../types/automation'
import { TrocknungsplanReiter } from './TrocknungsplanReiter'

/**
 * Plan › Trocknung: wählt das Trockenzelt (bei genau einem gibt es nichts zu wählen) und zeigt dessen Plan.
 * Das Zelt steht als `?zelt=…` in der Adresse.
 */
export function TrocknungsplanSeite() {
  const [params, setParams] = useSearchParams()
  const [zelte, setZelte] = useState<TentDto[] | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)

  useEffect(() => {
    let aktiv = true
    apiFetch<TentDto[]>('/api/settings/tents')
      .then((liste) => { if (aktiv) setZelte(liste.filter((z) => z.tentType === 'Drying')) })
      .catch((caught) => { if (aktiv) setFehler(formatApiError(caught, 'Die Zelte konnten nicht geladen werden.')) })
    return () => { aktiv = false }
  }, [])

  if (fehler) return <V1Alert tone="critical" message={fehler} />
  if (zelte === null) return <V1Skeleton rows={4} label="Lade Zelte" />
  if (zelte.length === 0) {
    return (
      <>
        <V1Alert
          tone="neutral"
          title="Noch kein Trockenzelt"
          message="Der Trocknungsplan gehört einem Zelt mit dem Zweck „Trocknung“. Lege eines an oder stelle den Zweck eines Zelts darauf um."
        />
        <V1LinkButton to="/zelte" variant="secondary">Zu den Zelten ›</V1LinkButton>
      </>
    )
  }

  const gewaehlt = zelte.find((z) => String(z.id) === params.get('zelt')) ?? zelte[0]
  return (
    <>
      {zelte.length > 1 && (
        <div data-audit="trocknung-zeltwahl">
          <V1Select
            label="Zelt"
            titel="Zelt"
            wert={String(gewaehlt.id)}
            onWahl={(wert) => setParams((alt) => { const neu = new URLSearchParams(alt); neu.set('zelt', wert); return neu }, { replace: true })}
            optionen={zelte.map((z) => ({ wert: String(z.id), text: z.name }))}
          />
        </div>
      )}
      <TrocknungsplanReiter key={gewaehlt.id} zeltId={gewaehlt.id} weitereZelte={zelte.filter((z) => z.id !== gewaehlt.id).map((z) => ({ id: z.id, name: z.name }))} />
    </>
  )
}
