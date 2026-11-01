import { useEffect, useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button } from '../../components/v1'
import { GiltFuer } from './GiltFuer'
import { zeltZeile, type TrockenzeltStand } from './plan-zuweisung'
import type { TrockenEtappe, TrocknungsplanZelt } from './trocknung-plan'

type Meldung = { text: string; ton: 'ok' | 'warn' | 'critical' }

/**
 * Plan › Trocknung › „Gilt für diese Trockenzelte“ (A-030): den Plan dieses Zelts auf weitere Trockenzelte übertragen.
 *
 * Der Trocknungsplan gehört dem Zelt, ein Zelt hat genau einen. „Zuweisen“ heißt hier: das andere Zelt bekommt diese
 * Etappen als seinen Plan und verliert den bisherigen. Es ist eine Kopie — ändert man den Plan später in einem Zelt,
 * bleibt das andere unberührt. Gibt es nur ein Trockenzelt, gibt es nichts zu wählen und der Block fehlt.
 */
export function TrocknungZuweisung({ zeltId, zeltName, etappen, ungespeichert, weitere }: {
  zeltId: number
  zeltName: string
  /** Die GESPEICHERTEN Etappen dieses Zelts — nie der Entwurf. */
  etappen: readonly TrockenEtappe[]
  ungespeichert: boolean
  /** Die übrigen Trockenzelte. */
  weitere: ReadonlyArray<{ id: number; name: string }>
}) {
  const [stand, setStand] = useState<TrockenzeltStand[]>([])
  const [gewaehlt, setGewaehlt] = useState<ReadonlySet<number>>(new Set())
  const [arbeitet, setArbeitet] = useState(false)
  const [meldung, setMeldung] = useState<Meldung | null>(null)
  const [neuLaden, setNeuLaden] = useState(0)

  // Die Seite baut die Liste bei jedem Rendern neu auf; neu geholt wird nur, wenn sich die Zelte wirklich ändern.
  const schluessel = JSON.stringify(weitere)

  useEffect(() => {
    let aktiv = true
    const liste: Array<{ id: number; name: string }> = JSON.parse(schluessel)
    Promise.all(liste.map(async (z): Promise<TrockenzeltStand> => {
      try {
        return { id: z.id, name: z.name, plan: await apiFetch<TrocknungsplanZelt>(`/api/tents/${z.id}/trocknung/plan`) }
      } catch {
        return { id: z.id, name: z.name, plan: null }
      }
    })).then((geholt) => { if (aktiv) setStand(geholt) })
    return () => { aktiv = false }
  }, [schluessel, neuLaden])

  const zeilen = useMemo(
    () => [
      zeltZeile({ id: zeltId, name: zeltName, plan: null }, true, true),
      ...stand.map((z) => zeltZeile(z, gewaehlt.has(z.id), false)),
    ],
    [stand, gewaehlt, zeltId, zeltName],
  )

  if (weitere.length === 0) return null

  function umschalten(id: number) {
    if (id === zeltId) return
    setMeldung(null)
    setGewaehlt((alt) => {
      const neu = new Set(alt)
      if (neu.has(id)) neu.delete(id)
      else neu.add(id)
      return neu
    })
  }

  async function uebertragen() {
    setArbeitet(true)
    setMeldung(null)
    const fehler: string[] = []
    const ok: string[] = []
    for (const id of gewaehlt) {
      const name = weitere.find((z) => z.id === id)?.name ?? `Zelt ${id}`
      try {
        await apiFetch<TrocknungsplanZelt>(`/api/tents/${id}/trocknung/plan`, { method: 'PUT', body: JSON.stringify({ etappen }) })
        ok.push(name)
      } catch (caught) {
        fehler.push(`${name}: ${formatApiError(caught, 'nicht übertragen.')}`)
      }
    }
    setMeldung({
      ton: fehler.length > 0 ? 'warn' : 'ok',
      text: [ok.length > 0 ? `Übertragen auf: ${ok.join(', ')}. Eine Charge, die dort gerade trocknet, folgt dem Plan sofort.` : '', ...fehler].filter(Boolean).join(' '),
    })
    setGewaehlt(new Set())
    setNeuLaden((n) => n + 1)
    setArbeitet(false)
  }

  return (
    <div data-audit="trocknung-zuweisung">
      <GiltFuer
        titel="Gilt für diese Trockenzelte"
        merken="plan-gilt-trocknung"
        audit="plan-gilt-trocknung"
        zeilen={zeilen}
        onUmschalten={umschalten}
        leer="Es gibt kein weiteres Trockenzelt."
        hinweis={ungespeichert
          ? 'Du hast den Plan geändert, aber noch nicht gespeichert. Übertragen wird der gespeicherte Plan — speichere zuerst.'
          : 'Nur Zelte mit dem Zweck „Trocknung“. Ein Zelt hat einen Trocknungsplan; wählst du es hier, bekommt es diese Etappen und verliert seinen bisherigen. Es ist eine Kopie: ändert man später eines der Zelte, bleibt das andere unberührt.'}
      >
        <V1Button variant="primary" disabled={gewaehlt.size === 0 || ungespeichert || arbeitet} onClick={() => void uebertragen()} audit="trocknung-uebertragen">
          Plan auf gewählte Zelte übertragen
        </V1Button>
      </GiltFuer>
      {meldung && <V1Alert tone={meldung.ton} message={meldung.text} />}
    </div>
  )
}
