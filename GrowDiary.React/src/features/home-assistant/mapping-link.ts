import type { SensorMetricType } from '../../types'
import { definitions } from './messgroessen'
import type { EntityDefinition } from './messgroessen'

/** Messgrößen, die ein Trockenzelt überhaupt hat — alles andere (Licht, CO₂, Reservoir, Technik) gehört dort nicht hin. */
export const TROCKNUNG_METRIKEN: SensorMetricType[] = ['AirTemperature', 'Humidity', 'Vpd']

/** Link auf die Zuordnungsseite, direkt auf das Zelt und die Messgröße (Seite scrollt hin und hebt die Zeile hervor). */
export function mappingLink(zeltId?: number | null, messwert?: SensorMetricType | null): string {
  const query = new URLSearchParams()
  if (zeltId != null) query.set('zelt', String(zeltId))
  if (messwert) query.set('messwert', messwert)
  const text = query.toString()
  return text ? `/home-assistant?${text}` : '/home-assistant'
}

export type MappingZiel = { tentId: number | null; definition: EntityDefinition | null }

/** Liest Zelt und Messgröße aus der Adresse; Unbekanntes wird verworfen statt zu raten. */
export function lesenMappingZiel(params: URLSearchParams): MappingZiel {
  const zelt = Number(params.get('zelt'))
  const messwert = params.get('messwert')
  return {
    tentId: Number.isInteger(zelt) && zelt > 0 ? zelt : null,
    definition: definitions.find((definition) => definition.metricType === messwert) ?? null,
  }
}
