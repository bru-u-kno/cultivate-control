/* src/letzte-seite.ts — Cultivate Control
   Nach einem Neuladen wieder dort weitermachen, wo man war.

   Die App läuft in Home Assistant in einem iframe, dessen Adresse in der HA-Adresszeile nicht auftaucht. Lädt HA
   die Seite neu (die Companion-App auf dem Handy tut das gelegentlich, wenn man nach einigen Minuten zurückkehrt),
   startet der iframe auf der Startseite „Live“ — eine offene Unterseite ist weg. Deshalb merkt sich die App die
   zuletzt gesehene Seite am Gerät und stellt sie beim Start wieder her, aber nur kurz nach der letzten Nutzung:
   wer das Panel nach Stunden öffnet, beginnt wie gewohnt auf Live. */

const KEY = 'growos.letzteSeite'

/** So lange nach der letzten Nutzung gilt die gemerkte Seite noch als „dieselbe Sitzung“. */
export const GUELTIG_MS = 30 * 60 * 1000

/** Nur schlichte App-Pfade mit optionaler Abfrage: „/tagebuch“, „/steuerung/chiller?zelt=2“. Kein „//“, kein Schema. */
const PFAD = /^\/(?!\/)[A-Za-z0-9_\-/]*(\?[A-Za-z0-9_\-=&.%,]*)?$/

type Gemerkt = { pfad: string; zeit: number }

export function merkeSeite(pfad: string, jetzt: number = Date.now()): void {
  if (!PFAD.test(pfad)) return
  try { localStorage.setItem(KEY, JSON.stringify({ pfad, zeit: jetzt } satisfies Gemerkt)) } catch { /* Privatmodus */ }
}

/** Nur die Zeit der letzten Nutzung nachziehen (Seite wird verlassen oder versteckt). */
export function beruehreSeite(jetzt: number = Date.now()): void {
  const g = lese()
  if (g) merkeSeite(g.pfad, jetzt)
}

function lese(): Gemerkt | null {
  try {
    const g: unknown = JSON.parse(localStorage.getItem(KEY) ?? 'null')
    if (!g || typeof g !== 'object') return null
    const { pfad, zeit } = g as Partial<Gemerkt>
    return typeof pfad === 'string' && typeof zeit === 'number' ? { pfad, zeit } : null
  } catch {
    return null
  }
}

/**
 * Wohin der Start umgeleitet werden soll — oder null, wenn auf der Startseite zu bleiben ist.
 * Nur wenn die App ganz ohne Ziel startet (Startseite ohne Abfrage und Anker) und die gemerkte Seite frisch ist.
 */
export function wiederherstellungsZiel(
  pfad: string,
  suche: string,
  anker: string,
  jetzt: number = Date.now(),
): string | null {
  if (pfad !== '/' || suche || anker) return null
  const g = lese()
  if (!g || !PFAD.test(g.pfad) || g.pfad === '/') return null
  const alter = jetzt - g.zeit
  return alter >= 0 && alter <= GUELTIG_MS ? g.pfad : null
}
