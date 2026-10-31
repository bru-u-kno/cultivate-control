import type { TentDto, UpdateTentRequest } from '../../types'

/**
 * Das Zelt als Speicher-Anfrage — OHNE Sensorzeilen. Das Backend ersetzt `sensors` nur, wenn der Rumpf sie mitbringt
 * (`SettingsApiController.SaveTent`); wer hier nur die Kameras ändert, lässt die Zuordnung also unberührt. Genau das
 * Gegenteil hatte die Zuordnungsseite bis cc.284: sie schickte immer alle Zeilen aus einem beim Öffnen geladenen Entwurf mit.
 */
export function zeltOhneSensoren(tent: TentDto, cameras: string[]): UpdateTentRequest {
  return {
    name: tent.name, status: tent.status, kind: tent.kind, tentType: tent.tentType, notes: tent.notes, displayOrder: tent.displayOrder,
    accentColor: tent.accentColor, widthCm: tent.widthCm, depthCm: tent.depthCm, tentHeightCm: tent.tentHeightCm, lightType: tent.lightType,
    lightWatt: tent.lightWatt, lightController: tent.lightController, lightControllerEntityId: tent.lightControllerEntityId,
    exhaustFanCount: tent.exhaustFanCount, exhaustM3h: tent.exhaustM3h, circulationFanCount: tent.circulationFanCount,
    hvacController: tent.hvacController, hvacControllerEntityId: tent.hvacControllerEntityId, co2Available: tent.co2Available,
    hasCo2Enrichment: tent.hasCo2Enrichment, raumId: tent.raumId, cameraEntityId: null,
    cameras: cameras.map((camera) => camera.trim()).filter((camera) => camera.length > 0),
  } as UpdateTentRequest
}
