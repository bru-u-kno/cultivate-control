import { formatNumber } from '../../utils'

/** Antwort von `GET/PUT /api/tents/{id}/trocknung/steuerung` (A-029, Etappe 3, Teil B). */
export type Betrieb = 'melden' | 'regeln'
export type SteuerungUrteil = 'nichtZugeordnet' | 'unbekannt' | 'geraetAus' | 'ohneAutomatik' | 'keinAusloeser' | 'keinPlan' | 'passt' | 'weichtAb'

export interface SteuerungGeraet {
  rolle: 'entfeuchter' | 'kuehlung' | 'abluft-feuchte' | 'abluft-temp'
  portName: string | null
  folgt: boolean
  modus: string | null
  wertAmGeraet: number | null
  einheit: string | null
  planWert: number | null
  urteil: SteuerungUrteil
  vorher: number | null
  zuletztGesetzt: string | null
}

export interface TrocknungSteuerung {
  trocknet: boolean
  betrieb: Betrieb
  portsErreichbar: boolean
  tag: number | null
  tageGeplant: number | null
  etappe: string | null
  tempMin: number | null
  tempMax: number | null
  feuchteMin: number | null
  feuchteMax: number | null
  geraete: SteuerungGeraet[]
}

export const zahl = (wert: number) => formatNumber(wert, Number.isInteger(wert) ? 0 : 1)

export const ROLLE = {
  entfeuchter: { titel: 'Entfeuchter', feld: 'Luftfeuchte-Obergrenze', einheit: '%' },
  kuehlung: { titel: 'Kühlung', feld: 'Temperatur-Obergrenze', einheit: '°C' },
  'abluft-feuchte': { titel: 'Abluft', feld: 'Luftfeuchte-Obergrenze', einheit: '%' },
  'abluft-temp': { titel: 'Abluft', feld: 'Temperatur-Obergrenze', einheit: '°C' },
} as const

/** Ein Schalter „Folgt dem Plan“ je Gerät. Die Abluft hat zwei Grenzen (Feuchte, Temperatur), aber nur einen Schalter. */
export type Schalter = 'entfeuchter' | 'kuehlung' | 'abluft'
export type Folgt = Record<Schalter, boolean>
export const SCHALTER: Schalter[] = ['entfeuchter', 'kuehlung', 'abluft']
export const schalterVon = (rolle: SteuerungGeraet['rolle']): Schalter => (rolle === 'abluft-feuchte' || rolle === 'abluft-temp' ? 'abluft' : rolle)

/** Plakette je Gerät: Text und Ton (warn = gelb, ok = grün). Hängt von Betriebsart und Schalter ab. */
export function plakette(g: SteuerungGeraet, betrieb: Betrieb): { text: string; ton?: 'warn' | 'ok' } {
  if (g.urteil === 'nichtZugeordnet') return { text: 'nicht zugeordnet' }
  if (g.urteil === 'unbekannt') return { text: 'nicht lesbar', ton: 'warn' }
  if (g.urteil === 'geraetAus') return { text: 'Gerät aus' }
  if (g.urteil === 'ohneAutomatik') return { text: 'ohne Automatik', ton: 'warn' }
  if (!g.folgt) return betrieb === 'regeln' ? { text: 'eigener Wert', ton: 'ok' } : { text: 'bleibt', ton: 'ok' }
  if (g.urteil === 'keinAusloeser') return { text: 'ohne Auslöser', ton: 'warn' }
  if (g.urteil === 'keinPlan') return { text: 'kein Plan' }
  if (g.urteil === 'passt') return { text: betrieb === 'regeln' ? 'folgt dem Plan' : 'passt', ton: 'ok' }
  return { text: betrieb === 'regeln' ? 'wird nachgezogen' : 'würde ändern', ton: 'warn' }
}

/** Was beim Einschalten von „Regeln“ geschrieben würde: eine Zeile je Gerät, das folgt und abweicht. */
export function wuerdeSchreiben(geraete: SteuerungGeraet[], folgt: Partial<Folgt>) {
  return geraete
    .filter((g) => folgt[schalterVon(g.rolle)] && g.urteil === 'weichtAb' && g.planWert != null && g.wertAmGeraet != null)
    .map((g) => ({
      rolle: g.rolle,
      portName: g.portName ?? ROLLE[g.rolle].titel,
      feld: ROLLE[g.rolle].feld,
      von: g.wertAmGeraet as number,
      nach: g.planWert as number,
      einheit: ROLLE[g.rolle].einheit,
    }))
}

/** Der Hinweis unter dem Schalter: was der Fork anfasst — und was nie. */
export function geraetHinweis(g: SteuerungGeraet): string {
  const grenze = 'Der Fork ändert nur die Grenze, nie den Modus.'
  if (!g.folgt) return 'Folgt dem Plan ist aus: dein Wert bleibt unangetastet.'
  // Der Controller stellt die Grenze nur im Modus Auto bereit — sonst gibt es nichts zu lesen und nichts zu schreiben.
  if (g.urteil === 'geraetAus' || g.urteil === 'ohneAutomatik') {
    return `Der Port steht auf ${g.modus === 'Off' ? 'Aus' : g.modus ?? 'einem anderen Modus'}; so lange stellt das Gerät die Grenze nicht bereit, und der Fork schreibt nichts. ${grenze} Sobald du den Port auf Auto stellst, setzt der Fork sie.`
  }
  return grenze
}

export const betriebText = (betrieb: Betrieb) => (betrieb === 'regeln' ? 'Regeln' : 'Nur melden')
