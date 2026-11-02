import type { KopfWert } from './zelt-kopf'
import type { buildScore } from './live-model'

/**
 * Der Kopf eines Zelts auf der Live-Seite — zugeklappt wie aufgeklappt gleich.
 *
 * Von links: Pfeil (wie bei allen Klappen der Seite), Score-Ring, Zeltname,
 * Zustandswort. Darunter die Phase; zugeklappt zusätzlich die Kernwerte in der
 * Farbe ihres Urteils (grün im Ziel, gelb daneben, rot an der Grenze) und —
 * wenn das Zelt Hydro-Werte meldet — eine zweite Zeile Nährlösung.
 *
 * Der Score steht NUR hier. Der aufgeklappte Inhalt trägt keinen zweiten Ring.
 */
export function ZeltKopf({ name, offen, onUmschalten, inhaltId, score, unterzeile, klima, hydro, gesperrt = false }: {
  name: string
  offen: boolean
  onUmschalten: () => void
  /** Kennung des aufgeklappten Inhalts, für aria-controls. */
  inhaltId: string
  score: ReturnType<typeof buildScore>
  unterzeile: string | null
  klima: KopfWert[]
  hydro: KopfWert[]
  /**
   * Solange im Zelt „Anpassen“ läuft, lässt sich das Zelt nicht zuklappen: ein
   * zugeklapptes Zelt wirft seinen ungespeicherten Entwurf weg, und das stumm.
   */
  gesperrt?: boolean
}) {
  const beschreibungId = `${inhaltId}-beschreibung`
  // Der Knopfname bleibt kurz; Phase und Kernwerte kommen als Beschreibung dazu,
  // sonst läse ein Screenreader den ganzen Kopf als einen langen Namen vor.
  const scoreText = score.value == null ? score.label : `Score ${score.value}, ${score.label}`
  const knopfName = gesperrt ? `${name}, ${scoreText}, gesperrt, solange Anpassen läuft` : `${name}, ${scoreText}`
  const beschreibung = [
    unterzeile,
    ...(!offen ? [...klima, ...hydro].map((wert) => `${wert.label} ${wert.wert}${wert.einheit ? ` ${wert.einheit}` : ''}`) : []),
  ].filter(Boolean).join(', ')
  return (
    <h2 className="ls-zb-ueberschrift">
      <button
        type="button"
        className={`ls-zb-kopf is-${score.tone}${gesperrt ? ' is-gesperrt' : ''}`}
        aria-expanded={offen}
        aria-controls={offen ? inhaltId : undefined}
        aria-disabled={gesperrt || undefined}
        aria-label={knopfName}
        aria-describedby={beschreibung ? beschreibungId : undefined}
        onClick={gesperrt ? undefined : onUmschalten}
        data-audit="zelt-kopf"
      >
        <span className="ls-zb-zeile">
          <span className="ls-fold-pfeil" aria-hidden="true">{gesperrt ? <Schloss /> : offen ? '▾' : '▸'}</span>
          <ZeltRing value={score.value} tone={score.tone} />
          <strong>{name}</strong>
          <span className="ls-zb-chip" data-audit="zelt-zustand">{score.label}</span>
        </span>
        {gesperrt
          ? (
            <span className="ls-zb-sub ls-zb-sperre" data-audit="zelt-gesperrt">
              Gesperrt, solange „Anpassen“ läuft. Erst „Speichern“ — oder mit „Fertig“ verwerfen —, dann lässt sich das Zelt zuklappen.
            </span>
          )
          : unterzeile && <span className="ls-zb-sub">{unterzeile}</span>}
        {!offen && klima.length > 0 && <WerteZeile werte={klima} />}
        {!offen && hydro.length > 0 && <WerteZeile werte={hydro} haarlinie />}
        {beschreibung && <span id={beschreibungId} hidden>{beschreibung}</span>}
      </button>
    </h2>
  )
}

function Schloss() {
  return (
    <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
      <rect x="3" y="7" width="10" height="7" rx="1.5" />
      <path d="M5.2 7V5a2.8 2.8 0 0 1 5.6 0v2" />
    </svg>
  )
}

function WerteZeile({ werte, haarlinie }: { werte: KopfWert[]; haarlinie?: boolean }) {
  return (
    <span className={haarlinie ? 'ls-zb-werte ls-zb-hydro' : 'ls-zb-werte'}>
      {werte.map((wert) => (
        <span key={wert.key} data-kopfwert={wert.key}>
          <small>{wert.label}</small>
          <b className={wert.ton ? `is-${wert.ton}` : undefined}>
            {wert.wert}{wert.einheit && <u>{' '}{wert.einheit}</u>}
          </b>
        </span>
      ))}
    </span>
  )
}

/**
 * Der kleine Score-Ring. Ohne Wert („Nicht bewertet", „Einrichten") bleibt er
 * leer und zeigt einen Strich statt einer Null — nichts gemessen ist keine
 * schlechte Note.
 */
function ZeltRing({ value, tone }: { value: number | null; tone: 'ok' | 'warn' | 'critical' | 'neutral' }) {
  const anteil = value == null ? 0 : Math.max(0, Math.min(100, value))
  return (
    <span
      className="ls-zb-ringhuelle"
      role="img"
      aria-label={value == null ? 'Nicht bewertet' : `Score ${value} von 100`}
      data-audit="zelt-score"
    >
      <span className={`ls-zb-ring is-${tone}`} style={{ ['--p' as string]: anteil }} />
      <b>{value == null ? '–' : value}</b>
    </span>
  )
}
