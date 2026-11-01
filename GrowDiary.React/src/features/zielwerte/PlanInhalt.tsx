import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { apiFetch } from '../../api'
import { V1Alert, V1Skeleton, V1Tabs } from '../../components/v1'
import { V1Select, type V1Option } from '../../components/V1Select'
import { PlanReiter } from './PlanReiter'
import { ProgrammReiter } from './ProgrammReiter'
import { TrocknungsplanSeite } from './TrocknungsplanSeite'
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

/** Die zwei Pläne der Seite: der Düngeplan (Wochen, Programme) und der eigene Trocknungsplan des Trockenzelts. */
type Art = 'duenge' | 'trocknung'

/**
 * Fork AI (A-029, Etappe 1): der Inhalt der Seite „Plan“ — mit der Auswahl
 * „Plan von“. Oben der Umschalter Düngeplan | Trocknung: Der Trocknungsplan gehört dem Trockenzelt und ist
 * unabhängig vom Düngeprogramm.
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
  const art: Art = params.get('art') === 'trocknung' ? 'trocknung' : 'duenge'

  function artWaehlen(wert: Art) {
    setHinweis(null)
    setParams((alt) => {
      const neu = new URLSearchParams(alt)
      if (wert === 'trocknung') neu.set('art', 'trocknung')
      else neu.delete('art')
      return neu
    }, { replace: true })
  }

  return (
    <>
      <V1Tabs
        label="Art des Plans"
        active={art}
        onChange={artWaehlen}
        items={[
          { value: 'duenge' as Art, label: 'Düngeplan', audit: 'plan-art-duenge' },
          { value: 'trocknung' as Art, label: 'Trocknung', audit: 'plan-art-trocknung' },
        ]}
      />
      {art === 'trocknung' && <TrocknungsplanSeite />}
      {art === 'duenge' && liste && liste.length > 0 && (
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

      {art === 'duenge' && !programmId && (
        <>
          <WochenZeile />
          <PlanReiter />
        </>
      )}
      {art === 'duenge' && programmId && liste === null && <V1Skeleton rows={6} label="Lade Programm" />}
      {art === 'duenge' && programmId && liste !== null && !gewaehlt && (
        <V1Alert tone="warn" message="Dieses Programm gibt es nicht (mehr). Wähle oben ein anderes." />
      )}
      {art === 'duenge' && gewaehlt && !hatRaster(gewaehlt) && (
        <V1Alert tone="neutral" title={gewaehlt.name} message={`${programmZeile(gewaehlt)}. Hier gibt es keine Wochenwerte zu ändern. Die Trocknung hat einen eigenen Plan, unabhängig vom Düngeplan: Reiter „Trocknung“ oben.`} />
      )}
      {art === 'duenge' && gewaehlt && hatRaster(gewaehlt) && (
        <ProgrammReiter key={gewaehlt.id} programmId={gewaehlt.id} startMeldung={hinweis?.programmId === gewaehlt.id ? hinweis.text : null} onKopie={(id, text) => void kopieOeffnen(id, text)} />
      )}
    </>
  )
}
