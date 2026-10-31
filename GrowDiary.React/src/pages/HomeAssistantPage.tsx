import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, Navigate, useSearchParams } from 'react-router-dom'
import { apiFetch, formatApiError } from '../api'
import type { HomeAssistantEntity, HomeAssistantSettingsDto, SettingsOverviewDto } from '../types'
import { V1Alert, V1Button, V1Field, V1Page, V1Skeleton, V1Switch } from '../components/v1'
import { toNullableString } from '../components/v1-utils'
import { lesenMappingZiel, mappingLink } from '../features/home-assistant/mapping-link'
import '../features/home-assistant/home-assistant.css'

/**
 * Home Assistant: die Verbindung (Adresse, Zugangsschlüssel, Schalter) und der Stand der Anbindung.
 *
 * cc.285: Die Zuordnung der Messgrößen und Kameras steht nicht mehr hier, sondern an der einen Stelle
 * „Geräte & Entitäten › Zuordnung“ (`features/geraete/ZuordnungReiter.tsx`). Alte Adressen mit `?zelt=` oder
 * `?messwert=` führen dorthin weiter.
 */
function HomeAssistantPage() {
  const [searchParams] = useSearchParams()
  const [ha, setHa] = useState<HomeAssistantSettingsDto>({ baseUrl: '', accessToken: '', enabled: false })
  const [entitaeten, setEntitaeten] = useState<number | null>(null)
  const [geladenUm, setGeladenUm] = useState<Date | null>(null)
  const [showToken, setShowToken] = useState(false)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      try {
        const overview = await apiFetch<SettingsOverviewDto>('/api/settings', { signal: controller.signal })
        if (controller.signal.aborted) return
        setHa(overview.homeAssistant)
        // Best-effort: ohne erreichbares Home Assistant bleibt die Liste leer.
        const liste = await apiFetch<HomeAssistantEntity[]>('/api/home-assistant/entities', { signal: controller.signal }).catch(() => [])
        if (!controller.signal.aborted) {
          setEntitaeten(liste.length)
          if (liste.length > 0) setGeladenUm(new Date())
        }
      } catch (caught) {
        if (!controller.signal.aborted) setError(formatApiError(caught, 'Home Assistant konnte nicht geladen werden.'))
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    }
    void load()
    return () => controller.abort()
  }, [])

  // Alte Direktlinks („Mappen“ vor cc.285) führen auf die Zuordnung weiter.
  const altesZiel = lesenMappingZiel(searchParams)
  if (searchParams.has('zelt') || searchParams.has('messwert')) {
    return <Navigate to={mappingLink(altesZiel.tentId, altesZiel.definition?.metricType)} replace />
  }

  const connected = ha.isManagedByAddon || ha.enabled

  async function saveConnection(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setMessage(null)
    try {
      const saved = await apiFetch<HomeAssistantSettingsDto>('/api/settings/home-assistant', { method: 'PUT', body: JSON.stringify({ baseUrl: toNullableString(ha.baseUrl), accessToken: toNullableString(ha.accessToken), enabled: ha.enabled }) })
      setHa(saved)
      setMessage('Home-Assistant-Verbindung gespeichert.')
    } catch (caught) {
      setError(formatApiError(caught, 'Home Assistant konnte nicht gespeichert werden.'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <V1Page
      eyebrow="Einrichtung / Home Assistant"
      title="Home Assistant"
      subtitle="Hier steht die Verbindung zu Home Assistant. Welche Entität welchen Messwert liefert, legst du unter Geräte & Entitäten › Zuordnung fest."
    >
      {error && <V1Alert message={error} tone="warn" />}
      {message && <V1Alert message={message} tone="ok" />}

      {loading ? <V1Skeleton rows={4} label="Lade Home Assistant" /> : (
        <>
          <div className="co-strip" data-audit="ha-status-strip">
            <div className="co-cell">
              <div className="co-cell-label">Verbindung</div>
              <div className={`co-cell-value is-md${connected && (entitaeten ?? 0) > 0 ? ' is-good' : connected ? ' is-warn' : ''}`}>
                {ha.isManagedByAddon ? 'Add-on · verbunden' : connected ? ((entitaeten ?? 0) > 0 ? 'verbunden' : 'keine Antwort') : 'inaktiv'}
              </div>
            </div>
            <div className="co-cell">
              <div className="co-cell-label">Entitäten geladen</div>
              <div className="co-cell-value is-md">{geladenUm ? `${entitaeten} · ${new Intl.DateTimeFormat('de-DE', { hour: '2-digit', minute: '2-digit' }).format(geladenUm)}` : 'keine'}</div>
            </div>
          </div>

          {!ha.isManagedByAddon && (
            <section className="ls-panel">
              <div className="ls-panel-head"><span className="ls-label">Verbindung</span></div>
              <form className="ha-connect" data-audit="ha-connection-layout" onSubmit={(event) => void saveConnection(event)}>
                <V1Field label="Home Assistant URL" hint="Beispiel: http://homeassistant.local:8123">
                  <input value={ha.baseUrl ?? ''} onChange={(event) => setHa((current) => ({ ...current, baseUrl: event.target.value }))} placeholder="http://homeassistant.local:8123" />
                </V1Field>
                <V1Field label="Long-Lived Access Token">
                  <div className="v1-inline-input">
                    <input type={showToken ? 'text' : 'password'} value={ha.accessToken ?? ''} onChange={(event) => setHa((current) => ({ ...current, accessToken: event.target.value }))} autoComplete="off" />
                    <V1Button onClick={() => setShowToken((current) => !current)}>{showToken ? 'Verbergen' : 'Anzeigen'}</V1Button>
                  </div>
                </V1Field>
                <div className="co-actions" data-audit="ha-connection-actions">
                  <V1Switch label="Home Assistant aktiv" checked={ha.enabled} onChange={(checked) => setHa((current) => ({ ...current, enabled: checked }))} />
                  <V1Button type="submit" variant="primary" disabled={saving}>{saving ? 'Speichert…' : 'Verbindung speichern'}</V1Button>
                </div>
              </form>
            </section>
          )}

          <section className="ls-panel">
            <div className="ls-panel-head"><span className="ls-label">Zuordnung</span></div>
            <p className="ha-pair-text" style={{ margin: '0 0 10px' }}>
              Messgrößen und Kameras je Zelt zuordnen: unter Geräte &amp; Entitäten.
            </p>
            <Link to={mappingLink()} className="ls-btn is-small is-primary">Geräte › Zuordnung</Link>
          </section>

          {/* Der QR-Code wohnt unter „Handy › App einrichten“; eine zweite Fassung zeigte auf die Ingress-Adresse mit Token und starb beim nächsten Aufruf. */}
          <section className="ls-panel">
            <div className="ls-panel-head"><span className="ls-label">Aufs Handy</span></div>
            <p className="ha-pair-text" style={{ margin: '0 0 10px' }}>
              Cultivate Control als Kachel auf den Startbildschirm des Handys — mit QR-Code und Anleitung.
            </p>
            <Link to="/handy?tab=app" className="ls-btn is-small">Handy › App einrichten</Link>
          </section>
        </>
      )}
    </V1Page>
  )
}

export default HomeAssistantPage
