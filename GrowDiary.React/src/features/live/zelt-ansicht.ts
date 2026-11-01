import { wegZurRoutine } from '../changeouts/routine-weg'
import { buildPhaseTimeline, currentPhaseLabel } from '../grows/phase-timeline'
import { jetztWocheText, waehlePlan, type WochenPlan } from '../zielwerte/wochen-zeile'
import type { MetricPayload, TentDto } from '../../types'
import type { LiveTask } from './LiveScreen'
import { urteilFuerMetrik } from './metric-tile-model'
import { messZeitpunkt } from './mess-zeitpunkt'
import {
  buildScore,
  climateMetricKeys,
  findMetric,
  hydroMetricKeys,
  mapMetrics,
  riskRank,
  type LiveState,
} from './live-model'

/**
 * Alles, was die Live-Seite für EIN Zelt aus den geladenen Daten ableitet.
 *
 * Früher stand das mitten in der Seite und galt für „das gewählte Zelt". Jetzt
 * hat jedes Zelt seinen Abschnitt — der zugeklappte Kopf (Score, Kernwerte) und
 * der aufgeklappte Inhalt lesen dieselbe Ableitung, damit der Kopf nie etwas
 * anderes sagt als der Inhalt darunter. Reine Funktion, kein Netz, keine Zustände.
 */
export function baueZeltAnsicht(tent: TentDto, state: LiveState, wochenplaene: WochenPlan[]) {
  const live = state.liveByTentId[tent.id]
  const metriken: MetricPayload[] = live?.metrics ?? []
  const activeGrows = state.grows.filter((grow) => grow.status === 'Running' || grow.status === 'Planning')
  const primaryGrow = activeGrows.find((grow) => grow.tentId === tent.id) ?? null
  const weekLine = primaryGrow ? jetztWocheText(waehlePlan(wochenplaene.filter((plan) => plan.growId === primaryGrow.id), primaryGrow.id)) : null
  const score = buildScore(metriken, tent)
  const climateMetrics = mapMetrics(metriken, climateMetricKeys)
  // Fuellstand wird entweder in Litern ODER in Zentimetern gemessen — beide
  // Kacheln zu zeigen heisst, dass eine davon immer leer ist. Es gewinnt die,
  // die einen Wert hat; ohne beides bleibt die Liter-Kachel als Platzhalter.
  const hydroAlle = mapMetrics(metriken, hydroMetricKeys)
  const hatWert = (key: string) => {
    const metric = hydroAlle.find((item) => item.key === key)
    return Boolean(metric && metric.value && metric.value !== '–')
  }
  const fuellstandKey = hatWert('reservoir-level') ? 'reservoir-level'
    : hatWert('reservoir-level-cm') ? 'reservoir-level-cm'
      : 'reservoir-level'
  const hasHydroGrow = primaryGrow ? primaryGrow.hydroStyle === 'DWC' || primaryGrow.hydroStyle === 'RDWC' : false
  // Ein Zelt ohne Nährlösung (Trockenzelt, Erde) hat kein Band mit lauter
  // Platzhaltern: leer ist hier „gibt es nicht", nicht „Sonde ausgefallen".
  // Läuft ein Hydro-Grow im Zelt, bleiben die Platzhalter stehen — dort heißt
  // ein fehlender Wert, dass etwas fehlt.
  const hydroGefiltert = hydroAlle.filter((metric) =>
    !metric.key.startsWith('reservoir-level') || metric.key === fuellstandKey)
  const hydroMetrics = hasHydroGrow || hydroGefiltert.some((metric) => metric.value && metric.value !== '–')
    ? hydroGefiltert
    : []
  const lightMetric = findMetric(metriken, ['light-cycle', 'ppfd'])
  // Strom hängt ganz hinten dran — und nur, wenn die Kostenseite eine Leistung liefert.
  const stromMetric = findMetric(metriken, ['power'])
  const climateForScreen = climateMetrics.concat(
    lightMetric ? [{ ...lightMetric, label: lightMetric.key === 'ppfd' ? 'PPFD' : 'Licht' }] : [],
    stromMetric ? [{ ...stromMetric, label: 'Strom' }] : [])
  const risksForContext = state.risks
    .filter((risk) => risk.status === 'Open' || risk.status === 'Acknowledged')
    .filter((risk) => (primaryGrow ? risk.growId === primaryGrow.id : false) || risk.tentId === tent.id)
    .sort((a, b) => riskRank(a.severity) - riskRank(b.severity) || a.startedAtUtc.localeCompare(b.startedAtUtc))

  const sensorsLive = metriken.filter((metric) => metric.value && metric.value !== '–').length

  // Die Score-Zeile: statt erfundener Teilscores nennt sie, was tatsaechlich
  // danebenliegt. Ein zurueckgerechnetes Ziel (Luft/Feuchte aus dem VPD-Ziel)
  // beschreibt dieselbe Lage wie VPD selbst und wird nicht zusaetzlich gezaehlt.
  const bewertbar = [...climateMetrics, ...hydroMetrics]
    .filter((metric) => metric.numericValue != null && !metric.targetDerived && (metric.targetMin != null || metric.targetMax != null))
  const abweichungen = bewertbar.filter((metric) => urteilFuerMetrik(metric) !== 'ok')
  const scoreParts = bewertbar.length === 0
    ? 'Keine Zielwerte — ohne aktiven Grow im Zelt gibt es nichts zu vergleichen'
    : abweichungen.length === 0
      ? `Alle ${bewertbar.length} bewerteten Werte im Zielband`
      : `${abweichungen.length} ${abweichungen.length === 1 ? 'Wert' : 'Werte'} daneben: ${abweichungen.map((metric) => metric.label).join(', ')}`

  const timeline = buildPhaseTimeline(primaryGrow)
  const lastMeasurement = primaryGrow?.latestMeasurementAt
    ? messZeitpunkt(primaryGrow.latestMeasurementAt) || null
    : null
  const stageLine = primaryGrow ? currentPhaseLabel(timeline) : null
  const plantLine = primaryGrow?.plantCount ? `${primaryGrow.plantCount} Pflanzen` : null

  // Heute faellig: was die Watchdog-Risiken und der Addback-Bedarf hergeben.
  const tasks: LiveTask[] = []
  if (hasHydroGrow && primaryGrow) {
    const ec = hydroMetrics.find((metric) => metric.key === 'reservoir-ec')
    if (ec?.numericValue != null && ec.targetMax != null && ec.numericValue > ec.targetMax) {
      tasks.push({
        id: 'addback',
        when: 'fällig',
        title: `Addback · EC ${ec.numericValue.toFixed(2).replace('.', ',')} → ${ec.targetMax.toFixed(2).replace('.', ',')}`,
        action: 'Start',
        to: `/addback?growId=${primaryGrow.id}`,
        due: true,
      })
    }
  }
  // Ueberfaellige Routinen des angezeigten Grows. Sie standen bisher nur auf
  // /aufgaben — hier fehlten sie, obwohl derselbe Block „Heute faellig" heisst.
  for (const routine of (primaryGrow ? state.faelligeRoutinen[primaryGrow.id] ?? [] : []).slice(0, 3 - tasks.length)) {
    // Hat die Routine eine eigene Seite, fuehrt der Knopf dorthin und heisst
    // nach der Handlung. „Oeffnen → /aufgaben" war eine Sackgasse: dort steht
    // die Mahnung noch einmal, aber nicht das Formular.
    const weg = wegZurRoutine(routine.sopId)
    tasks.push({
      id: `routine-${routine.sopId}`,
      when: routine.severity === 'critical' ? 'überfällig' : 'fällig',
      title: routine.name,
      action: weg?.aktion ?? 'Öffnen',
      to: weg?.to ?? '/aufgaben',
      due: true,
    })
  }
  for (const risk of risksForContext.slice(0, 3 - tasks.length)) {
    tasks.push({
      id: `risk-${risk.id}`,
      when: risk.severity === 'Critical' ? 'jetzt' : 'offen',
      title: risk.title,
      action: 'Öffnen',
      to: '/diagnose',
      due: risk.severity === 'Critical',
    })
  }

  return {
    live,
    metriken,
    primaryGrow,
    weekLine,
    score,
    scoreParts,
    climateForScreen,
    hydroMetrics,
    risksForContext,
    sensorsLive,
    timeline,
    lastMeasurement,
    stageLine,
    plantLine,
    tasks,
  }
}

export type ZeltAnsicht = ReturnType<typeof baueZeltAnsicht>
