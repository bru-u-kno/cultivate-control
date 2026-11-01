import type { GiltZeile } from './GiltFuer'
import type { TrocknungsplanZelt } from './trocknung-plan'

/**
 * Plan › „Gilt für …“ (A-030): die Rechnung ohne Oberfläche.
 *
 * Der Düngeplan gehört dem **Grow** (er richtet sich nach den Pflanzen und ihrer Woche), der Trocknungsplan dem
 * **Zelt** (dort findet das Trocknen statt). Beide Listen sehen gleich aus (`GiltFuer`); hier stehen nur die Sätze.
 */

/** Ein Grow, dem sich ein Düngeprogramm zuweisen lässt — wie `GET /api/programme/{id}/zuweisung` ihn liefert. */
export type ZuweisungGrow = {
  growId: number
  name: string
  zelt: string | null
  /** `Running` oder `Planning`. */
  status: string
  aktuellesProgramm: string | null
  zugewiesen: boolean
  hatPlan: boolean
  eigeneAenderungen: number
}

export type ZuweisungErgebnis = { growId: number; name: string; umgestellt: boolean; entfallen: number; hinweis: string | null }

const STATUS: Record<string, string> = { Running: 'läuft', Planning: 'geplant' }

const aenderungen = (n: number) => `${n} eigene Änderung${n === 1 ? '' : 'en'} am Plan`

/** Die Zeile eines Grows: Name, wo und in welchem Zustand, was heute gilt — und was das Zuweisen ersetzt. */
export function growZeile(g: ZuweisungGrow, angehakt: boolean): GiltZeile {
  const unter = [g.zelt ?? 'kein Zelt', STATUS[g.status], `heute: ${g.aktuellesProgramm ?? 'kein Programm'}`]
    .filter((teil): teil is string => Boolean(teil))
    .join(' · ')
  if (g.zugewiesen) {
    return { id: g.growId, name: g.name, unter, zusatz: 'Schon zugewiesen', zusatzTon: 'ok', angehakt: true, gesperrt: true }
  }
  const folge = [
    g.aktuellesProgramm ? `Ersetzt: ${g.aktuellesProgramm}` : null,
    g.hatPlan ? (g.eigeneAenderungen > 0 ? aenderungen(g.eigeneAenderungen) : 'Wochenwerte wechseln') : null,
  ].filter((teil): teil is string => teil !== null)
  return {
    id: g.growId, name: g.name, unter, angehakt,
    zusatz: angehakt && folge.length > 0 ? folge.join(' · ') : undefined,
    zusatzTon: 'warn',
  }
}

/** Die angekreuzten Grows, bei denen ein Wechsel eigene Änderungen am Plan berührt — hier muss der Nutzer entscheiden. */
export function brauchenEntscheidung(grows: readonly ZuweisungGrow[], gewaehlt: ReadonlySet<number>): ZuweisungGrow[] {
  return grows.filter((g) => gewaehlt.has(g.growId) && !g.zugewiesen && g.hatPlan && g.eigeneAenderungen > 0)
}

/** Der Satz nach dem Zuweisen: wer umgestellt wurde, wer nicht ging, wie viele eigene Änderungen entfielen. */
export function ergebnisText(ergebnisse: readonly ZuweisungErgebnis[]): { text: string; ton: 'ok' | 'warn' } {
  const ok = ergebnisse.filter((e) => e.umgestellt)
  const nicht = ergebnisse.filter((e) => !e.umgestellt)
  const entfallen = ok.reduce((summe, e) => summe + e.entfallen, 0)
  const teile: string[] = []
  if (ok.length > 0) teile.push(`Zugewiesen: ${ok.map((e) => e.name).join(', ')}.`)
  if (entfallen > 0) teile.push(`${aenderungen(entfallen)} entfielen, weil das neue Programm diese Wochen nicht kennt.`)
  for (const e of nicht) teile.push(`${e.name}: ${e.hinweis ?? 'nicht umgestellt.'}`)
  return { text: teile.join(' '), ton: nicht.some((e) => e.hinweis !== 'Hat dieses Programm schon.') ? 'warn' : 'ok' }
}

/** Ein weiteres Trockenzelt mit dem Stand seines Plans — für die Liste „Gilt für diese Trockenzelte“. */
export type TrockenzeltStand = { id: number; name: string; plan: TrocknungsplanZelt | null }

/** Die Zeile eines Trockenzelts: was dort heute gilt und ob gerade eine Charge trocknet. */
export function zeltZeile(z: TrockenzeltStand, angehakt: boolean, hier: boolean): GiltZeile {
  if (hier) {
    return { id: z.id, name: z.name, unter: 'Dieser Plan wird hier gerade bearbeitet', angehakt: true, gesperrt: true }
  }
  const plan = z.plan
  const unter = plan === null
    ? 'Stand nicht lesbar'
    : [plan.laeuft ? `Charge trocknet · Tag ${plan.laeuft.tag}` : 'keine Charge', plan.istStandard ? 'heute: Standard „langsam“' : 'heute: eigener Plan']
      .join(' · ')
  return {
    id: z.id, name: z.name, unter, angehakt,
    zusatz: angehakt && plan && !plan.istStandard ? 'Ersetzt den eigenen Plan dieses Zelts' : undefined,
    zusatzTon: 'warn',
  }
}
