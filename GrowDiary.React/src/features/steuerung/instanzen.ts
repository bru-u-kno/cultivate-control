import { useCallback, useEffect, useState } from 'react'
import { apiFetch } from '../../api'
import type { SteuerungInstanz, SteuerungInstanzen, SteuerungZeltKurz, SteuerungZuweisung } from './steuerung-typen'

/** Die Zelte, aus denen eine Instanz eines wählen kann (die aktiven; leer, solange sie nicht gelesen sind). */
export function useZelte(): SteuerungZeltKurz[] {
  const [zelte, setZelte] = useState<SteuerungZeltKurz[]>([])
  useEffect(() => {
    const controller = new AbortController()
    void apiFetch<SteuerungZuweisung>('/api/steuerung/zuweisung', { signal: controller.signal })
      .then((z) => { if (!controller.signal.aborted) setZelte(z.zelte) })
      .catch(() => undefined)
    return () => controller.abort()
  }, [])
  return zelte
}

/** Die weiteren Instanzen mit ihrem Zelt. */
export function useInstanzen(): { instanzen: SteuerungInstanz[] | null; neuLaden: () => Promise<void> } {
  const [instanzen, setInstanzen] = useState<SteuerungInstanz[] | null>(null)
  useEffect(() => {
    const controller = new AbortController()
    void apiFetch<SteuerungInstanzen>('/api/steuerung/instanzen', { signal: controller.signal })
      .then((antwort) => { if (!controller.signal.aborted) setInstanzen(antwort.instanzen) })
      .catch(() => undefined)
    return () => controller.abort()
  }, [])
  const neuLaden = useCallback(async () => {
    setInstanzen((await apiFetch<SteuerungInstanzen>('/api/steuerung/instanzen')).instanzen)
  }, [])
  return { instanzen, neuLaden }
}

/** Einen weiteren Wasserkühler anlegen; die Antwort ist die Liste aller Instanzen, die neue ist die mit der höchsten Nummer. */
export async function chillerAnlegen(name: string, zeltId: number | null): Promise<SteuerungInstanz> {
  const antwort = await apiFetch<SteuerungInstanzen>('/api/steuerung/instanzen', {
    method: 'POST',
    body: JSON.stringify({ art: 'chiller', name, zeltId }),
  })
  const neu = antwort.instanzen.filter((i) => i.art === 'chiller').sort((a, b) => b.nummer - a.nummer)[0]
  if (!neu) throw new Error('Die neue Instanz fehlt in der Antwort.')
  return neu
}

export const instanzUmbenennen = (modul: string, name: string) =>
  apiFetch<SteuerungInstanzen>(`/api/steuerung/instanzen/${encodeURIComponent(modul)}`, { method: 'PUT', body: JSON.stringify({ name }) })

/** `ausHa`: auch Automationen, Rechenwerte und Helfer der Instanz aus Home Assistant entfernen (nur wenn das ganz gelingt, verschwindet die Instanz). */
export const instanzEntfernen = (modul: string, ausHa = false) =>
  apiFetch<SteuerungInstanzen>(`/api/steuerung/instanzen/${encodeURIComponent(modul)}${ausHa ? '?ausHa=true' : ''}`, { method: 'DELETE' })

export const instanzZeltSetzen = (modul: string, zeltId: number) =>
  apiFetch<SteuerungZuweisung>('/api/steuerung/zuweisung', { method: 'PUT', body: JSON.stringify({ zuordnungen: { [modul]: zeltId } }) })
