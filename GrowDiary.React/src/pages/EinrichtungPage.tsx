import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { apiFetch } from '../api'
import { V1Page, V1Skeleton } from '../components/v1'
import { useKiAktiv } from '../ki-aktiv'
import { EinrichtungReiter } from '../features/einrichtung/EinrichtungReiter'
import { berechneSchritte, zaehlung } from '../features/einrichtung/schritte'
import type { EinrichtungDaten, Schritt } from '../features/einrichtung/schritte'
import type { SteuerungAuswahl } from '../features/steuerung/steuerung-typen'
import type { HomeAssistantEntity, HydroSetupDto, SettingsOverviewDto } from '../types'
import type { NotificationSettingsDto } from '../types/notification'
import '../features/einrichtung/einrichtung.css'

type Wasser = { conductivityUsCm: number | null; ph: number | null; totalHardnessDh: number | null } | null

/** Ein Abruf, der scheitern darf: `null` heißt „nicht lesbar“ — der Schritt sagt das, statt „erledigt“ zu behaupten. */
const lese = async <T,>(pfad: string, signal: AbortSignal): Promise<T | null> => apiFetch<T>(pfad, { signal }).catch(() => null)

const STATUS_TEXT: Record<Schritt['status'], string> = { erledigt: 'erledigt', offen: 'offen', optional: 'optional', unbekannt: 'nicht lesbar' }
const STATUS_KLASSE: Record<Schritt['status'], string> = { erledigt: 'is-good', offen: 'is-warn', optional: 'is-faint', unbekannt: 'is-faint' }

function Zeile({ schritt }: { schritt: Schritt }) {
  return (
    <Link to={schritt.ziel} className="co-row ei-schritt" data-audit="einrichtung-schritt" data-schritt={schritt.id}>
      <div className="co-row-text">
        <div className="co-row-title">{schritt.titel}</div>
        <div className="co-row-sub">{schritt.text}</div>
      </div>
      <span className={`co-row-value ${STATUS_KLASSE[schritt.status]}`}>{STATUS_TEXT[schritt.status]}</span>
    </Link>
  )
}

/**
 * Einrichtung (cc.287): die Schritte vom Anfang bis zum laufenden Betrieb — mit Stand, live gelesen — und die Anlage
 * (Zelte, Hydro, Wasser). Die Technik (Geräte, Zuordnung, Rollen, Räume, Inventar, Wartung, Verbindung) steht auf
 * `/geraete`; beide Seiten tragen oben dieselbe Reiterleiste. Ersetzt die statische Seite „Erste Schritte“.
 */
export default function EinrichtungPage() {
  const [params] = useSearchParams()
  const bereich = params.get('bereich') === 'anlage' ? 'anlage' : 'schritte'
  const ki = useKiAktiv()
  const [daten, setDaten] = useState<Omit<EinrichtungDaten, 'kiAktiv'> | null>(null)

  useEffect(() => {
    const abbruch = new AbortController()
    void (async () => {
      const [uebersicht, entitaeten, auswahl, hydro, wasser, push] = await Promise.all([
        lese<SettingsOverviewDto>('/api/settings', abbruch.signal),
        lese<HomeAssistantEntity[]>('/api/home-assistant/entities', abbruch.signal),
        lese<SteuerungAuswahl>('/api/steuerung/auswahl', abbruch.signal),
        lese<HydroSetupDto[]>('/api/hydro-setups', abbruch.signal),
        lese<Wasser>('/api/water-profile', abbruch.signal),
        lese<NotificationSettingsDto>('/api/notifications/settings', abbruch.signal),
      ])
      if (abbruch.signal.aborted) return
      setDaten({
        verbindung: uebersicht ? { verbunden: Boolean(uebersicht.homeAssistant.isManagedByAddon || uebersicht.homeAssistant.enabled), entitaeten: entitaeten?.length ?? null } : null,
        zelte: uebersicht ? uebersicht.tents.map((z) => ({ id: z.id, name: z.name, status: z.status, tentType: z.tentType, messgroessen: z.sensors.filter((s) => s.isActive && s.haEntityId).map((s) => s.metricType) })) : null,
        auswahl: auswahl ? auswahl.eintraege : null,
        hydro: hydro ? hydro.length : null,
        wasserErfasst: wasser === null ? null : [wasser.conductivityUsCm, wasser.ph, wasser.totalHardnessDh].some((wert) => wert != null),
        pushEingerichtet: push ? Boolean(push.notifyService && push.notifyService.trim() !== '') : null,
      })
    })()
    return () => abbruch.abort()
  }, [])

  const schritte = useMemo(() => (daten ? berechneSchritte({ ...daten, kiAktiv: ki.aktiv }) : []), [daten, ki.aktiv])
  const stand = zaehlung(schritte)
  const anlage = schritte.filter((s) => ['zelte', 'hydro', 'wasser'].includes(s.id))

  return (
    <V1Page eyebrow="Einrichtung" title="Einrichtung" subtitle="Von oben nach unten einrichten — oder direkt zu einem Bereich springen.">
      <EinrichtungReiter aktiv={bereich} />
      {!daten ? <V1Skeleton rows={5} label="Einrichtung wird gelesen" /> : bereich === 'schritte' ? (
        <>
          <div className="co-strip" data-audit="einrichtung-stand">
            <div className="co-cell">
              <div className="co-cell-label">Erledigt</div>
              <div className="co-cell-value is-md">{stand.erledigt} von {stand.gesamt}</div>
            </div>
            <div className="co-cell">
              <div className="co-cell-label">Nächster Schritt</div>
              <div className={`co-cell-value is-md${stand.naechster ? ' is-warn' : ' is-good'}`}>{stand.naechster ? stand.naechster.titel : 'alles eingerichtet'}</div>
            </div>
          </div>
          <section className="ls-panel">
            <div className="ls-panel-head"><span className="ls-label">Schritte</span><span className="ls-panel-meta">Stand wird live gelesen</span></div>
            {schritte.map((schritt) => <Zeile key={schritt.id} schritt={schritt} />)}
          </section>
        </>
      ) : (
        <section className="ls-panel" data-audit="einrichtung-anlage">
          <div className="ls-panel-head"><span className="ls-label">Anlage</span><span className="ls-panel-meta">Zelte, Hydro-Systeme und Wasser</span></div>
          {anlage.map((schritt) => <Zeile key={schritt.id} schritt={{ ...schritt, titel: schritt.titel.replace(/^\d+ · /, '') }} />)}
        </section>
      )}
    </V1Page>
  )
}
