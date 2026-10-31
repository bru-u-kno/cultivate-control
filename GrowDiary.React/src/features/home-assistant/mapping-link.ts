import type { SensorMetricType } from '../../types'
import { definitions } from './messgroessen'
import type { EntityDefinition } from './messgroessen'

/** Messgrößen, die ein Trockenzelt überhaupt hat — alles andere (Licht, CO₂, Reservoir, Technik) gehört dort nicht hin. */
export const TROCKNUNG_METRIKEN: SensorMetricType[] = ['AirTemperature', 'Humidity', 'Vpd']

/** Der Reiter „Zuordnung“ unter Geräte & Entitäten — die eine Stelle, an der Messgrößen Entitäten zugeordnet werden (cc.285). */
export const ZUORDNUNG_PFAD = '/geraete'

/** Link auf die Zuordnung, direkt auf das Zelt und die Messgröße (Seite scrollt hin und hebt die Zeile hervor). */
export function mappingLink(zeltId?: number | null, messwert?: SensorMetricType | null): string {
  const query = new URLSearchParams({ reiter: 'zuordnung' })
  if (zeltId != null) query.set('zelt', String(zeltId))
  if (messwert) query.set('messwert', messwert)
  return `${ZUORDNUNG_PFAD}?${query.toString()}`
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
