import type { SensorMetricType, TentDto } from '../../types'
import { TROCKNUNG_METRIKEN } from '../home-assistant/mapping-link'
import { RAUMWERTE_METRIKEN } from '../home-assistant/messgroessen'
import type { EntityDefinition } from '../home-assistant/messgroessen'

/**
 * cc.288: Was die Zuordnung zählt — eine Stelle für Anzeige, Kurzauskunft und die Zahl am Reiter.
 *
 * <b>„Offen“</b> ist eine Zelle (Zelt × Messgröße) ohne Entität. VPD zählt nie: der Fork rechnet ihn, wo er leer ist.
 * Außenwerte, die der Raum liefert, zählen nicht (die Zeile ist gesetzt). Ein Trockenzelt hat nur Temperatur, Feuchte
 * und VPD. In den Zahlen am Reiter und im Ton („warn“) zählen nur die Kernwerte; optionale Messgrößen werden gezählt
 * („1 von 2 zugeordnet“), mahnen aber nicht.
 */
export type ZuordnungKontext = { raumZelte: ReadonlySet<number> }

export function zeigtMessgroesse(zelt: TentDto, definition: EntityDefinition): boolean {
  return zelt.tentType !== 'Drying' || TROCKNUNG_METRIKEN.includes(definition.metricType as SensorMetricType)
}

export function istZugeordnet(zelt: TentDto, definition: EntityDefinition): boolean {
  return zelt.sensors.some((sensor) => sensor.metricType === definition.metricType && sensor.haEntityId.trim() !== '')
}

export function kommtVomRaum(zelt: TentDto, definition: EntityDefinition, kontext: ZuordnungKontext): boolean {
  return RAUMWERTE_METRIKEN.includes(definition.metricType) && kontext.raumZelte.has(zelt.id)
}

/** Die Zelte, für die diese Messgröße eine Zeile hat. */
export function zeltenMitZeile(zelte: readonly TentDto[], definition: EntityDefinition): TentDto[] {
  return zelte.filter((zelt) => zeigtMessgroesse(zelt, definition))
}

/** Die Zelte, bei denen diese Messgröße noch fehlt. */
export function offeneZelte(zelte: readonly TentDto[], definition: EntityDefinition, kontext: ZuordnungKontext): TentDto[] {
  if (definition.metricType === 'Vpd') return []
  return zeltenMitZeile(zelte, definition).filter((zelt) => !istZugeordnet(zelt, definition) && !kommtVomRaum(zelt, definition, kontext))
}

export type Kurzauskunft = { text: string; ton: 'warn' | 'ok' | undefined }

export function kurzauskunft(zelte: readonly TentDto[], definition: EntityDefinition, kontext: ZuordnungKontext): Kurzauskunft {
  if (definition.metricType === 'Vpd') return { text: 'wird gerechnet, wo leer', ton: undefined }
  const mitZeile = zeltenMitZeile(zelte, definition)
  const offen = offeneZelte(zelte, definition, kontext).length
  const text = offen === 0 ? (mitZeile.length === 1 ? 'zugeordnet' : `alle ${mitZeile.length} zugeordnet`) : `${mitZeile.length - offen} von ${mitZeile.length} zugeordnet`
  return { text, ton: definition.importance === 'core' ? (offen === 0 ? 'ok' : 'warn') : undefined }
}

/** Offene Kernwerte einer Gruppe — die Zahl am Reiter. */
export function offeneKernwerte(zelte: readonly TentDto[], definitionen: readonly EntityDefinition[], gruppe: EntityDefinition['group'], kontext: ZuordnungKontext): number {
  return definitionen
    .filter((definition) => definition.group === gruppe && definition.importance === 'core')
    .reduce((summe, definition) => summe + offeneZelte(zelte, definition, kontext).length, 0)
}

/** Aufgeklappt, wenn ein Kernwert fehlt — alles Vollständige und Optionale bleibt zu. */
export function standardOffen(zelte: readonly TentDto[], definition: EntityDefinition, kontext: ZuordnungKontext): boolean {
  return definition.importance === 'core' && offeneZelte(zelte, definition, kontext).length > 0
}
