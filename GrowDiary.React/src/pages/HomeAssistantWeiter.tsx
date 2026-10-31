import { Navigate, useSearchParams } from 'react-router-dom'
import { VERBINDUNG_PFAD } from '../features/geraete/technik-pfade'
import { lesenMappingZiel, mappingLink } from '../features/home-assistant/mapping-link'

/**
 * Die alte Adresse `/home-assistant` (cc.286): Die Verbindung wohnt unter Geräte & Entitäten. Alte Zuordnungs-Links
 * (mit Zelt oder Messgröße, vor cc.285 „Mappen“) führen auf die Zuordnung, alles andere auf die Verbindung.
 */
export default function HomeAssistantWeiter() {
  const [params] = useSearchParams()
  if (!params.has('zelt') && !params.has('messwert')) return <Navigate to={VERBINDUNG_PFAD} replace />
  const ziel = lesenMappingZiel(params)
  return <Navigate to={mappingLink(ziel.tentId, ziel.definition?.metricType)} replace />
}
