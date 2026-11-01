import { useCallback, useEffect, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1LinkButton, V1Page, V1Section, V1Skeleton, V1Stat, V1Switch, V1Tabs } from '../../components/v1'
import { V1Sheet } from '../../components/V1Sheet'
import { SteuerungWechsel } from '../steuerung/SteuerungWechsel'
import type { SteuerungModul } from '../steuerung/steuerung-typen'
import '../steuerung/steuerung.css'
import './ziel.css'
import { PortBlatt } from './PortBlatt'
import {
  betriebText, geraetHinweis, plakette, ROLLE, wuerdeSchreiben, zahl,
  type Betrieb, type SteuerungGeraet, type TrocknungSteuerung,
} from './steuerung-text'

/**
 * Steuerung › Trocknung (A-029, Etappe 3, Teil B): Folgen die Geräte am Trockenzelt dem Plan?
 *
 * Der Fork ändert nur die Grenzen der Auslöser an den Ports des Controllers, nie deren Modus; der Controller regelt weiter selbst.
 * „Nur melden“ schreibt nichts und zeigt, was geschrieben würde. Auf „Regeln“ stellt man erst nach einer Rückfrage, die genau
 * auflistet, was geschrieben wird.
 */
export default function TrocknungDetail({ module, aktiv, onWechsel }: {
  module: SteuerungModul[]
  aktiv: string
  onWechsel: (kennung: string) => void
}) {
  const zeltId = module.find((m) => m.kennung === 'trocknung')?.zeltId ?? null
  const [stand, setStand] = useState<TrocknungSteuerung | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [meldung, setMeldung] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)
  const [laedt, setLaedt] = useState(true)
  const [rueckfrage, setRueckfrage] = useState<{ betrieb: Betrieb; entfeuchter: boolean; kuehlung: boolean } | null>(null)
  const [portBlatt, setPortBlatt] = useState(false)

  const laden = useCallback(async () => {
    if (zeltId == null) return
    setStand(await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`))
  }, [zeltId])

  useEffect(() => {
    if (zeltId == null) return
    const controller = new AbortController()
    const erstLaden = async () => {
      try {
        const geladen = await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`, { signal: controller.signal })
        if (!controller.signal.aborted) { setStand(geladen); setFehler(null) }
      } catch (caught) {
        if (!controller.signal.aborted) setFehler(formatApiError(caught, 'Die Steuerung der Trocknung konnte nicht geladen werden.'))
      } finally {
        if (!controller.signal.aborted) setLaedt(false)
      }
    }
    void erstLaden()
    return () => controller.abort()
  }, [zeltId])

  // Eine Minute Takt: Der Wert am Gerät ändert sich auch ohne uns (Controller, Etappenwechsel).
  useEffect(() => {
    const uhr = window.setInterval(() => { void laden().catch(() => undefined) }, 60000)
    return () => window.clearInterval(uhr)
  }, [laden])

  const setzen = async (betrieb: Betrieb, entfeuchter: boolean, kuehlung: boolean) => {
    if (zeltId == null) return
    setArbeitet(true); setMeldung(null)
    try {
      const neu = await apiFetch<TrocknungSteuerung>(`/api/tents/${zeltId}/trocknung/steuerung`, {
        method: 'PUT',
        body: JSON.stringify({ betrieb, entfeuchterFolgt: entfeuchter, kuehlungFolgt: kuehlung }),
      })
      setStand(neu); setFehler(null)
      setMeldung(betrieb === 'regeln' ? 'Gespeichert. Der Fork hat die Grenzen auf den Plan gesetzt.' : 'Gespeichert.')
    } catch (caught) {
      setFehler(formatApiError(caught, 'Speichern fehlgeschlagen.'))
    } finally {
      setArbeitet(false)
    }
  }

  const zuruecksetzen = async () => {
    if (zeltId == null) return
    setArbeitet(true); setMeldung(null)
    try {
      const antwort = await apiFetch<{ zurueck: number; gescheitert: number; stand: TrocknungSteuerung }>(
        `/api/tents/${zeltId}/trocknung/steuerung/zuruecksetzen`, { method: 'POST' })
      setStand(antwort.stand); setFehler(null)
      setMeldung(antwort.gescheitert > 0
        ? `${antwort.zurueck} Wert zurückgesetzt, ${antwort.gescheitert} ließ sich nicht zurücksetzen. Der Fork fasst nichts mehr an.`
        : 'Die alten Werte stehen wieder am Gerät.')
    } catch (caught) {
      setFehler(formatApiError(caught, 'Zurücksetzen fehlgeschlagen.'))
    } finally {
      setArbeitet(false)
    }
  }

  const kopf = { eyebrow: 'Steuerung', title: 'Trocknung', vorKopf: <SteuerungWechsel module={module} aktiv={aktiv} onWechsel={onWechsel} /> }
  if (zeltId == null) {
    return <V1Page {...kopf}><V1Alert tone="warn" title="Kein Trockenzelt" message="Es gibt noch kein Zelt mit dem Zweck „Trocknung“." /></V1Page>
  }
  if (laedt && !stand) return <V1Page {...kopf}><V1Skeleton rows={4} tiles={2} label="Wird geladen" /></V1Page>
  if (!stand) return <V1Page {...kopf}>{fehler && <V1Alert tone="critical" message={fehler} />}</V1Page>

  const folgt = {
    entfeuchter: stand.geraete.find((g) => g.rolle === 'entfeuchter')?.folgt ?? false,
    kuehlung: stand.geraete.find((g) => g.rolle === 'kuehlung')?.folgt ?? false,
  }
  const zugeordnet = stand.geraete.filter((g) => g.urteil !== 'nichtZugeordnet').length
  const regeln = stand.betrieb === 'regeln'
  const hatMerker = stand.geraete.some((g) => g.vorher != null)

  // Wer „Regeln“ einschaltet oder im Betrieb „Regeln“ ein Gerät dazunimmt, bekommt vorher gezeigt, was geschrieben wird.
  const anfrage = (betrieb: Betrieb, entfeuchter: boolean, kuehlung: boolean) => {
    if (betrieb === 'regeln' && wuerdeSchreiben(stand.geraete, { entfeuchter, kuehlung }).length > 0) {
      setRueckfrage({ betrieb, entfeuchter, kuehlung })
      return
    }
    void setzen(betrieb, entfeuchter, kuehlung)
  }

  const untertitel = stand.trocknet
    ? `Etappe „${stand.etappe ?? '–'}“, Tag ${stand.tag} von ${stand.tageGeplant}`
    : 'Es trocknet gerade nichts'
  const bandText = (von: number | null, bis: number | null, einheit: string) =>
    von == null || bis == null ? '–' : von === bis ? `${zahl(von)} ${einheit}` : `${zahl(von)}–${zahl(bis)} ${einheit}`

  return (
    <V1Page
      eyebrow="Steuerung"
      title="Trocknung"
      subtitle={untertitel}
      vorKopf={kopf.vorKopf}
    >
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {meldung && <V1Alert tone="ok" message={meldung} />}
      {!stand.portsErreichbar && <V1Alert tone="warn" title="Home Assistant nicht erreichbar" message="Die Werte an den Geräten lassen sich gerade nicht lesen. Der Fork schreibt in dieser Zeit nichts." />}
      <V1Alert
        tone={regeln ? 'ok' : 'neutral'}
        title={regeln ? 'Regeln: die Geräte folgen dem Plan' : 'Nur melden'}
        message={regeln
          ? 'Der Fork stellt die Grenzen am Controller auf den Plan, beim Start und bei jedem Etappenwechsel. Geregelt wird weiter vom Controller.'
          : 'Der Fork schreibt nichts an die Geräte. Er zeigt nur, was er einstellen würde.'}
      />

      <section className="v1-kpi-grid">
        <V1Stat label="Plan Luft" value={bandText(stand.tempMin, stand.tempMax, '°C')} wortwert />
        <V1Stat label="Plan Luftfeuchte" value={bandText(stand.feuchteMin, stand.feuchteMax, '%')} wortwert />
      </section>

      <V1Card>
        <div className="st-feldzeile">
          <span className="st-etikett">
            Die Etappen änderst du im Plan.
            <small>Zielwert je Etappe; für einen Wert pro Tag lege Etappen von einem Tag an.</small>
          </span>
          <V1LinkButton to="/plan?bereich=trocknung" variant="ghost">Plan ›</V1LinkButton>
        </div>
      </V1Card>

      <V1Section title="Betrieb">
        <V1Card>
          <div className="st-feldzeile">
            <span className="st-etikett">Betriebsart<small>Starte mit „Nur melden“.</small></span>
          </div>
          <V1Tabs
            items={[{ value: 'melden' as Betrieb, label: 'Nur melden' }, { value: 'regeln' as Betrieb, label: 'Regeln' }]}
            active={stand.betrieb}
            onChange={(b) => { if (b !== stand.betrieb && !arbeitet) { if (b === 'melden') void setzen('melden', folgt.entfeuchter, folgt.kuehlung); else anfrage('regeln', folgt.entfeuchter, folgt.kuehlung) } }}
            label="Betriebsart"
          />
        </V1Card>

        {stand.geraete.map((g) => (
          <GeraetKarte
            key={g.rolle}
            g={g}
            betrieb={stand.betrieb}
            gesperrt={arbeitet}
            onFolgt={(an) => {
              const neu = { ...folgt, [g.rolle]: an }
              if (regeln && an) anfrage('regeln', neu.entfeuchter, neu.kuehlung)
              else void setzen(stand.betrieb, neu.entfeuchter, neu.kuehlung)
            }}
          />
        ))}

        <div className="zj-aktionen">
          {!regeln && <V1Button variant="primary" disabled={arbeitet || (!folgt.entfeuchter && !folgt.kuehlung)} onClick={() => anfrage('regeln', folgt.entfeuchter, folgt.kuehlung)}>Auf „Regeln“ stellen…</V1Button>}
          {hatMerker && <V1Button variant="ghost" disabled={arbeitet} onClick={() => void zuruecksetzen()}>Alte Werte zurück</V1Button>}
        </div>
      </V1Section>

      <div className="st-geraete-zeile">
        <span>Ports <b>{zugeordnet} / 2</b> zugeordnet</span>
        <V1Button variant="ghost" onClick={() => setPortBlatt(true)}>Ports bearbeiten ›</V1Button>
      </div>

      <V1Sheet
        open={rueckfrage != null}
        onClose={() => setRueckfrage(null)}
        title="Auf „Regeln“ stellen?"
        footer={(
          <>
            <button type="button" className="ls-btn" onClick={() => setRueckfrage(null)}>Abbrechen</button>
            <button type="button" className="ls-btn is-primary" onClick={() => { const r = rueckfrage; setRueckfrage(null); if (r) void setzen(r.betrieb, r.entfeuchter, r.kuehlung) }}>Ja, Regeln einschalten</button>
          </>
        )}
      >
        <div className="zj-rueckfrage">
          <p>Das schreibt der Fork ab jetzt an den Controller:</p>
          {rueckfrage && wuerdeSchreiben(stand.geraete, { entfeuchter: rueckfrage.entfeuchter, kuehlung: rueckfrage.kuehlung }).map((w) => (
            <div key={w.rolle} className="zj-wert" data-audit={`rueckfrage-${w.rolle}`}>
              <span>{w.portName} · {w.feld}</span>
              <strong>{zahl(w.von)} → {zahl(w.nach)} {w.einheit}</strong>
            </div>
          ))}
          <p className="fe-hinweis">
            Nicht angefasst: alle Geräte ohne „Folgt dem Plan“ und der Modus der Ports. Der Fork merkt sich die alten Werte; „Alte Werte zurück“
            setzt sie wieder ein. {betriebText('regeln')} gilt, bis du auf „Nur melden“ zurückstellst.
          </p>
        </div>
      </V1Sheet>
      <PortBlatt
        key={portBlatt ? 'offen' : 'zu'}
        open={portBlatt}
        onClose={() => setPortBlatt(false)}
        tentId={zeltId}
        onGespeichert={() => { setPortBlatt(false); void laden().catch(() => undefined) }}
      />
    </V1Page>
  )
}

function GeraetKarte({ g, betrieb, gesperrt, onFolgt }: { g: SteuerungGeraet; betrieb: Betrieb; gesperrt: boolean; onFolgt: (an: boolean) => void }) {
  const r = ROLLE[g.rolle]
  const p = plakette(g, betrieb)
  const nichtZugeordnet = g.urteil === 'nichtZugeordnet'
  return (
    <V1Card>
      <div className="st-feldzeile" data-audit={`trocknung-${g.rolle}`}>
        <span className="st-etikett">
          {r.titel}
          <small>{g.portName ? `${g.portName}${g.modus ? ` · ${g.modus === 'Off' ? 'Modus Aus' : g.modus}` : ''}` : 'kein Port zugeordnet'}</small>
        </span>
        <span className={p.ton ? `zj-chip is-${p.ton}` : 'zj-chip'}>{p.text}</span>
      </div>
      {!nichtZugeordnet && (
        <>
          <V1Switch label="Folgt dem Plan" checked={g.folgt} onChange={gesperrt ? () => undefined : onFolgt} hint={geraetHinweis(g)} />
          <div className="st-feldzeile">
            <span className="st-etikett">{r.feld} am Gerät</span>
            <span className="st-nurlesen">{g.wertAmGeraet == null ? '–' : `${zahl(g.wertAmGeraet)} ${r.einheit}`}</span>
          </div>
          <div className="st-feldzeile">
            <span className="st-etikett">Plan (oben im Band)</span>
            <span className="st-nurlesen">{g.planWert == null ? '–' : `${zahl(g.planWert)} ${r.einheit}`}</span>
          </div>
          {g.zuletztGesetzt && (
            <div className="st-feldzeile">
              <span className="st-etikett">Zuletzt gesetzt<small>{g.vorher != null ? `vorher ${zahl(g.vorher)} ${r.einheit} (gemerkt)` : null}</small></span>
              <span className="st-nurlesen">{new Date(g.zuletztGesetzt).toLocaleString('de-DE', { dateStyle: 'short', timeStyle: 'short' })}</span>
            </div>
          )}
        </>
      )}
    </V1Card>
  )
}
