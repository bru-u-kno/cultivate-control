import { formatNumber } from '../../utils'

/** Antwort von `GET/PUT /api/trocknung/zuluft` (cc.337): Die Zuluft folgt dem Trocknungsplan. */
export type ZuluftTrocknungZustand = 'saugt' | 'zeltZuKalt' | 'frost' | 'zeltZuTrocken' | 'zeltImPlan' | 'aussenFeuchter' | 'keinMesswert'

export interface ZuluftTrocknung {
  trockenzelt: boolean
  tentId: number | null
  zeltName: string | null
  trocknet: boolean
  folgt: boolean
  regelt: boolean
  hoechststufe: number
  abstandProzent: number
  zeltTempC: number | null
  zeltRhProzent: number | null
  zeltAbsolutGm3: number | null
  aussenAbsolutGm3: number | null
  planRhProzent: number | null
  planTempC: number | null
  tag: number | null
  tageGeplant: number | null
  zustand: ZuluftTrocknungZustand
  stufe: number
  differenz: number | null
  laeuftStufe: number | null
  gescheitert: boolean
}

export type Ton = 'ok' | 'warn' | 'grau'

const z = (wert: number | null | undefined, stellen = 1) => (wert == null ? '–' : formatNumber(wert, stellen))

/** Die Plakette oben rechts an der Karte: Zustand in einem Wort. */
export function zuluftPlakette(s: ZuluftTrocknung): { text: string; ton: Ton } {
  if (!s.folgt) return { text: 'nicht eingebunden', ton: 'grau' }
  if (!s.trocknet) return { text: 'wartet auf Trocknung', ton: 'grau' }
  if (s.gescheitert) return { text: 'schaltet nicht', ton: 'warn' }
  switch (s.zustand) {
    case 'saugt': return { text: 'saugt', ton: 'ok' }
    case 'zeltImPlan': return { text: 'im Plan', ton: 'ok' }
    case 'aussenFeuchter': return { text: 'Außenluft feuchter', ton: 'grau' }
    case 'zeltZuTrocken': return { text: 'Zelt zu trocken', ton: 'warn' }
    case 'zeltZuKalt': return { text: 'Zelt zu kalt', ton: 'warn' }
    case 'frost': return { text: 'Frostschutz', ton: 'warn' }
    default: return { text: 'kein Messwert', ton: 'warn' }
  }
}

/** „Was das jetzt heißt“: ein bis zwei Sätze in Klartext, mit den Zahlen von jetzt. */
export function zuluftErklaerung(s: ZuluftTrocknung): string {
  if (!s.folgt) return 'Die Zuluft regelt wie bisher nach der Kellerluft. Der Trocknungsplan wirkt auf sie nicht.'
  if (!s.trocknet) return 'Im Zelt trocknet gerade nichts. Sobald eine Charge trocknet, folgt die Zuluft dem Plan; bis dahin regelt sie wie bisher nach der Kellerluft.'
  const plan = s.planRhProzent
  const zelt = `${z(s.zeltRhProzent)} %`
  switch (s.zustand) {
    case 'saugt':
      return `Das Zelt ist mit ${zelt} feuchter als der Plan (${z(plan)} %), die Außenluft ist um ${z(s.differenz)} g/m³ trockener. Die Zuluft saugt auf Stufe ${s.stufe}. Der Entfeuchter springt erst an, wenn sie nicht reicht.`
    case 'zeltImPlan':
      return `Das Zelt liegt mit ${zelt} im Plan (${z(plan)} %). Die Zuluft bleibt aus; der Entfeuchter hält den Planwert, falls es wieder feuchter wird.`
    case 'aussenFeuchter':
      return `Das Zelt ist mit ${zelt} feuchter als der Plan, aber die Außenluft trocknet nicht genug (Unterschied ${z(s.differenz)} g/m³). Die Zuluft bleibt aus.`
    case 'zeltZuTrocken':
      return `Das Zelt liegt mit ${zelt} mehr als ${s.abstandProzent} Prozentpunkte unter dem Plan (${z(plan)} %). Die Zuluft bleibt aus, damit es nicht zu schnell trocknet.`
    case 'zeltZuKalt':
      return `Das Zelt ist mit ${z(s.zeltTempC)} °C zu kalt. Die Zuluft bleibt aus und läuft wieder an, wenn es 1 °C über der Grenze „Zelttemperatur min.“ liegt.`
    case 'frost':
      return 'Draußen ist es zu kalt (Frostschutz). Die Zuluft bleibt aus und läuft wieder an, wenn es 1 °C über der Grenze „Außentemperatur min.“ liegt.'
    default:
      return 'Ein Messwert (Zelt, Außenluft oder Plan) lässt sich nicht lesen. Die Zuluft bleibt aus, bis er wieder da ist.'
  }
}

/** Der Hinweis unter dem Haken. */
export function zuluftHinweis(folgt: boolean): string {
  return folgt
    ? 'Der Fork schaltet die Zuluft nach dem Plan, solange das Zelt trocknet. Den Modus des Controllers ändert er nie; die Regelung für den Keller ruht so lange.'
    : 'Solange das Zelt trocknet, saugt die Zuluft nach dem Plan an statt nach dem Keller. Den Modus des Controllers ändert der Fork nie.'
}
