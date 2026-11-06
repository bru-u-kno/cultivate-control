import { formatNumber } from '../../utils'

/** Antwort von `GET/PUT /api/tents/{id}/trocknung/steuerung` (A-029, Etappe 3, Teil B). */
export type Betrieb = 'melden' | 'regeln'
export type SteuerungUrteil = 'nichtZugeordnet' | 'unbekannt' | 'geraetAus' | 'ohneAutomatik' | 'keinAusloeser' | 'keinPlan' | 'passt' | 'weichtAb'
  // Entfeuchter, den der Fork ein- und ausschaltet (06.11.2026):
  | 'schaltet' | 'portNichtSchaltbar' | 'keinMesswert' | 'schaltetNicht'

/** Der Entfeuchter, den der Fork schaltet: Schwellen aus dem Plan, Luftfeuchte im Zelt und der letzte Schaltvorgang. */
export interface SteuerungSchalten {
  zustand: 'laeuft' | 'steht' | 'wartet' | 'schaltetEin' | 'schaltetAus' | 'portNichtSchaltbar' | 'keinMesswert'
  feuchte: number | null
  einAb: number | null
  ausUnter: number | null
  modus: string | null
  zuletztUtc: string | null
  zuletztAktion: 'ein' | 'aus' | null
  gescheitert: boolean
}

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
  /** Nur beim Entfeuchter, solange er eingebunden ist und eine Charge trocknet. */
  schalten?: SteuerungSchalten | null
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
  /** Ein Vorgang läuft gerade (auch von einer anderen Seite angestoßen); fehlt, wenn nichts läuft. */
  inArbeit?: { phase: ArbeitPhase; seit: string; clientId: string | null } | null
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

/** Plakette je Gerät: Text und Ton (warn = gelb, ok = grün, grau = ruhig). Hängt von Betriebsart und Haken „Gerät einbinden“ ab. */
export type PlaketteTon = 'warn' | 'ok' | 'grau'

/** Die Zeile, die Kopf, Plakette und Erklärung der Karte bestimmt: die mit dem größten Problem, sonst die erste. */
const VORRANG: SteuerungUrteil[] = ['schaltetNicht', 'portNichtSchaltbar', 'keinMesswert', 'unbekannt', 'keinAusloeser', 'ohneAutomatik', 'geraetAus', 'weichtAb', 'keinPlan']
export const kopfZeile = (zeilen: SteuerungGeraet[]): SteuerungGeraet => {
  // Bei der Abluft (zwei Grenzen) bestimmt die Zeile mit dem größten Problem die Karte — „hält den Plan“ erst, wenn keine etwas zu melden hat.
  for (const urteil of VORRANG) {
    const z = zeilen.find((x) => x.urteil === urteil)
    if (z) return z
  }
  return zeilen[0]
}

export function plakette(g: SteuerungGeraet, betrieb: Betrieb): { text: string; ton: PlaketteTon } {
  if (g.urteil === 'nichtZugeordnet') return { text: 'kein Port zugeordnet', ton: 'grau' }
  if (!g.folgt) return { text: 'nicht eingebunden', ton: 'grau' }
  if (g.urteil === 'schaltet') return { text: betrieb === 'regeln' ? 'schaltet nach Plan' : 'würde schalten', ton: betrieb === 'regeln' ? 'ok' : 'grau' }
  if (g.urteil === 'schaltetNicht') return { text: 'kann nicht schalten', ton: 'warn' }
  if (g.urteil === 'portNichtSchaltbar') return { text: g.modus === 'Auto' ? 'Port steht auf Auto' : 'Port nicht schaltbar', ton: 'warn' }
  if (g.urteil === 'keinMesswert') return { text: 'kein Messwert', ton: 'warn' }
  if (g.urteil === 'unbekannt') return { text: 'Wert nicht lesbar', ton: 'warn' }
  if (g.urteil === 'geraetAus') return { text: 'Port steht auf Aus', ton: 'warn' }
  if (g.urteil === 'ohneAutomatik') return { text: 'Port nicht auf Auto', ton: 'warn' }
  if (g.urteil === 'keinAusloeser') return { text: 'Auslöser fehlt', ton: 'warn' }
  if (g.urteil === 'keinPlan') return { text: 'kein Planwert heute', ton: 'grau' }
  if (g.urteil === 'passt') return { text: betrieb === 'regeln' ? 'hält den Plan' : 'passt zum Plan', ton: 'ok' }
  return { text: betrieb === 'regeln' ? 'stellt nach' : 'würde nachstellen', ton: 'warn' }
}

/** Was die Steuerung je Schalter erreichen will — der Satz hinter „Gerät einbinden“. */
export const ZIEL_TEXT: Record<Schalter, string> = {
  entfeuchter: 'die Luftfeuchte',
  kuehlung: 'die Temperatur',
  abluft: 'Luftfeuchte und Temperatur',
}

const planText = (zeilen: SteuerungGeraet[]) =>
  zeilen.filter((z) => z.planWert != null).map((z) => `${zahl(z.planWert as number)} ${ROLLE[z.rolle].einheit}`).join(' und ')

/** Der Hinweis unter „Gerät einbinden“: was der Haken bewirkt — je nach Betriebsart. */
export function einbindenHinweis(schalter: Schalter, eingebunden: boolean, betrieb: Betrieb = 'regeln'): string {
  if (schalter === 'entfeuchter') {
    if (!eingebunden) return 'Setze den Haken, damit die Steuerung den Entfeuchter nach der Luftfeuchte im Zelt ein- und ausschaltet.'
    return betrieb === 'regeln'
      ? 'Eingebunden: Die Steuerung schaltet den Entfeuchter ein und aus (jede Minute geprüft). Der Auto-Modus am Gerät wird nicht gebraucht.'
      : 'Eingebunden: Im Modus „Nur anzeigen“ zeigt die Steuerung, wann sie schalten würde. Mit „Regeln“ schaltet sie den Entfeuchter selbst.'
  }
  if (!eingebunden) return `Setze den Haken, damit die Steuerung ${ZIEL_TEXT[schalter]} auf den Planwert bringt und dort hält.`
  return betrieb === 'regeln'
    ? `Eingebunden: Die Steuerung versucht, ${ZIEL_TEXT[schalter]} auf den Planwert zu bringen und dort zu halten.`
    : `Eingebunden: Im Modus „Nur anzeigen“ zeigt die Steuerung, was sie einstellen würde. Mit „Regeln“ bringt sie ${ZIEL_TEXT[schalter]} auf den Planwert und hält sie dort.`
}

/** Was mit dem Wert am Gerät ist, wenn das Gerät nicht (mehr) eingebunden ist — mit gemerktem Wert ist es der zuletzt gesetzte, nicht der alte. */
export function ohneEinbindungText(zeilen: SteuerungGeraet[]): string {
  const gemerkt = zeilen.find((z) => z.vorher != null)
  if (gemerkt?.vorher == null) return 'Die Steuerung fasst dieses Gerät nicht an. Dein Wert am Gerät bleibt, wie er ist.'
  return `Die Steuerung fasst dieses Gerät nicht mehr an. Der zuletzt gesetzte Wert bleibt am Gerät; deinen alten Wert (${zahl(gemerkt.vorher)} ${ROLLE[gemerkt.rolle].einheit}) stellt „Alte Werte zurück“ wieder her.`
}

/** „Was das jetzt heißt“: der Zustand des Geräts in einem Klartext-Satz, mit den Zahlen. */
export function erklaerung(zeilen: SteuerungGeraet[], betrieb: Betrieb): string {
  const g = kopfZeile(zeilen)
  const schalter = schalterVon(g.rolle)
  const einheit = ROLLE[g.rolle].einheit
  const wert = g.wertAmGeraet == null ? '–' : `${zahl(g.wertAmGeraet)} ${einheit}`
  const plan = g.planWert == null ? '–' : `${zahl(g.planWert)} ${einheit}`
  if (g.urteil === 'nichtZugeordnet') return 'Diesem Gerät ist noch kein Port zugeordnet. Das geht unter „Ports bearbeiten“.'
  if (!g.folgt) return ohneEinbindungText(zeilen)
  if (g.urteil === 'schaltet' && g.schalten) return schaltText(g.schalten, betrieb)
  if (g.urteil === 'schaltetNicht') return 'Der Entfeuchter ließ sich nicht schalten: Die Cloud des Controllers hat den Befehl nicht angenommen. Die Steuerung versucht es jede Minute erneut und meldet es dir nach 10 Minuten per Push.'
  if (g.urteil === 'portNichtSchaltbar') {
    const modus = g.modus ? ` (aktuell: ${g.modus})` : ''
    return `Der Port steht nicht auf On oder Off${modus}. Im Auto-Modus nimmt der Controller keine Änderung an. Stell den Port in der App des Controllers auf On, dann schaltet die Steuerung den Entfeuchter. Auto stellt sie nie selbst ein.`
  }
  if (g.urteil === 'keinMesswert') return 'Die Luftfeuchte im Trockenzelt lässt sich gerade nicht lesen. Die Steuerung schaltet in dieser Zeit nichts: Der Entfeuchter bleibt, wie er ist.'
  if (g.urteil === 'unbekannt') return 'Der Wert am Gerät lässt sich gerade nicht lesen. Die Steuerung schreibt in dieser Zeit nichts.'
  if (g.urteil === 'geraetAus') return 'Das Gerät ist am Controller ausgeschaltet. Stell den Port auf Auto, dann kann die Steuerung ihn regeln. Den Modus ändert sie nie selbst.'
  if (g.urteil === 'ohneAutomatik') {
    const modus = g.modus ? ` (aktuell: ${g.modus})` : ''
    return `Der Port steht nicht auf Auto${modus}. Dann gibt es keine Grenze, die die Steuerung setzen könnte. Stell den Port am Controller auf Auto — dann hält die Steuerung ${ZIEL_TEXT[schalter]} beim Planwert (${planText(zeilen) || '–'}). Den Modus ändert sie nie selbst.`
  }
  if (g.urteil === 'keinAusloeser') return 'Am Port gibt es in Home Assistant keinen Wert für diese Grenze (Auslöser). Ohne ihn kann die Steuerung nichts einstellen.'
  if (g.urteil === 'keinPlan') return 'Der Trocknungsplan hat für heute keinen Wert für diese Grenze. Die Steuerung ändert so lange nichts.'
  if (g.urteil === 'passt') {
    return betrieb === 'regeln'
      ? `Am Gerät steht ${wert} — das ist der Planwert. Die Steuerung hält ihn dort.`
      : `Am Gerät steht ${wert} — das ist der Planwert. Im Modus „Nur anzeigen“ stellt die Steuerung nichts nach.`
  }
  return betrieb === 'regeln'
    ? `Am Gerät steht ${wert}, der Plan verlangt ${plan}. Die Steuerung stellt das Gerät jetzt auf ${plan} und prüft es alle 5 Minuten.`
    : `Am Gerät steht ${wert}, der Plan verlangt ${plan}. Im Modus „Nur anzeigen“ ändert die Steuerung nichts — sie zeigt nur, was sie tun würde.`
}

/** Der Satz zum Entfeuchter, den die Steuerung schaltet: Schwellen, Messwert und wohin sie gerade schaltet. */
export function schaltText(s: SteuerungSchalten, betrieb: Betrieb): string {
  const ein = s.einAb == null ? '–' : `${zahl(s.einAb)} %`
  const aus = s.ausUnter == null ? '–' : `${zahl(s.ausUnter)} %`
  const jetzt = s.feuchte == null ? '' : ` Jetzt: ${zahl(s.feuchte)} %.`
  const wann = s.zuletztUtc ? ` Zuletzt geschaltet: ${new Date(s.zuletztUtc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })} Uhr (${s.zuletztAktion ?? '–'}).` : ''
  if (betrieb !== 'regeln') return `Im Modus „Nur anzeigen“ schaltet die Steuerung nichts. Mit „Regeln“ schaltet sie den Entfeuchter ein ab ${ein} und aus unter ${aus}.${jetzt}`
  const lage = s.zustand === 'wartet' ? ' Sie wartet noch die Mindestpause von 3 Minuten seit dem letzten Schaltvorgang ab.' : ''
  return `Die Steuerung schaltet den Entfeuchter ein ab ${ein} und aus unter ${aus} (Ziel und Untergrenze der Etappe).${jetzt}${wann}${lage}`
}

/** Was beim Einschalten von „Regeln“ für den Entfeuchter gilt: die Schwellen, wenn er eingebunden ist und Werte hat. */
export function wuerdeSchalten(geraete: SteuerungGeraet[], folgt: Partial<Folgt>) {
  const g = geraete.find((x) => x.rolle === 'entfeuchter')
  if (!g || !folgt.entfeuchter || !g.schalten || g.schalten.einAb == null || g.schalten.ausUnter == null) return null
  return { portName: g.portName ?? ROLLE.entfeuchter.titel, einAb: g.schalten.einAb, ausUnter: g.schalten.ausUnter }
}

/** Die Schritte eines laufenden Vorgangs in der Reihenfolge, in der der Fork sie abarbeitet. */
export type ArbeitPhase = 'speichern' | 'abgleichen' | 'pruefen'
export const ARBEIT_SCHRITTE: Array<{ phase: ArbeitPhase; text: string }> = [
  { phase: 'speichern', text: 'Haken gespeichert' },
  { phase: 'abgleichen', text: 'Mit dem Controller abgleichen' },
  { phase: 'pruefen', text: 'Wert am Gerät prüfen' },
]
export const ARBEIT_TITEL: Record<ArbeitPhase, string> = { speichern: 'wird gespeichert', abgleichen: 'gleicht ab', pruefen: 'prüft' }

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

export const betriebText = (betrieb: Betrieb) => (betrieb === 'regeln' ? 'Regeln' : 'Nur anzeigen')
