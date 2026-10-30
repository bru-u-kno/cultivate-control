import { useEffect, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1Section, V1Skeleton, V1Switch } from '../../components/v1'
import { KlappBereich } from '../../components/Klapp'
import type { LichtWaechterEinstellungen, LichtWaechterStand } from './steuerung-typen'
import './steuerung.css'

const URL = '/api/steuerung/licht/waechter'

/** Eine Zahl mit Komma, wie sie auf dem Schirm steht. */
function prozent(wert: number | null): string {
  return wert == null ? '–' : `${wert.toLocaleString('de-DE', { maximumFractionDigits: 1 })}\u00a0%`
}

function uhrzeit(utc: string): string {
  return new Date(utc).toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' })
}

/**
 * Steuerung › Licht › Wächter.
 *
 * Der Zeitplan läuft im Controller; der Wächter prüft nur, ob das Ergebnis stimmt. Jede Änderung
 * wird sofort gespeichert — es gibt hier keinen eigenen Speichern-Knopf.
 */
export default function LichtWaechter() {
  const [stand, setStand] = useState<LichtWaechterStand | null>(null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    const laden = async () => {
      try {
        const geladen = await apiFetch<LichtWaechterStand>(URL, { signal: controller.signal })
        if (!controller.signal.aborted) { setStand(geladen); setFehler(null) }
      } catch (caught) {
        if (!controller.signal.aborted) setFehler(formatApiError(caught, 'Der Licht-Wächter konnte nicht geladen werden.'))
      }
    }
    void laden()
    const takt = window.setInterval(() => { void laden() }, 30000)
    return () => { controller.abort(); window.clearInterval(takt) }
  }, [])

  const speichern = async (neu: LichtWaechterEinstellungen) => {
    setArbeitet(true)
    try {
      setStand(await apiFetch<LichtWaechterStand>(URL, { method: 'PUT', body: JSON.stringify(neu) }))
      setFehler(null)
    } catch (e) {
      setFehler(formatApiError(e, 'Die Änderung konnte nicht gespeichert werden.'))
    } finally {
      setArbeitet(false)
    }
  }

  const pausieren = async (stunden: number) => {
    setArbeitet(true)
    try {
      setStand(await apiFetch<LichtWaechterStand>('/api/steuerung/licht/waechter/pause', { method: 'POST', body: JSON.stringify({ stunden }) }))
      setFehler(null)
    } catch (e) {
      setFehler(formatApiError(e, 'Die Änderung konnte nicht gespeichert werden.'))
    } finally {
      setArbeitet(false)
    }
  }

  if (!stand) return fehler ? <V1Alert tone="critical" message={fehler} /> : <V1Skeleton />

  const e = stand.einstellungen
  const korrigiert = e.modus === 'korrigieren'
  const kurzEinstellung = [
    e.aktiv ? 'AN' : 'AUS',
    korrigiert ? 'korrigiert + meldet' : 'nur melden',
    `hell ab ${e.hellAbProzent}\u00a0%`,
    stand.letzter ? `zuletzt ${stand.letzter.phase} ${stand.letzter.ergebnis === 'ok' ? 'ok' : stand.letzter.ergebnis}` : 'noch nicht geprüft',
  ].join(' · ')

  return (
    <V1Section title="Wächter">
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {!stand.planVorhanden && (
        <V1Alert tone="warn" message="Es gibt keinen aktiven Lichtplan im Zelt. Ohne ihn weiß der Wächter nicht, wann es hell sein soll, und prüft nichts." />
      )}
      {!stand.sensorZugeordnet && (
        <V1Alert tone="neutral" message="Es ist kein Lichtsensor zugeordnet (Geräte › Licht › „Lichtsensor · Helligkeit“). Ohne ihn prüft der Wächter nur Betriebsart und Zeiten." />
      )}

      <KlappBereich titel="Licht-Wächter" kurz={kurzEinstellung} offen merken="licht-waechter-einstellungen">
        <V1Card>
          <p className="st-hinweis">
            Prüft 5 Minuten nach jeder Ein- und Ausschaltzeit mit dem Lichtsensor, ob das Licht wirklich so ist wie der Lichtplan
            {stand.plan ? ` (${stand.plan})` : ''}. Jetzt: Sensor {prozent(stand.sensor)}.
          </p>
          <V1Switch label="Wächter" checked={e.aktiv} onChange={(aktiv) => void speichern({ ...e, aktiv })} />

          <div className="st-feldzeile is-gestapelt">
            <span className="st-etikett">
              Wenn etwas nicht stimmt
              <small>Bei falscher Betriebsart oder falschen Zeiten setzt der Fork den Zeitplan selbst zurück (höchstens 2 Versuche je Lichtphase). Alles andere meldet er nur.</small>
            </span>
            <span className="st-eingaben">
              <V1Button variant={korrigiert ? 'primary' : 'ghost'} disabled={arbeitet} onClick={() => void speichern({ ...e, modus: 'korrigieren' })}>Korrigieren + melden</V1Button>
              <V1Button variant={!korrigiert ? 'primary' : 'ghost'} disabled={arbeitet} onClick={() => void speichern({ ...e, modus: 'melden' })}>Nur melden</V1Button>
            </span>
          </div>

          <div className="st-feldzeile">
            <span className="st-etikett">
              Hell ab
              <small>Der Lichtsensor gilt ab diesem Wert als hell, darunter als dunkel (Voreinstellung 80&nbsp;%).</small>
            </span>
            <span className="st-eingaben">
              <V1Button variant="ghost" aria-label="Hell ab verringern" disabled={arbeitet || e.hellAbProzent <= 5} onClick={() => void speichern({ ...e, hellAbProzent: e.hellAbProzent - 5 })}>−</V1Button>
              <span className="st-wert">{e.hellAbProzent}&nbsp;%</span>
              <V1Button variant="ghost" aria-label="Hell ab erhöhen" disabled={arbeitet || e.hellAbProzent >= 100} onClick={() => void speichern({ ...e, hellAbProzent: e.hellAbProzent + 5 })}>+</V1Button>
            </span>
          </div>

          <div className="st-feldzeile is-gestapelt">
            <span className="st-etikett">
              Pause
              <small>
                Licht bewusst aus, z.&nbsp;B. bei Arbeit im Zelt: Der Wächter schweigt bis zum Ablauf. „3 Tage“ reicht für die Zeit bis zur Ernte; mit „Pause beenden“ geht es früher weiter.
                {stand.pausiert && e.pauseBisUtc ? ` Pausiert bis ${uhrzeit(e.pauseBisUtc)} Uhr (${new Date(e.pauseBisUtc).toLocaleDateString('de-DE')}).` : ''}
              </small>
            </span>
            <span className="st-eingaben">
              <V1Button variant="ghost" disabled={arbeitet} onClick={() => void pausieren(1)}>1 h</V1Button>
              <V1Button variant="ghost" disabled={arbeitet} onClick={() => void pausieren(2)}>2 h</V1Button>
              <V1Button variant="ghost" disabled={arbeitet} onClick={() => void pausieren(72)}>3 Tage</V1Button>
              {stand.pausiert && <V1Button variant="primary" disabled={arbeitet} onClick={() => void pausieren(0)}>Pause beenden</V1Button>}
            </span>
          </div>

          {stand.letzter && (
            <p className="st-hinweis">
              Letzte Prüfung ({stand.letzter.hell ? 'Lichtphase' : 'Dunkelphase'} ab {stand.letzter.phase}, geprüft {uhrzeit(stand.letzter.geprueftUtc)} Uhr): {stand.letzter.text}
            </p>
          )}
        </V1Card>
      </KlappBereich>

      <KlappBereich titel="So prüft er" kurz="4 Schritte der Reihe nach" merken="licht-waechter-ablauf">
        <V1Card>
          <p className="st-hinweis">Fünf Minuten nach der geplanten Ein- oder Ausschaltzeit meldet der Lichtsensor „hell“ oder „dunkel“. Passt das nicht zum Lichtplan, geht der Wächter der Reihe nach vor:</p>
          <ol className="st-hinweis">
            <li>Controller erreichbar? Wenn nicht: nur melden, nichts schalten.</li>
            <li>Betriebsart wirklich „Zeitplan“? Wenn nicht: zurücksetzen.</li>
            <li>Ein- und Ausschaltzeit wie im Lichtplan? Wenn nicht: zurücksetzen. Leistungsstufe 0: nur melden.</li>
            <li>Nach dem Zurücksetzen prüft er nach 2 Minuten erneut, ob der Sensor es bestätigt. Stimmt trotzdem alles und es bleibt dunkel, meldet er: Lampe, Netzteil oder Sensor?</li>
          </ol>
        </V1Card>
      </KlappBereich>

      <KlappBereich titel="Push-Texte" kurz="2 Beispiele" merken="licht-waechter-push">
        <V1Card>
          <p className="st-hinweis">So kommen die Meldungen auf dem Handy an (Beispiele):</p>
          <ul className="st-hinweis">
            <li>
              <strong>Licht war aus – behoben</strong><br />
              Soll hell seit 05:00, Sensor 0,3&nbsp;%. Betriebsart stand auf „aus“ statt „Zeitplan“. Zeitplan wiederhergestellt, 05:07 Sensor 100&nbsp;%.
            </li>
            <li>
              <strong>Licht dunkel – bitte prüfen</strong><br />
              Soll hell seit 05:00, Sensor 0,3&nbsp;%. Zeitplan, Betriebsart und Zeiten stimmen, der Controller ist erreichbar. Das kann der Fork nicht beheben: Lampe, Netzteil oder Sensor?
            </li>
          </ul>
        </V1Card>
      </KlappBereich>

      <KlappBereich titel="Befunde und Reaktion" kurz="5 Befunde" merken="licht-waechter-befunde">
        <V1Card>
          <ul className="st-hinweis">
            <li><strong>Betriebsart oder Zeiten falsch:</strong> selbst korrigieren + Push (im Modus „Nur melden“ nur Push).</li>
            <li><strong>Betriebsart und Zeiten stimmen, Sensor bleibt dunkel:</strong> nur Push (Lampe, Netzteil oder Sensor).</li>
            <li><strong>Controller oder Cloud nicht erreichbar:</strong> nur Push, nichts schalten.</li>
            <li><strong>Lichtsensor liefert keinen Wert:</strong> Push „nicht prüfbar“, Betriebsart und Zeiten werden weiter geprüft.</li>
            <li><strong>Hell in der Dunkelphase:</strong> gleiche Prüfung, Push.</li>
          </ul>
        </V1Card>
      </KlappBereich>
    </V1Section>
  )
}
