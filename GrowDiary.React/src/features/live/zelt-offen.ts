import { useCallback, useState } from 'react'

/**
 * Welche Zelte auf der Live-Seite aufgeklappt sind.
 *
 * <b>Am Gerät, nicht am Zelt.</b> Wie bei den eingeklappten Kacheln
 * (`eingeklappt.ts`) ist das eine Eigenschaft des Bildschirms: am Telefon hat
 * man das Trockenzelt zu und das Blütezelt offen, am Schreibtisch beide offen.
 * Deshalb im Browser abgelegt — kein Datenmodell, keine Wanderung beim Update.
 *
 * <b>Ohne Merkzettel.</b> Wer noch nie ein Zelt auf- oder zugeklappt hat, sieht
 * das Standardzelt (das mit dem laufenden Grow) offen und die übrigen zu — mit
 * ihren Kernwerten im Kopf. Sobald für ein Zelt etwas gemerkt ist, gilt das.
 * Ein Zelt, das es nicht mehr gibt, bleibt als toter Eintrag liegen und schadet nicht.
 */

const KEY = 'growos.live.zelteOffen'

export type ZeltOffen = Record<string, boolean>

export function leseZeltOffen(roh: string | null): ZeltOffen {
  if (!roh) return {}
  try {
    const gelesen: unknown = JSON.parse(roh)
    if (typeof gelesen !== 'object' || gelesen === null || Array.isArray(gelesen)) return {}
    const ergebnis: ZeltOffen = {}
    for (const [id, wert] of Object.entries(gelesen)) {
      if (typeof wert === 'boolean') ergebnis[id] = wert
    }
    return ergebnis
  } catch {
    return {}
  }
}

export function istZeltOffen(gemerkt: ZeltOffen, zeltId: number, standardId: number | null): boolean {
  const eintrag = gemerkt[String(zeltId)]
  return typeof eintrag === 'boolean' ? eintrag : zeltId === standardId
}

export function setzeZeltOffen(gemerkt: ZeltOffen, zeltId: number, offen: boolean): ZeltOffen {
  return { ...gemerkt, [String(zeltId)]: offen }
}

function lade(): ZeltOffen {
  try { return leseZeltOffen(localStorage.getItem(KEY)) } catch { return {} }
}

function speichere(gemerkt: ZeltOffen): void {
  try { localStorage.setItem(KEY, JSON.stringify(gemerkt)) } catch { /* Privatmodus */ }
}

export type ZelteOffen = {
  istOffen: (zeltId: number, standardId: number | null) => boolean
  umschalten: (zeltId: number, standardId: number | null) => void
  /** Auf einen bestimmten Zustand setzen — für das Zuklappen eines per Meldung geöffneten Zelts. */
  setze: (zeltId: number, offen: boolean) => void
}

export function useZeltOffen(): ZelteOffen {
  const [gemerkt, setGemerkt] = useState<ZeltOffen>(lade)
  const ersetzen = useCallback((aendern: (vorher: ZeltOffen) => ZeltOffen) => {
    setGemerkt((vorher) => {
      const naechste = aendern(vorher)
      speichere(naechste)
      return naechste
    })
  }, [])
  const umschalten = useCallback((zeltId: number, standardId: number | null) => {
    ersetzen((vorher) => setzeZeltOffen(vorher, zeltId, !istZeltOffen(vorher, zeltId, standardId)))
  }, [ersetzen])
  const setze = useCallback((zeltId: number, offen: boolean) => {
    ersetzen((vorher) => setzeZeltOffen(vorher, zeltId, offen))
  }, [ersetzen])
  return {
    istOffen: (zeltId, standardId) => istZeltOffen(gemerkt, zeltId, standardId),
    umschalten,
    setze,
  }
}
