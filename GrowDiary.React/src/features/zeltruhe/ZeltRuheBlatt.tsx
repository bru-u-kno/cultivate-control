import { useState } from 'react'
import { formatApiError } from '../../api'
import { V1Alert, V1Button, V1Switch } from '../../components/v1'
import { V1Sheet } from '../../components/V1Sheet'
import { schalteRuhend, type ZeltRuheGeraet } from './zeltruhe-api'
import { starteReinigung, type ReinigungNachGrow } from '../reinigung/reinigung-api'
import './zeltruhe.css'

/**
 * Fork AI (cc.277, „Zelt ruhend"): Das Blatt nach „Speichern & Grow abschließen".
 *
 * Fragt, ob die Geräte im leeren Zelt ausgeschaltet werden sollen. Es listet nur, was im Ruhemodus ausgehen würde
 * UND gerade läuft; läuft nichts davon, erscheint es gar nicht (siehe `laufendeAuswahl`). Die Frage ist ein Vorschlag:
 * „Alles laufen lassen" ändert nichts.
 */
export function ZeltRuheBlatt({ geraete, reinigung, onFertig, onReinigung }: {
  geraete: ZeltRuheGeraet[]
  /** Fork AI: die empfohlene Reinigung des leeren Systems — als Angebot unter den Geräten. */
  reinigung?: ReinigungNachGrow | null
  onFertig: () => void
  /** Die Reinigung wurde gestartet: wohin es weitergeht (statt in das Archiv). */
  onReinigung?: (r: ReinigungNachGrow) => void
}) {
  const [wahl, setWahl] = useState<Record<string, boolean>>(() => Object.fromEntries(geraete.map((g) => [g.kennung, true])))
  const [arbeitet, setArbeitet] = useState(false)
  const [fehler, setFehler] = useState<string | null>(null)
  const gewaehlt = geraete.filter((g) => wahl[g.kennung]).map((g) => g.kennung)
  const [reinigungLaeuft, setReinigungLaeuft] = useState(false)

  const reinigungStarten = async () => {
    if (!reinigung) return
    setReinigungLaeuft(true)
    setFehler(null)
    try {
      await starteReinigung(reinigung)
      onReinigung?.(reinigung)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Die Reinigung ließ sich nicht starten.'))
    } finally {
      setReinigungLaeuft(false)
    }
  }

  const ruhend = async () => {
    setArbeitet(true)
    setFehler(null)
    try {
      const antwort = await schalteRuhend(gewaehlt)
      if (antwort.ok) { onFertig(); return }
      setFehler(antwort.zusammenfassung)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Das Zelt ließ sich nicht ruhend schalten.'))
    } finally {
      setArbeitet(false)
    }
  }

  return (
    <V1Sheet
      open
      onClose={onFertig}
      title="Ernte gespeichert"
      subtitle="Der Grow ist abgeschlossen. Sollen die Geräte im leeren Zelt ausgeschaltet werden?"
      label="Zelt ruhend schalten?"
      footer={(
        <>
          <V1Button variant="primary" className="zr-blatt-knopf" disabled={arbeitet || gewaehlt.length === 0} onClick={() => void ruhend()} audit="zelt-ruhend-ja">
            {arbeitet ? 'Schaltet…' : 'Zelt ruhend schalten'}
          </V1Button>
          <button type="button" className="forkai-sheet-cancel" onClick={onFertig} data-audit="zelt-ruhend-nein">Alles laufen lassen</button>
        </>
      )}
    >
      <div className="zr-liste" data-audit="zelt-ruhe-blatt">
        {geraete.map((g) => (
          <V1Switch
            key={g.kennung}
            label={g.titel}
            checked={wahl[g.kennung] ?? false}
            onChange={(an) => setWahl((aktuell) => ({ ...aktuell, [g.kennung]: an }))}
            hint={`jetzt: ${g.jetzt}`}
          />
        ))}
        {reinigung && (
          <div className="zr-reinigung" data-audit="reinigung-im-blatt">
            <strong>Danach: Reinigung</strong>
            <small>{reinigung.meldung} Sie steht danach unter Aufgaben.</small>
            <V1Button variant="secondary" disabled={reinigungLaeuft} onClick={() => void reinigungStarten()} audit="reinigung-jetzt-starten">
              {reinigungLaeuft ? 'Startet…' : reinigung.aktiveInstanzId != null ? 'Reinigung öffnen' : 'Reinigung jetzt starten'}
            </V1Button>
          </div>
        )}
        <p className="zr-hinweis">Alles andere läuft weiter. Die Auswahl lässt sich unter Zelte &amp; Räume ändern; dort steht auch „Zelt wieder aktiv“.</p>
      </div>
      {fehler && <V1Alert tone="critical" message={fehler} />}
    </V1Sheet>
  )
}
