export type EinrichtungBereich = 'schritte' | 'anlage' | 'technik'

/** Die Pfade der drei Bereiche — „Technik“ ist die Seite Geräte & Entitäten mit ihren eigenen Reitern. */
export const BEREICH_PFAD: Record<EinrichtungBereich, string> = {
  schritte: '/einrichtung',
  anlage: '/einrichtung?bereich=anlage',
  technik: '/geraete',
}
