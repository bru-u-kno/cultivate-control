import { useEffect } from 'react'
import './kurzmeldung.css'

export type KurzmeldungTon = 'ok' | 'warn' | 'critical'

/**
 * Kurze Rückmeldung am unteren Rand, die nach einigen Sekunden von selbst verschwindet („Gespeichert“, „Fehlgeschlagen“).
 * Sie ersetzt keine Fehlermeldung an der Stelle des Fehlers, sondern sagt zusätzlich, dass ein Vorgang abgeschlossen ist.
 * Für Screenreader ein höflicher Statusbereich (`status`); wer einen Fehler an seiner Stelle sieht, bekommt ihn dort als eigenes Band.
 */
export function Kurzmeldung({ text, ton = 'ok', nr, onSchliessen, sekunden }: {
  text: string | null
  ton?: KurzmeldungTon
  /** Laufende Nummer der Meldung: eine gleichlautende zweite Meldung startet den Timer neu. */
  nr?: number
  onSchliessen: () => void
  sekunden?: number
}) {
  useEffect(() => {
    if (!text) return undefined
    const t = window.setTimeout(onSchliessen, (sekunden ?? (ton === 'critical' ? 8 : 4.5)) * 1000)
    return () => window.clearTimeout(t)
  }, [text, ton, nr, sekunden, onSchliessen])
  // Der Bereich steht immer im Baum, nur sein Inhalt wechselt: so liest ein Screenreader die Meldung zuverlässig vor.
  return (
    <div role="status" aria-live="polite">
      {text && <div className={`v1-kurzmeldung tone-${ton}`} data-audit="kurzmeldung">{text}</div>}
    </div>
  )
}
