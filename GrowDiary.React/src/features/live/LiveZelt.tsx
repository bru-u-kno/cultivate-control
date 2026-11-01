import { useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import type { TentDto } from '../../types'
import { classNames } from '../../utils'
import { LiveScreen, type DashboardPanel } from './LiveScreen'
import { ZeltKopf } from './ZeltKopf'
import { useTentDashboard } from './useTentDashboard'
import { useTentSparklines } from './useTentSparklines'
import { layoutIsEmpty, seedLayout, type DashboardLayout } from './dashboard-layout'
import type { ZeltAnsicht } from './zelt-ansicht'
import { HYDRO_KOPF, KLIMA_KOPF, kopfWerte, zeltUnterzeile } from './zelt-kopf'

/**
 * Ein Zelt auf der Live-Seite: Kopf (Score, Kernwerte) und — aufgeklappt — alles,
 * was zu genau diesem Zelt gehört (Klima, Nährlösung, Risiken, Aufgaben, Kamera,
 * Grow). So hängt jede Zahl sichtbar an ihrem Zelt, statt unter einer Auswahl
 * zu stehen, die man erst lesen muss.
 */
export function LiveZelt({ tent, ansicht, offen, onUmschalten, onRefresh }: {
  tent: TentDto
  /** Von der Seite einmal je Zelt berechnet — sie braucht die Zählung der Sensoren auch. */
  ansicht: ZeltAnsicht
  offen: boolean
  onUmschalten: () => void
  onRefresh: () => void
}) {
  const inhaltId = `live-zelt-inhalt-${tent.id}`
  return (
    <section
      className={classNames('ls-zb', !offen && 'zu')}
      data-audit="live-zelt"
      data-zelt-id={tent.id}
      aria-label={tent.name}
    >
      <ZeltKopf
        name={tent.name}
        offen={offen}
        onUmschalten={onUmschalten}
        inhaltId={inhaltId}
        score={ansicht.score}
        unterzeile={zeltUnterzeile(ansicht.stageLine, ansicht.weekLine, ansicht.metriken)}
        klima={kopfWerte(ansicht.metriken, KLIMA_KOPF)}
        hydro={kopfWerte(ansicht.metriken, HYDRO_KOPF)}
      />
      {offen && (
        <div id={inhaltId}>
          <LiveZeltInhalt tent={tent} onRefresh={onRefresh} ansicht={ansicht} eingebettet />
        </div>
      )}
    </section>
  )
}

/**
 * Der Inhalt eines Zelts — mit seinen eigenen Netz-Abrufen (Verlaufskurven,
 * eigene Anordnung). Erst gemountet, wenn das Zelt aufgeklappt ist: ein
 * zugeklapptes Zelt lädt nichts nach, sein Kopf kommt aus den Live-Werten, die
 * die Seite ohnehin für alle Zelte holt.
 */
export function LiveZeltInhalt({ tent, onRefresh, ansicht, eingebettet = false, systemWarning = null }: {
  tent: TentDto
  onRefresh: () => void
  ansicht: ZeltAnsicht
  eingebettet?: boolean
  systemWarning?: { headline: string; detail: string } | null
}) {
  // Anpassen-Modus: der Entwurf lebt hier, gespeichert wird erst auf Knopfdruck.
  // Je Zelt ein eigener Inhalt — Entwurf und Modus gehören damit von selbst zu
  // genau diesem Zelt.
  const [draft, setDraft] = useState<DashboardLayout | null>(null)
  const [editing, setEditing] = useState(false)
  const [savingLayout, setSavingLayout] = useState(false)
  const [layoutWarning, setLayoutWarning] = useState<string | null>(null)

  const { layout, entityValues, reload: reloadLayout } = useTentDashboard(tent.id)
  const trends = useTentSparklines(tent.id)

  // Das Layout, das gerade gilt: der Entwurf im Anpassen-Modus, sonst das
  // Gespeicherte. Ohne beides zeichnet der Bildschirm seine festen Reihen.
  const shownLayout = draft ?? layout
  const { climateForScreen, hydroMetrics } = ansicht

  const dashboardPanel: DashboardPanel | null = useMemo(() => {
    if (!shownLayout) return null
    return {
      layout: shownLayout,
      entityValues,
      editing,
      saving: savingLayout,
      dirty: draft != null,
      warning: layoutWarning,
      onChange: (next) => { setDraft(next); setLayoutWarning(null) },
      onSave: () => void speichern(),
      onReset: () => void zuruecksetzen(),
      onToggleEditing: () => {
        if (editing) {
          // „Fertig": ein nicht gespeicherter Entwurf wird verworfen, nicht
          // stillschweigend behalten — sonst zeigt die Seite etwas an, das
          // nirgends steht.
          setDraft(null)
          setEditing(false)
          setLayoutWarning(null)
          return
        }
        // Beim Einstieg genau das übernehmen, was gerade auf dem Schirm steht.
        setDraft(shownLayout.isCustom
          ? shownLayout
          // Ein Bereich ohne Kachel (Zelt ohne Nährlösung) wird nicht gesät: sonst steht
          // im Editor eine leere Überschrift, die auf dem Schirm vorher nicht stand.
          : seedLayout(tent.id, [
            { title: 'Klima', metrics: climateForScreen },
            { title: 'Hydroponik · Nährlösung', metrics: hydroMetrics },
          ].filter((bereich) => bereich.metrics.length > 0)))
        setEditing(true)
      },
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tent.id, shownLayout, entityValues, editing, savingLayout, draft, layoutWarning, climateForScreen, hydroMetrics])

  async function speichern() {
    if (!draft) return
    if (layoutIsEmpty(draft)) {
      // Der Server wirft ein leeres Layout weg und liefert den Standard — das
      // sähe aus, als wäre das Speichern fehlgeschlagen. Lieber vorher sagen.
      setLayoutWarning('Mindestens eine Kachel muss stehen bleiben — sonst gibt es nichts anzuzeigen.')
      return
    }
    setSavingLayout(true)
    setLayoutWarning(null)
    try {
      await apiFetch<DashboardLayout>(`/api/tents/${tent.id}/dashboard`, {
        method: 'PUT',
        body: JSON.stringify({ tentId: tent.id, sections: draft.sections }),
      })
      setDraft(null)
      setEditing(false)
      reloadLayout()
    } catch (caught) {
      setLayoutWarning(formatApiError(caught, 'Anordnung konnte nicht gespeichert werden.'))
    } finally {
      setSavingLayout(false)
    }
  }

  async function zuruecksetzen() {
    setSavingLayout(true)
    setLayoutWarning(null)
    try {
      await apiFetch(`/api/tents/${tent.id}/dashboard`, { method: 'DELETE' })
      setDraft(null)
      setEditing(false)
      reloadLayout()
    } catch (caught) {
      setLayoutWarning(formatApiError(caught, 'Zurücksetzen fehlgeschlagen.'))
    } finally {
      setSavingLayout(false)
    }
  }

  return (
    <LiveScreen
      tent={tent}
      grow={ansicht.primaryGrow}
      score={ansicht.score}
      scoreParts={ansicht.scoreParts}
      climate={ansicht.climateForScreen}
      hydro={ansicht.hydroMetrics}
      alleMetriken={ansicht.metriken}
      sensorsLive={ansicht.sensorsLive}
      lastMeasurement={ansicht.lastMeasurement}
      stageLine={ansicht.stageLine}
      weekLine={ansicht.weekLine}
      risks={ansicht.risksForContext}
      tasks={ansicht.tasks}
      timeline={ansicht.timeline.phases}
      timelineDates={ansicht.timeline.dates}
      flipIsPlanned={ansicht.timeline.flipIsPlanned}
      daysToFlip={ansicht.timeline.daysToFlip}
      plantLine={ansicht.plantLine}
      eingebettet={eingebettet}
      systemWarning={systemWarning}
      trends={trends}
      chiller={ansicht.live?.chiller ?? null}
      dashboard={dashboardPanel}
      onRefresh={onRefresh}
    />
  )
}
