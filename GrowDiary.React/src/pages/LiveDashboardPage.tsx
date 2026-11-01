import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { apiFetch, formatApiError } from '../api'
import type { GrowSummary, RiskEventDto, TentDto, TentLivePayload } from '../types'
import { LiveZelt, LiveZeltInhalt } from '../features/live/LiveZelt'
import { baueZeltAnsicht } from '../features/live/zelt-ansicht'
import { useZeltOffen } from '../features/live/zelt-offen'
import type { WochenPlan } from '../features/zielwerte/wochen-zeile'
import '../features/live/live-screen.css'
import { V1Skeleton } from '../components/v1'
import { VERBINDUNG_PFAD } from '../features/geraete/technik-pfade'
import {
  chooseInitialTent,
  initialLiveState,
  type LiveState,
} from '../features/live/live-model'

function LiveDashboardPage() {
  const [state, setState] = useState<LiveState>(initialLiveState)
  // Ein Link auf /live/<zelt> (Push-Meldung) klappt das Zelt auf und springt hin.
  // Je Navigation einmal — danach klappt man frei auf und zu. Angepasst während
  // des Renderns statt im Effekt, damit nicht erst das falsche Bild aufblitzt.
  const location = useLocation()
  const zeltAusLink = (location.state as { zeltId?: unknown } | null)?.zeltId
  const [linkGelesen, setLinkGelesen] = useState<string | null>(null)
  // Neues Objekt je Link, damit auch ein zweiter Link auf dasselbe Zelt wieder hinspringt.
  const [sprung, setSprung] = useState<{ id: number } | null>(null)
  const zelteOffen = useZeltOffen()
  if (typeof zeltAusLink === 'number' && linkGelesen !== location.key) {
    setLinkGelesen(location.key)
    setSprung({ id: zeltAusLink })
    // Das Zelt der Meldung bleibt offen, wie wenn man es selbst aufgeklappt hätte —
    // ein späterer Link auf ein anderes Zelt klappt es nicht wieder zu.
    zelteOffen.setze(zeltAusLink, true)
  }
  // Danach den Zustand aus dem Verlaufseintrag nehmen. Sonst springt die
  // Zurück-Taste (Kachel antippen, zurück) wieder zum Zelt aus der Meldung.
  const navigate = useNavigate()
  useEffect(() => {
    if (typeof zeltAusLink === 'number') navigate(location.pathname + location.search, { replace: true, state: null })
  }, [zeltAusLink, location.pathname, location.search, navigate])
  const [loading, setLoading] = useState(true)
  const [refresh, setRefresh] = useState(0)
  // Der Watchdog meldet, wenn die Überwachung SELBST schweigt — das gehört auf
  // Live, weil frische Zahlen eines Zelts verdecken, dass ein anderes dunkel
  // ist. Nur Probleme erscheinen; „alles wach" wäre Dauerrauschen.
  const [systemWarning, setSystemWarning] = useState<{ headline: string; detail: string } | null>(null)

  // Mirror the latest committed state so a background refresh can fall back to the
  // last good data instead of blanking out when a request fails transiently.
  const stateRef = useRef(state)
  useEffect(() => { stateRef.current = state }, [state])

  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      // Note: don't flip `loading` back on for background refreshes — the
      // initial useState(true) covers first paint, and keeping it false on the
      // 30s tick lets the dashboard update in place instead of blanking out.
      const previous = stateRef.current
      const issues: string[] = []
      // Report whether the call succeeded so a transient failure keeps the last
      // good value instead of overwriting it with an empty fallback (which made
      // sensor values vanish until the page was re-opened).
      const attempt = async <T,>(name: string, path: string): Promise<{ ok: boolean; value: T | null }> => {
        try { return { ok: true, value: await apiFetch<T>(path, { signal: controller.signal }) } }
        catch (caught) {
          if (!controller.signal.aborted) issues.push(`${name}: ${formatApiError(caught, 'nicht erreichbar')}`)
          return { ok: false, value: null }
        }
      }

      /**
       * Faellige Routinen aus den Ablaeufen.
       *
       * Der Block „Heute faellig" speiste sich nur aus Addback-Bedarf und
       * Risiken — die ueberfaelligen Routinen fehlten. Ein Klick auf „Alle"
       * fuehrte dann auf eine Seite, die drei weitere Punkte zeigte, die es
       * hier angeblich nicht gab. Zwei Zahlen zur selben Frage, die sich
       * widersprechen.
       */
      type FaelligeRoutine = { sopId: string; name: string; severity: string; meldung: string }

      const [tentsResult, growsResult, risksResult, watchdogResult] = await Promise.all([
        attempt<TentDto[]>('Zelte', '/api/settings/tents'),
        attempt<GrowSummary[]>('Grows', '/api/grows?archived=false'),
        attempt<RiskEventDto[]>('Risiken', '/api/risk-events?openOnly=true'),
        // Kein `attempt`: ein scheiternder Watchdog-Abruf ist kein Seitenfehler.
        apiFetch<{ headline: string; detail: string; isProblem: boolean }>('/api/notifications/watchdog', { signal: controller.signal }).catch(() => null),
      ])

      const sorted = tentsResult.ok
        ? [...(tentsResult.value ?? [])].sort((a, b) => a.displayOrder - b.displayOrder || a.name.localeCompare(b.name))
        : previous.tents
      const grows = growsResult.ok ? (growsResult.value ?? []) : previous.grows
      const risks = risksResult.ok ? (risksResult.value ?? []) : previous.risks

      // Je laufendem Grow. Scheitert einer, bleibt seine Liste leer — der
      // Rest der Seite haengt nicht daran.
      const routinenJeGrow = new Map<number, FaelligeRoutine[]>()
      await Promise.all(grows
        .filter((grow) => grow.status === 'Running')
        .map(async (grow) => {
          try {
            const liste = await apiFetch<FaelligeRoutine[]>(`/api/grows/${grow.id}/due-sops`, { signal: controller.signal })
            routinenJeGrow.set(grow.id, liste)
          } catch {
            /* Eine fehlende Routinenliste ist kein Seitenfehler. */
          }
        }))

      const livePairs = await Promise.all(sorted.map(async (tent) => {
        try { return [tent.id, await apiFetch<TentLivePayload>(`/api/live/tents/${tent.id}`, { signal: controller.signal })] as const }
        catch { return [tent.id, null] as const }
      }))

      if (controller.signal.aborted) return
      // Merge into the previous live map so a tent whose refresh failed keeps its
      // last good payload instead of dropping to empty. Only keep entries for
      // tents that still exist.
      const freshLive = Object.fromEntries(livePairs.filter((pair): pair is readonly [number, TentLivePayload] => pair[1] !== null))
      const liveByTentId: Record<number, TentLivePayload> = {}
      for (const tent of sorted) {
        const merged = freshLive[tent.id] ?? previous.liveByTentId[tent.id]
        if (merged) liveByTentId[tent.id] = merged
      }
      setState({ tents: sorted, grows, risks, liveByTentId, faelligeRoutinen: Object.fromEntries(routinenJeGrow), issues })
      setSystemWarning(watchdogResult?.isProblem ? { headline: watchdogResult.headline, detail: watchdogResult.detail } : null)
      setLoading(false)
    }
    void load()
    return () => controller.abort()
  }, [refresh])

  useEffect(() => {
    const id = window.setInterval(() => setRefresh((value) => value + 1), 30000)
    return () => window.clearInterval(id)
  }, [])

  // Die Plan-Woche für den Kopf: derselbe Wochenplan wie die Wochenzeile. Die
  // Zeile ist eine Zugabe — scheitert das Laden, fehlt sie, die Seite bleibt.
  const [wochenplaene, setWochenplaene] = useState<WochenPlan[]>([])
  useEffect(() => {
    let aktiv = true
    apiFetch<WochenPlan[]>('/api/wochenplan')
      .then((plaene) => { if (aktiv) setWochenplaene(plaene) })
      .catch(() => {})
    return () => { aktiv = false }
  }, [refresh])

  const ansichten = useMemo(
    () => state.tents.map((tent) => ({ tent, ansicht: baueZeltAnsicht(tent, state, wochenplaene) })),
    [state, wochenplaene],
  )
  // Offen ohne Merkzettel: das Zelt mit dem laufenden Grow.
  const standardId = chooseInitialTent(state.tents, state.grows)

  // Nach dem Sprung aus einer Meldung das Zelt ins Bild holen — erst, wenn die
  // Abschnitte da sind.
  useEffect(() => {
    if (sprung == null || loading) return
    document.querySelector(`[data-zelt-id="${sprung.id}"]`)?.scrollIntoView?.({ block: 'start' })
  }, [sprung, loading])

  if (loading) {
    return (
      <main className="ls">
        <V1Skeleton tiles={4} label="Lade Zelt" />
        <V1Skeleton tiles={5} rows={3} label="Lade Messwerte" />
      </main>
    )
  }

  // Frische Installation: ohne Zelt gibt es nichts zu zeigen. Statt eines leeren
  // Cockpits, das aussieht wie ein Fehler, steht hier der Weg — in der
  // Reihenfolge, in der es gemacht werden muss.
  if (state.tents.length === 0) {
    return (
      <main className="ls" data-audit="live-first-run">
        <header className="ls-head">
          <div className="ls-head-title">
            <div className="ls-eyebrow">Jetzt / Live</div>
            <h1>Willkommen bei Cultivate Control</h1>
            <div className="ls-head-parts">Drei Schritte bis zum ersten Messwert, dann die Steuerungen.</div>
          </div>
        </header>
        <ol className="ls-firstrun">
          <li>
            <div>
              <strong>Zelt anlegen</strong>
              <span>Der Raum: Größe, Licht, Abluft. Danach lassen sich Sensoren zuordnen.</span>
            </div>
            <Link className="ls-btn is-primary" to="/zelte/new">Zelt anlegen</Link>
          </li>
          <li>
            <div>
              <strong>Hydro-System anlegen</strong>
              <span>RDWC oder DWC: Sites, Topf- und Tankgröße. Daraus rechnet der Addback.</span>
            </div>
            <Link className="ls-btn" to="/hydro/new">Hydro anlegen</Link>
          </li>
          <li>
            <div>
              <strong>Grow starten</strong>
              <span>Sorte, Zelt und System wählen — ab dann zeigt diese Seite deine Werte.</span>
            </div>
            <Link className="ls-btn" to="/grows/new">Grow starten</Link>
          </li>
          <li>
            <div>
              <strong>Steuerungen einrichten</strong>
              <span>Danach, wenn du CO₂, Entfeuchter, Zuluft, Kühler oder Lampe über Home Assistant regelst: sag, was du hast — der Rest wird nicht angelegt.</span>
            </div>
            <Link className="ls-btn" to="/steuerung/einrichtung">Einrichten</Link>
          </li>
        </ol>
        <article className="ls-panel">
          <div className="ls-panel-body">
            <p>Lieber ausführlich? Die Ersten Schritte erklären Einrichtung und Home-Assistant-Anbindung.</p>
            <div className="ls-panel-actions">
              <Link className="ls-btn" to="/start">Erste Schritte</Link>
              <Link className="ls-btn" to={VERBINDUNG_PFAD}>Home Assistant verbinden</Link>
            </div>
          </div>
        </article>
      </main>
    )
  }

  const onRefresh = () => setRefresh((current) => current + 1)

  // Ein einziges Zelt: die Seite bleibt, wie sie war — ohne Zeltkopf, mit eigenem
  // Score-Ring. Ein Kopf, der nur sich selbst aufklappt, wäre ein Umweg.
  if (ansichten.length === 1) {
    const { tent, ansicht } = ansichten[0]
    return <LiveZeltInhalt tent={tent} ansicht={ansicht} onRefresh={onRefresh} systemWarning={systemWarning} />
  }

  const sensorenGesamt = ansichten.reduce((summe, eintrag) => summe + eintrag.ansicht.sensorsLive, 0)

  return (
    <main className="ls" data-audit="live-screen">
      <div className="ls-seitenkopf">
        <h1 className="ls-eyebrow">Jetzt / Live</h1>
        <span className="ls-pill"><i />{sensorenGesamt} Sensoren live</span>
      </div>
      {systemWarning && (
        <Link className="ls-syswarn" to="/handy?tab=push" data-audit="live-system-warning">
          <span className="ls-label">Systemüberwachung</span>
          <span className="ls-syswarn-text"><strong>{systemWarning.headline}</strong> — {systemWarning.detail}</span>
        </Link>
      )}
      {ansichten.map(({ tent, ansicht }) => (
        <LiveZelt
          key={tent.id}
          tent={tent}
          ansicht={ansicht}
          offen={zelteOffen.istOffen(tent.id, standardId)}
          onUmschalten={() => zelteOffen.umschalten(tent.id, standardId)}
          onRefresh={onRefresh}
        />
      ))}
    </main>
  )
}

export default LiveDashboardPage
