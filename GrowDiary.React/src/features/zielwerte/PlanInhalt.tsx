import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { apiFetch } from '../../api'
import { V1Alert, V1Skeleton, V1Tabs } from '../../components/v1'
import { V1Select, type V1Option } from '../../components/V1Select'
import { PlanReiter } from './PlanReiter'
import { ProgrammReiter } from './ProgrammReiter'
import { TrocknungReiter } from './TrocknungReiter'
import { WochenZeile } from './WochenZeile'
import { hatRaster, programmZeile, type ProgrammKurz } from './programm-plan'

/** Der Wert der Auswahl für „der Plan des laufenden Grows“ — kein Programm in der Adresse. */
const GROW = '__grow__'

/**
 * Die Einträge der Auswahl „Plan von“: erst der laufende Grow, dann die mitgelieferten Vorlagen, dann
 * die eigenen Programme. Eine Auswahlliste statt Reitern, weil die Zahl der eigenen Programme wächst —
 * Reiter umbrechen am Telefon (Regel des Projekts) und schöben den Plan aus dem Bild.
 */
function planVonOptionen(liste: ProgrammKurz[]): V1Option[] {
  return [
    { wert: GROW, text: 'Laufender Grow', hinweis: 'Der Plan deines Grows, Woche für Woche', betont: true },
    ...liste.map((p): V1Option => ({
      wert: p.id,
      text: p.name,
      hinweis: programmZeile(p),
      gruppe: p.eigen ? 'Eigene Programme' : 'Mitgelieferte Vorlagen',
    })),
  ]
}

type Bereich = 'wochen' | 'trocknung'

/**
 * Fork AI (A-029, Etappe 1): der Inhalt der Seite „Plan“ — mit der Auswahl
 * „Plan von“.
 *
 * Bis dahin zeigte die Seite nur den Plan des laufenden Grows; zwischen zwei
 * Grows stand dort „Kein laufender Grow“, und die Programme (SKX, Athena,
 * eigene …) waren nirgends änderbar. Jetzt wählt man oben, welchen Plan man
 * sieht. Das Programm steht als `?programm=…` in der Adresse, damit sich ein
 * Link darauf öffnen lässt.
 */
export function PlanInhalt() {
  const [params, setParams] = useSearchParams()
  const programmId = params.get('programm')
  const [liste, setListe] = useState<ProgrammKurz[] | null>(null)
  // Der Satz nach dem Anlegen einer Kopie: der Reiter wird für die Kopie neu aufgebaut und bekommt ihn hier mit.
  // Er gehört zu GENAU dieser Kopie — nach „Zurück“ auf ein anderes Programm hat er dort nichts verloren.
  const [hinweis, setHinweis] = useState<{ programmId: string; text: string } | null>(null)

  // Nach dem Anlegen einer Kopie ändert sich `programmId` — dann steht sie auch in der Liste.
  useEffect(() => {
    let aktiv = true
    apiFetch<ProgrammKurz[]>('/api/programme')
      .then((antwort) => { if (aktiv) setListe(antwort) })
      .catch(() => { if (aktiv) setListe([]) }) // ohne Liste bleibt der Plan des Grows erreichbar
    return () => { aktiv = false }
  }, [programmId])

  function waehlen(wert: string) {
    setHinweis(null)
    setParams((alt) => {
      const neu = new URLSearchParams(alt)
      neu.delete('woche')
      if (wert === GROW) neu.delete('programm')
      else neu.set('programm', wert)
      return neu
    })
  }

  async function kopieOeffnen(neueId: string, text: string) {
    // Erst die Liste neu holen, dann wechseln: sonst kennt die Seite die Kopie noch nicht und
    // meldet für einen Augenblick „Dieses Programm gibt es nicht“.
    try {
      setListe(await apiFetch<ProgrammKurz[]>('/api/programme'))
    } catch {
      // Ohne neue Liste wechselt die Seite trotzdem; der Effekt holt sie nach.
    }
    setHinweis({ programmId: neueId, text })
    setParams((alt) => {
      const neu = new URLSearchParams(alt)
      neu.set('programm', neueId)
      return neu
    }, { replace: true })
  }

  const gewaehlt = programmId ? liste?.find((p) => p.id === programmId) : undefined
  // Ein Programm ohne Wochenwerte (Canna Aqua, VBX) öffnet auf der Trocknung — dort gibt es etwas zu ändern.
  const bereich: Bereich = params.get('bereich') === 'wochen' ? 'wochen'
    : params.get('bereich') === 'trocknung' || (gewaehlt && !hatRaster(gewaehlt)) ? 'trocknung' : 'wochen'

  function bereichWaehlen(wert: Bereich) {
    setHinweis(null)
    setParams((alt) => {
      const neu = new URLSearchParams(alt)
      neu.set('bereich', wert)
      return neu
    }, { replace: true })
  }

  return (
    <>
      {liste && liste.length > 0 && (
        <div data-audit="plan-von">
          <V1Select
            label="Plan von"
            titel="Plan von"
            unterzeile="Der Plan deines Grows oder ein Programm"
            wert={programmId ?? GROW}
            onWahl={waehlen}
            optionen={planVonOptionen(liste)}
          />
        </div>
      )}

      {!programmId && (
        <>
          <WochenZeile />
          <PlanReiter />
        </>
      )}
      {programmId && liste === null && <V1Skeleton rows={6} label="Lade Programm" />}
      {programmId && liste !== null && !gewaehlt && (
        <V1Alert tone="warn" message="Dieses Programm gibt es nicht (mehr). Wähle oben ein anderes." />
      )}
      {gewaehlt && (
        <V1Tabs
          label="Bereich"
          active={bereich}
          onChange={bereichWaehlen}
          items={[
            { value: 'wochen' as Bereich, label: 'Wochen', audit: 'plan-bereich-wochen' },
            { value: 'trocknung' as Bereich, label: 'Trocknung', audit: 'plan-bereich-trocknung' },
          ]}
        />
      )}
      {gewaehlt && bereich === 'wochen' && !hatRaster(gewaehlt) && (
        <V1Alert tone="neutral" title={gewaehlt.name} message={`${programmZeile(gewaehlt)}. Hier gibt es keine Wochenwerte zu ändern — die Trocknung kannst du trotzdem einstellen.`} />
      )}
      {gewaehlt && bereich === 'wochen' && hatRaster(gewaehlt) && (
        <ProgrammReiter key={gewaehlt.id} programmId={gewaehlt.id} startMeldung={hinweis?.programmId === gewaehlt.id ? hinweis.text : null} onKopie={(id, text) => void kopieOeffnen(id, text)} />
      )}
      {gewaehlt && bereich === 'trocknung' && (
        <TrocknungReiter key={gewaehlt.id} programmId={gewaehlt.id} startMeldung={hinweis?.programmId === gewaehlt.id ? hinweis.text : null} onKopie={(id, text) => void kopieOeffnen(id, text)} />
      )}
    </>
  )
}
