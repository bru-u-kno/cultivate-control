import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'

import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Empty, V1Skeleton } from '../../components/v1'
import { V1Select } from '../../components/V1Select'
import type { V1Option } from '../../components/V1Select'
import type { HomeAssistantEntity, SettingsOverviewDto, TentDto } from '../../types'
import { haWert } from '../../utils'
import { maschinenZahl } from '../../zahlenfeld'
import type { RaumStand } from '../steuerung/steuerung-typen'
import { vorbereiten } from './entitaets-passung'
import { CameraPreview } from '../home-assistant/CameraPreview'
import { lesenMappingZiel, TROCKNUNG_METRIKEN } from '../home-assistant/mapping-link'
import { definitions, entityOptionLabel, groups, messgroessenOptionen, RAUMWERTE_METRIKEN, vpdKpa } from '../home-assistant/messgroessen'
import type { EntityDefinition } from '../home-assistant/messgroessen'
import { zeltOhneSensoren } from '../home-assistant/tent-request'
import '../home-assistant/home-assistant.css'

/**
 * cc.285: Die EINE Stelle, an der Messgrößen Entitäten zugeordnet werden (vorher: Seite „Home Assistant“ UND der Reiter
 * „Messgrößen“ — beide schrieben dasselbe).
 *
 * <b>Aufbau.</b> Erst die Gruppe (Zelt, RDWC/DWC, Technik, Kameras), dann je Messgröße eine Überschrift mit einer Zeile je Zelt.
 * Das Trockenzelt führt nur Temperatur, Feuchte und VPD und erscheint nur dort.
 *
 * <b>Speichern.</b> Jede Zeile speichert für sich (<c>PUT /api/settings/tents/{id}/sensors/{messgroesse}</c>). Die alte
 * Seite hielt einen Entwurf aller Zeilen und ersetzte beim Speichern ALLE Sensorzeilen — ein Entwurf, der vor einer anderen
 * Änderung geladen wurde, löschte sie wieder (Zelt 2, 31.10.2026). Eine Zeile kann das nicht: sie kennt nur sich selbst.
 *
 * <b>VPD</b> ist optional: leer heißt „der Fork rechnet ihn aus Temperatur und Feuchte“, die Zeile zeigt dann den Wert.
 */
type Gruppe = 'tent' | 'reservoir' | 'hardware' | 'kameras'
const KAMERAS = 'kameras'

export function ZuordnungReiter({ entities }: { entities: HomeAssistantEntity[] }) {
  const [params] = useSearchParams()
  const [ziel] = useState(() => lesenMappingZiel(params))
  const zielErledigt = useRef(false)
  const [tents, setTents] = useState<TentDto[]>([])
  const [raum, setRaum] = useState<RaumStand | null>(null)
  const [kameras, setKameras] = useState<Record<number, string[]>>({})
  const [gruppe, setGruppe] = useState<Gruppe>(ziel.definition?.group ?? 'tent')
  const [laedt, setLaedt] = useState(true)
  const [speichert, setSpeichert] = useState<string | null>(null)
  const [vorschau, setVorschau] = useState<Record<number, number>>({})
  const [hinweis, setHinweis] = useState<string | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)

  useEffect(() => {
    const abbruch = new AbortController()
    void (async () => {
      try {
        const uebersicht = await apiFetch<SettingsOverviewDto>('/api/settings', { signal: abbruch.signal })
        if (abbruch.signal.aborted) return
        const sortiert = [...uebersicht.tents].sort((a, b) => punkte(b) - punkte(a) || a.displayOrder - b.displayOrder || a.name.localeCompare(b.name))
        setTents(sortiert)
        setKameras(Object.fromEntries(sortiert.map((zelt) => [zelt.id, kameraListe(zelt)])))
        const raumStand = await apiFetch<RaumStand>('/api/steuerung/raum', { signal: abbruch.signal }).catch(() => null)
        if (!abbruch.signal.aborted) setRaum(raumStand)
      } catch (caught) {
        if (!abbruch.signal.aborted) setFehler(formatApiError(caught, 'Die Zuordnung konnte nicht geladen werden.'))
      } finally {
        if (!abbruch.signal.aborted) setLaedt(false)
      }
    })()
    return () => abbruch.abort()
  }, [])

  // Sprung aus „Mappen“: zur Zeile scrollen und sie kurz hervorheben (einmal, nach dem Laden). Kein setState im Effekt.
  useEffect(() => {
    if (laedt || zielErledigt.current || !ziel.definition) return
    zielErledigt.current = true
    const metrik = ziel.definition.metricType
    const zeile = document.querySelector(`.ha-zuordnung [data-metric="${metrik}"][data-tent="${ziel.tentId}"]`)
      ?? document.querySelector(`.ha-zuordnung [data-metric="${metrik}"]`)
    if (!zeile) return
    zeile.scrollIntoView({ block: 'center' })
    zeile.classList.add('ha-treffer')
    window.setTimeout(() => zeile.classList.remove('ha-treffer'), 4000)
  }, [laedt, ziel])

  const vorbereitet = useMemo(() => entities.map(vorbereiten), [entities])
  const kameraEntitaeten = useMemo(() => {
    const kameraListe = entities.filter((entity) => entity.domain === 'camera' || entity.domain === 'image')
    return kameraListe.length > 0 ? kameraListe : entities
  }, [entities])

  const entitaet = (id: string) => entities.find((eintrag) => eintrag.entityId === id.trim())
  const livewert = (id: string) => {
    const treffer = id.trim() ? entitaet(id) : undefined
    return treffer ? haWert(treffer.state, treffer.unitOfMeasurement) : null
  }
  const zugeordnet = (zelt: TentDto, definition: EntityDefinition) => zelt.sensors.find((sensor) => sensor.metricType === definition.metricType)?.haEntityId ?? ''

  /** VPD aus den Entitäten für Temperatur und Feuchte des Zelts — so rechnet auch der Fork, wenn nichts zugeordnet ist. */
  function gerechneterVpd(zelt: TentDto): string | null {
    const temp = maschinenZahl(entitaet(zugeordnet(zelt, definitionVon('AirTemperature')))?.state)
    const feuchte = maschinenZahl(entitaet(zugeordnet(zelt, definitionVon('Humidity')))?.state)
    if (temp == null || feuchte == null) return null
    const wert = vpdKpa(temp, feuchte)
    return wert == null ? null : `≈ ${haWert(String(wert), 'kPa')}`
  }

  async function setzen(zelt: TentDto, definition: EntityDefinition, entityId: string) {
    setSpeichert(`${zelt.id}-${definition.metricType}`)
    setFehler(null)
    setHinweis(null)
    try {
      const gespeichert = await apiFetch<TentDto>(`/api/settings/tents/${zelt.id}/sensors/${definition.metricType}`, { method: 'PUT', body: JSON.stringify({ haEntityId: entityId.trim() }) })
      setTents((aktuell) => aktuell.map((eintrag) => (eintrag.id === gespeichert.id ? gespeichert : eintrag)))
      setHinweis(`${definition.label} (${zelt.name}) gespeichert.`)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Die Zuordnung konnte nicht gespeichert werden.'))
    } finally {
      setSpeichert(null)
    }
  }

  async function kamerasSpeichern(zelt: TentDto) {
    setSpeichert(`${zelt.id}-${KAMERAS}`)
    setFehler(null)
    setHinweis(null)
    try {
      const gespeichert = await apiFetch<TentDto>(`/api/settings/tents/${zelt.id}`, { method: 'PUT', body: JSON.stringify(zeltOhneSensoren(zelt, kameras[zelt.id] ?? [])) })
      setTents((aktuell) => aktuell.map((eintrag) => (eintrag.id === gespeichert.id ? gespeichert : eintrag)))
      setKameras((aktuell) => ({ ...aktuell, [gespeichert.id]: kameraListe(gespeichert) }))
      setHinweis(`Kameras (${zelt.name}) gespeichert.`)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Die Kameras konnten nicht gespeichert werden.'))
    } finally {
      setSpeichert(null)
    }
  }

  const aendereKameras = (zelt: TentDto, aendern: (liste: string[]) => string[]) =>
    setKameras((aktuell) => ({ ...aktuell, [zelt.id]: aendern(aktuell[zelt.id] ?? []) }))

  if (laedt) return <V1Skeleton rows={4} label="Zuordnung wird geladen" />
  if (tents.length === 0) return <V1Empty title="Kein Zelt angelegt" action={<Link to="/zelte" className="ls-btn is-primary">Zelt anlegen</Link>} />

  const sichtbar = (zelt: TentDto, definition: EntityDefinition) => zelt.tentType !== 'Drying' || TROCKNUNG_METRIKEN.includes(definition.metricType)
  const raumWerte = (zelt: TentDto) => (zelt.raumId && raum?.aussenWerte && raum.fuehler ? raum : null)
  const gruppenChips = [...groups, { key: KAMERAS as Gruppe, label: 'Kameras' }]

  return (
    <section className="ls-panel ha-zuordnung" data-audit="ha-mapping-panel">
      <div className="ls-panel-head">
        <span className="ls-label">Zuordnung</span>
        <span className="ls-panel-meta">Messgröße → HA-Entität, je Zelt · speichert sofort</span>
      </div>

      {fehler && <V1Alert message={fehler} tone="warn" />}
      {hinweis && <V1Alert message={hinweis} tone="ok" />}

      <div className="ha-tent-row co-chips" role="tablist" aria-label="Messgruppe">
        {gruppenChips.map((eintrag) => (
          <button key={eintrag.key} type="button" role="tab" aria-selected={eintrag.key === gruppe} className={eintrag.key === gruppe ? 'co-chip active' : 'co-chip'} onClick={() => setGruppe(eintrag.key as Gruppe)}>
            {eintrag.label}
          </button>
        ))}
      </div>

      {gruppe !== KAMERAS && definitions.filter((definition) => definition.group === gruppe).map((definition) => {
        const zelte = tents.filter((zelt) => sichtbar(zelt, definition))
        if (zelte.length === 0) return null
        const auswahl = messgroessenOptionen(vorbereitet, definition)
        return (
          <div key={definition.metricType} data-audit="ha-messgroesse">
            <div className="ha-messgroesse">
              <span className="ls-label">{definition.label}</span>
              {definition.unit && <small>{definition.unit}</small>}
            </div>
            {definition.hinweis && <p className="ha-metric-hinweis">{definition.hinweis}</p>}
            {zelte.map((zelt) => {
              const vomRaum = RAUMWERTE_METRIKEN.includes(definition.metricType) ? raumWerte(zelt) : null
              if (vomRaum?.fuehler) return <RaumZeile key={zelt.id} zelt={zelt} definition={definition} raum={vomRaum} entitaet={entitaet} />
              const id = zugeordnet(zelt, definition)
              const live = livewert(id)
              const gerechnet = definition.metricType === 'Vpd' && id.trim() === '' ? gerechneterVpd(zelt) : null
              return (
                <div key={zelt.id} className="co-row" data-audit="ha-entity-row" data-metric={definition.metricType} data-tent={zelt.id}>
                  <span className="ha-metric">{zelt.name}</span>
                  <V1Select
                    label={zelt.name}
                    ariaLabel={`${definition.label} · ${zelt.name}`}
                    titel={`${definition.label} · ${zelt.name}`}
                    unterzeile={definition.placeholder}
                    wert={id}
                    platzhalter={definition.placeholder}
                    disabled={speichert === `${zelt.id}-${definition.metricType}`}
                    onWahl={(wert) => void setzen(zelt, definition, wert)}
                    optionen={[{ wert: '', text: '— nicht zugeordnet —', betont: true }, ...auswahl.optionen]}
                    hinweisOben={auswahl.leerHinweis}
                  />
                  {live != null
                    ? <span className="co-row-value is-good">{live}</span>
                    : gerechnet != null
                      ? <span className="co-row-value">{gerechnet}</span>
                      : id.trim() === ''
                        ? <span className="co-row-value is-faint">nicht gemappt</span>
                        : <span className="co-row-value">gemappt</span>}
                </div>
              )
            })}
          </div>
        )
      })}

      {gruppe === KAMERAS && tents.map((zelt) => {
        const liste = kameras[zelt.id] ?? []
        const gespeichert = (zelt.cameras ?? []).filter((kamera) => kamera.trim())
        const bilder = vorschau[zelt.id] ? (gespeichert.length > 0 ? gespeichert : liste.map((kamera) => kamera.trim()).filter(Boolean)) : []
        return (
          <div key={zelt.id} data-audit="ha-kameras-zelt">
            <div className="ha-messgroesse"><span className="ls-label">{zelt.name}</span></div>
            {liste.map((kamera, index) => (
              <div key={index} className="co-row" data-audit="ha-camera-field-action">
                <span className="ha-metric">Kamera {index + 1}</span>
                <V1Select
                  label={`Kamera ${index + 1}`}
                  titel={`Kamera ${index + 1} · ${zelt.name}`}
                  wert={kamera}
                  platzhalter="camera.hauptzelt"
                  onWahl={(wert) => aendereKameras(zelt, (alt) => alt.map((eintrag, i) => (i === index ? wert : eintrag)))}
                  optionen={kameraEntitaeten.map((entity): V1Option => ({ wert: entity.entityId, text: entity.friendlyName ?? entity.entityId, hinweis: entityOptionLabel(entity) }))}
                />
                <button type="button" className="ls-btn is-small" onClick={() => aendereKameras(zelt, (alt) => alt.filter((_, i) => i !== index))}>Entfernen</button>
              </div>
            ))}
            <div className="co-row">
              <div className="co-actions">
                <button type="button" className="ls-btn is-small" onClick={() => aendereKameras(zelt, (alt) => [...alt, ''])}>+ Kamera</button>
                <button type="button" className="ls-btn is-small is-primary" disabled={speichert === `${zelt.id}-${KAMERAS}`} onClick={() => void kamerasSpeichern(zelt)}>
                  {speichert === `${zelt.id}-${KAMERAS}` ? 'Speichert…' : 'Kameras speichern'}
                </button>
                <button type="button" className="ls-btn is-small" onClick={() => setVorschau((aktuell) => ({ ...aktuell, [zelt.id]: Date.now() }))}>Snapshot-Test</button>
              </div>
            </div>
            {vorschau[zelt.id] != null && (bilder.length === 0
              ? <div className="ls-panel-body"><p>Trag zuerst mindestens eine Kamera ein.</p></div>
              : <div className="ha-previews">{bilder.map((kamera, index) => <CameraPreview key={`${kamera}-${vorschau[zelt.id]}`} tentId={zelt.id} entity={kamera} index={index} nonce={vorschau[zelt.id]} />)}</div>)}
          </div>
        )
      })}
    </section>
  )
}

/** Die Außenwerte kommen vom Raum (cc.263): die Zeile zeigt den Raumwert und führt zu Geräte › Räume. */
function RaumZeile({ zelt, definition, raum, entitaet }: { zelt: TentDto; definition: EntityDefinition; raum: RaumStand; entitaet: (id: string) => HomeAssistantEntity | undefined }) {
  const temp = entitaet(raum.fuehler?.tempEntityId ?? '')
  const feuchte = entitaet(raum.fuehler?.rhEntityId ?? '')
  let wert: string | null = null
  if (definition.metricType === 'OutsideTemperature' && temp) wert = haWert(temp.state, temp.unitOfMeasurement)
  else if (definition.metricType === 'OutsideHumidity' && feuchte) wert = haWert(feuchte.state, feuchte.unitOfMeasurement)
  else if (definition.metricType === 'OutsideVpd') {
    const t = maschinenZahl(temp?.state)
    const f = maschinenZahl(feuchte?.state)
    const vpd = t != null && f != null ? vpdKpa(t, f) : null
    wert = vpd != null ? haWert(String(vpd), 'kPa') : null
  }
  return (
    <div className="co-row" data-audit="ha-raumwerte-row" data-metric={definition.metricType} data-tent={zelt.id}>
      <span className="ha-metric">{zelt.name}</span>
      <div className="v1-field v1-select">
        <span>Raumwert</span>
        <button type="button" className="v1-select-feld" disabled aria-label={`${definition.label} kommt vom Raum ${raum.name}`}>
          <span>Vom Raum „{raum.name}“ · <Link to="/geraete?reiter=raeume">Geräte › Räume</Link></span>
        </button>
      </div>
      {wert != null ? <span className="co-row-value is-good">{wert}</span> : <span className="co-row-value is-faint">kein Wert</span>}
    </div>
  )
}

function definitionVon(metrik: EntityDefinition['metricType']): EntityDefinition {
  return definitions.find((definition) => definition.metricType === metrik) as EntityDefinition
}

function punkte(zelt: TentDto) {
  return (zelt.activeGrowCount > 0 ? 100 : 0) + (zelt.tentType === 'Production' ? 10 : 0)
}

function kameraListe(zelt: TentDto): string[] {
  return zelt.cameras && zelt.cameras.length > 0 ? [...zelt.cameras] : (zelt.cameraEntityId ? [zelt.cameraEntityId] : [])
}
