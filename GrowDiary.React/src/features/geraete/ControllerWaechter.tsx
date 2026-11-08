import { useEffect, useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import type { ReactNode } from 'react'
import { V1Alert, V1Button, V1Card, V1Field, V1Section, V1Skeleton, V1Switch } from '../../components/v1'
import { V1Select } from '../../components/V1Select'
import type { V1Option } from '../../components/V1Select'
import type { HomeAssistantEntity } from '../../types'
import type { ControllerStand, ControllerWaechterAntwort, ControllerWaechterEintrag } from '../steuerung/steuerung-typen'
import { entitaetsOptionen } from './entitaets-optionen'
import { vorbereiten } from './entitaets-passung'
import { haWert } from '../../utils'
import '../steuerung/steuerung.css'

const URL = '/api/steuerung/controller-waechter'

/** Ein neuer Controller mit den Voreinstellungen — dieselben Werte wie im Backend (ControllerWaechterEintrag). */
const NEU: ControllerWaechterEintrag = {
  id: '', name: '', statusEntity: null, feuchteEntity: null, steckdoseEntity: null, aktiv: true, modus: 'neustart',
  meldenNachMin: 10, neustartNachMin: 15, ausDauerSek: 15, maxNeustartsProTag: 2, pauseBisUtc: null,
}

const BEFUND: Record<ControllerStand['befund'], { text: string; ton: 'ok' | 'warn' | 'neutral' }> = {
  Gesund: { text: 'online', ton: 'ok' },
  Offline: { text: 'offline', ton: 'warn' },
  Eingefroren: { text: 'Feuchte eingefroren', ton: 'warn' },
  NichtPruefbar: { text: 'nicht prüfbar', ton: 'neutral' },
}

/** V1Field mit der Fehlerzeile der Rollen-Zeilen darunter (`st-fehler`). */
function Feld({ fehler, children, ...rest }: { label: string; hint?: string | null; wide?: boolean; fehler?: string; children: ReactNode }) {
  return (
    <V1Field {...rest}>
      {children}
      {fehler && <div className="st-fehler" role="alert">{fehler}</div>}
    </V1Field>
  )
}

function uhrzeit(utc: string): string {
  return new Date(utc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })
}

function dauer(seitUtc: string): string {
  const min = Math.max(0, Math.round((Date.now() - new Date(seitUtc).getTime()) / 60000))
  return min >= 60 ? `${Math.floor(min / 60)} Std. ${min % 60} Min.` : `${min} Min.`
}

/** Ein Wert mit Pfeil-Knöpfen, wie bei den anderen Steuerungen. */
function Stufe({ titel, klein, wert, einheit, min, max, schritt = 1, onChange }: {
  titel: string; klein: string; wert: number; einheit: string; min: number; max: number; schritt?: number
  onChange: (neu: number) => void
}) {
  return (
    <div className="st-feldzeile">
      <span className="st-etikett">{titel}<small>{klein}</small></span>
      <span className="st-eingaben">
        <V1Button variant="ghost" ariaLabel={`${titel} verringern`} disabled={wert <= min} onClick={() => onChange(Math.max(min, wert - schritt))}>−</V1Button>
        <span className="st-wert">{wert}{' '}{einheit}</span>
        <V1Button variant="ghost" ariaLabel={`${titel} erhöhen`} disabled={wert >= max} onClick={() => onChange(Math.min(max, wert + schritt))}>+</V1Button>
      </span>
    </div>
  )
}

/**
 * Geräte › Technik › Rollen › Controller · Wächter (08.11.2026).
 *
 * Der Wächter hängt am Controller, nicht an einer Steuerung: Er prüft Verbindung und Feuchtefühler, meldet einen
 * Ausfall und startet den Controller über seine Steckdose neu. Gespeichert wird je Controller mit einem Knopf, denn
 * Entitäten und Zahlen gehören zusammen — die Pause dagegen wirkt sofort.
 */
export function ControllerWaechter({ entities }: { entities: HomeAssistantEntity[] }) {
  const [stand, setStand] = useState<ControllerStand[] | null>(null)
  const [entwurf, setEntwurf] = useState<ControllerWaechterEintrag[] | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [feldFehler, setFeldFehler] = useState<Record<string, string>>({})
  const [hinweis, setHinweis] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)

  useEffect(() => {
    const abbruch = new AbortController()
    const laden = async () => {
      try {
        const geladen = await apiFetch<ControllerWaechterAntwort>(URL, { signal: abbruch.signal })
        if (!abbruch.signal.aborted) { setStand(geladen.controller); setFehler(null) }
      } catch (caught) {
        if (!abbruch.signal.aborted) setFehler(formatApiError(caught, 'Die Controller konnten nicht geladen werden.'))
      }
    }
    void laden()
    const takt = window.setInterval(() => { void laden() }, 30000)
    return () => { abbruch.abort(); window.clearInterval(takt) }
  }, [])

  const vorbereitet = useMemo(() => entities.map(vorbereiten), [entities])
  const optionen = useMemo(() => ({
    status: entitaetsOptionen(vorbereitet, { domains: ['binary_sensor'], label: 'Verbindung Status online' }),
    feuchte: entitaetsOptionen(vorbereitet, { domains: ['sensor'], einheit: '%', label: 'Luftfeuchtigkeit' }),
    dose: entitaetsOptionen(vorbereitet, { domains: ['switch'], label: 'Steckdose Strom' }),
  }), [vorbereitet])

  if (!stand) return fehler ? <V1Alert tone="critical" message={fehler} /> : <V1Skeleton label="Controller werden geladen" />

  const liste = entwurf ?? stand.map((s) => s.controller)
  const geaendert = entwurf !== null
  const standVon = (id: string) => stand.find((s) => s.controller.id === id)

  const aendern = (index: number, teil: Partial<ControllerWaechterEintrag>) => {
    setHinweis(null)
    setEntwurf(liste.map((c, i) => (i === index ? { ...c, ...teil } : c)))
  }

  const speichern = async () => {
    setArbeitet(true)
    setHinweis(null)
    try {
      const antwort = await apiFetch<ControllerWaechterAntwort>(URL, { method: 'PUT', body: JSON.stringify({ controller: liste }) })
      setStand(antwort.controller)
      setEntwurf(null)
      setFeldFehler({})
      setFehler(null)
      setHinweis('Gespeichert.')
    } catch (caught) {
      const felder = (caught as { payload?: { fieldErrors?: Record<string, string[]> } }).payload?.fieldErrors
      if (felder) {
        setFeldFehler(Object.fromEntries(Object.entries(felder).map(([feld, texte]) => [feld, texte.join(' ')])))
        setFehler('Bitte die markierten Zeilen prüfen.')
      } else {
        setFehler(formatApiError(caught, 'Das Speichern hat nicht geklappt.'))
      }
    } finally {
      setArbeitet(false)
    }
  }

  const pausieren = async (id: string, stunden: number) => {
    setArbeitet(true)
    try {
      const antwort = await apiFetch<ControllerWaechterAntwort>(`/api/steuerung/controller-waechter/${encodeURIComponent(id)}/pause`, { method: 'POST', body: JSON.stringify({ stunden }) })
      setStand(antwort.controller)
      setFehler(null)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Die Pause konnte nicht gesetzt werden.'))
    } finally {
      setArbeitet(false)
    }
  }

  const wert = (id: string | null): string | undefined => {
    const e = id ? entities.find((x) => x.entityId === id) : undefined
    return e ? (haWert(e.state, e.unitOfMeasurement) ?? undefined) : undefined
  }
  const waehlbar = (liste: V1Option[], aktuell: string | null): V1Option[] => [
    { wert: '', text: '— keins —', hinweis: 'Diese Rolle bleibt leer.', betont: true },
    ...(aktuell && !liste.some((o) => o.wert === aktuell) ? [{ wert: aktuell, text: aktuell, hinweis: 'Home Assistant kennt diese Entität gerade nicht.' }] : []),
    ...liste,
  ]

  return (
    <div className="st-rollen">
      <p className="st-hinweis">
        Der Wächter gehört zum Controller, nicht zu einer Steuerung: Fällt er aus, melden Trocknung, Licht und alles andere am Controller nichts mehr.
      </p>
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {hinweis && <V1Alert tone="ok" message={hinweis} />}

      {liste.length === 0 && (
        <V1Card>
          <p className="st-hinweis">Noch kein Controller eingetragen. Lege einen an und wähle seine Verbindung (zum Beispiel „… Status“ der AC-Infinity-Integration) und seinen Feuchtefühler.</p>
        </V1Card>
      )}

      {liste.map((c, i) => {
        const s = standVon(c.id)
        const f = (name: string) => feldFehler[`controller[${i}].${name}`]
        const befund = s ? BEFUND[s.befund] : null
        const neustart = c.modus === 'neustart'
        const dose = c.steckdoseEntity !== null
        return (
          <div key={c.id || `neu-${i}`}>
            {s && s.befund !== 'Gesund' && s.befund !== 'NichtPruefbar' && (
              <V1Alert
                tone="warn"
                message={`${c.name || 'Der Controller'} ist ${s.befund === 'Offline' ? 'offline' : 'verbunden, liefert aber keine neue Feuchte'}${s.seitUtc ? ` — seit ${uhrzeit(s.seitUtc)} Uhr (${dauer(s.seitUtc)})` : ''}.`}
              />
            )}

            <V1Section title={c.name || 'Neuer Controller'} action={befund ? <span className={`zj-chip is-${befund.ton === 'ok' ? 'ok' : befund.ton === 'warn' ? 'warn' : 'grau'}`}>{befund.text}</span> : undefined}>
              <Feld label="Name" wide fehler={f('Name')}>
                <input value={c.name} onChange={(e) => aendern(i, { name: e.target.value })} placeholder="Trockenzelt-Controller" />
              </Feld>

              <Feld label="Verbindung" hint="Meldet, ob der Controller mit seiner Cloud verbunden ist. Das Hauptsignal des Wächters." wide fehler={f('StatusEntity')}>
                <div className="st-geraet">
                  <V1Select label="" ariaLabel="Verbindung des Controllers" titel="Verbindung des Controllers" wert={c.statusEntity ?? ''} platzhalter="— keins —"
                    onWahl={(w) => aendern(i, { statusEntity: w || null })} optionen={waehlbar(optionen.status.optionen, c.statusEntity)} hinweisOben={optionen.status.leerHinweis ?? undefined} />
                  <span className={s?.statusZustand ? 'st-geraet-wert is-ok' : 'st-geraet-wert is-faint'}>{s?.statusZustand ? (s.statusZustand === 'on' ? 'online' : s.statusZustand === 'off' ? 'offline' : s.statusZustand) : 'nicht zugeordnet'}</span>
                </div>
              </Feld>

              <Feld label="Feuchtefühler (%)" hint="Zweite Sicherung: Bleibt der Wert 15 Minuten gleich, gilt er als eingefroren." wide fehler={f('FeuchteEntity')}>
                <div className="st-geraet">
                  <V1Select label="" ariaLabel="Feuchtefühler des Controllers" titel="Feuchtefühler des Controllers" wert={c.feuchteEntity ?? ''} platzhalter="— keins —"
                    onWahl={(w) => aendern(i, { feuchteEntity: w || null })} optionen={waehlbar(optionen.feuchte.optionen, c.feuchteEntity)} hinweisOben={optionen.feuchte.leerHinweis ?? undefined} />
                  <span className={c.feuchteEntity ? 'st-geraet-wert is-ok' : 'st-geraet-wert is-faint'}>{c.feuchteEntity ? (wert(c.feuchteEntity) ?? 'unbekannt') : 'nicht zugeordnet'}</span>
                </div>
              </Feld>

              <Feld
                label="Steckdose des Controllers"
                hint={dose
                  ? 'Nur sie wird zum Neustart aus- und wieder eingeschaltet.'
                  : 'Noch keine schaltbare Steckdose gewählt. Sobald du eine einbindest (zum Beispiel eine Shelly), wählst du sie hier — bis dahin meldet der Wächter nur.'}
                wide fehler={f('SteckdoseEntity')}
              >
                <div className="st-geraet">
                  <V1Select label="" ariaLabel="Steckdose des Controllers" titel="Steckdose des Controllers" wert={c.steckdoseEntity ?? ''} platzhalter="— keine —"
                    onWahl={(w) => aendern(i, { steckdoseEntity: w || null })} optionen={waehlbar(optionen.dose.optionen, c.steckdoseEntity)} hinweisOben={optionen.dose.leerHinweis ?? undefined} />
                  <span className={dose ? 'st-geraet-wert is-ok' : 'st-geraet-wert is-faint'}>{dose ? (s?.steckdoseZustand === 'on' ? 'an' : s?.steckdoseZustand === 'off' ? 'aus' : (s?.steckdoseZustand ?? 'unbekannt')) : 'nicht zugeordnet'}</span>
                </div>
              </Feld>
            </V1Section>

            <V1Section title="Wächter">
              <V1Card>
                <V1Switch label="Wächter" checked={c.aktiv} onChange={(aktiv) => aendern(i, { aktiv })} />

                <div className="st-feldzeile is-gestapelt">
                  <span className="st-etikett">
                    Wenn der Controller ausfällt
                    <small>{dose ? 'Der Neustart unterbricht alles, was am Controller hängt, für etwa 15 Sekunden.' : 'Voreingestellt ist Melden + neu starten. Ohne schaltbare Steckdose meldet der Wächter nur.'}</small>
                  </span>
                  <span className="st-eingaben">
                    <V1Button variant={!neustart ? 'primary' : 'ghost'} onClick={() => aendern(i, { modus: 'melden' })}>Nur melden</V1Button>
                    <V1Button variant={neustart ? 'primary' : 'ghost'} onClick={() => aendern(i, { modus: 'neustart' })}>Melden + neu starten</V1Button>
                  </span>
                </div>
                {f('Modus') && <div className="st-fehler" role="alert">{f('Modus')}</div>}

                <Stufe titel="Melden nach" klein="So lange muss der Ausfall dauern, bevor der Push kommt. Kurze Aussetzer bleiben still." wert={c.meldenNachMin} einheit="Min." min={1} max={120} onChange={(v) => aendern(i, { meldenNachMin: v })} />
                {neustart && (
                  <>
                    <Stufe titel="Neustart nach" klein="So lange muss der Controller offline sein, bevor die Steckdose aus- und wieder eingeschaltet wird." wert={c.neustartNachMin} einheit="Min." min={1} max={240} onChange={(v) => aendern(i, { neustartNachMin: v })} />
                    {f('NeustartNachMin') && <div className="st-fehler" role="alert">{f('NeustartNachMin')}</div>}
                    <Stufe titel="Aus-Dauer" klein="So lange bleibt die Steckdose aus." wert={c.ausDauerSek} einheit="Sek." min={5} max={120} schritt={5} onChange={(v) => aendern(i, { ausDauerSek: v })} />
                    <Stufe titel="Höchstens pro Tag" klein="Danach meldet der Wächter nur noch. So gibt es keine Dauerschleife." wert={c.maxNeustartsProTag} einheit="×" min={1} max={6} onChange={(v) => aendern(i, { maxNeustartsProTag: v })} />
                  </>
                )}

                <p className="zj-erklaerung">
                  <b>{dose && neustart ? 'So läuft es ab:' : 'Heute:'}</b>{' '}
                  {dose && neustart
                    ? `Nach ${c.meldenNachMin} Min. Ausfall kommt der Push, nach ${c.neustartNachMin} Min. wird die Steckdose ${c.ausDauerSek} Sek. ausgeschaltet und wieder eingeschaltet. Ist der Controller 5 Min. danach noch offline, gibt es einen Push und eine Stunde Ruhe; höchstens ${c.maxNeustartsProTag}× pro Tag.`
                    : neustart
                      ? 'Keine schaltbare Steckdose gewählt — der Wächter meldet nur. Der Neustart läuft erst, wenn du oben eine Steckdose wählst.'
                      : `Der Wächter meldet nach ${c.meldenNachMin} Min. Ausfall und ändert nichts.`}
                </p>

                {s && c.id && (
                  <div className="st-feldzeile is-gestapelt">
                    <span className="st-etikett">
                      Pause
                      <small>
                        Etwa wenn du den Controller selbst abklemmst. Wirkt sofort.
                        {s.pausiert && c.pauseBisUtc ? ` Pausiert bis ${uhrzeit(c.pauseBisUtc)} Uhr (${new Date(c.pauseBisUtc).toLocaleDateString('de-DE')}).` : ''}
                      </small>
                    </span>
                    <span className="st-eingaben">
                      <V1Button variant="ghost" disabled={arbeitet} onClick={() => void pausieren(c.id, 1)}>1 h</V1Button>
                      <V1Button variant="ghost" disabled={arbeitet} onClick={() => void pausieren(c.id, 72)}>3 Tage</V1Button>
                      {s.pausiert && <V1Button variant="primary" disabled={arbeitet} onClick={() => void pausieren(c.id, 0)}>Pause beenden</V1Button>}
                    </span>
                  </div>
                )}

                {s && (
                  <p className="st-hinweis">
                    {s.geprueftUtc ? `Letzte Prüfung ${uhrzeit(s.geprueftUtc)} Uhr.` : 'Noch nicht geprüft.'} Neustarts in den letzten 24 Stunden: {s.neustartsHeute}.
                    {s.letzteMeldung ? ` Letzte Meldung: ${s.letzteMeldung}` : ''}
                  </p>
                )}
              </V1Card>
            </V1Section>

            <div className="st-knopfleiste">
              <V1Button onClick={() => setEntwurf(liste.filter((_, k) => k !== i))}>Controller entfernen</V1Button>
            </div>
          </div>
        )
      })}

      <div className="st-knopfleiste">
        <V1Button onClick={() => { setHinweis(null); setEntwurf([...liste, { ...NEU }]) }}>Controller hinzufügen</V1Button>
        <V1Button variant="primary" disabled={arbeitet || !geaendert} onClick={() => void speichern()}>
          {arbeitet ? 'Speichert…' : 'Speichern'}
        </V1Button>
      </div>
    </div>
  )
}
