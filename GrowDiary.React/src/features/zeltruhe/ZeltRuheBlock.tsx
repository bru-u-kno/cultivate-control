import { useCallback, useEffect, useState } from 'react'
import { formatApiError } from '../../api'
import { V1Alert, V1Switch } from '../../components/v1'
import { ladeZeltRuhe, schalteAktiv, schalteRuhend, type ZeltRuheErgebnis, type ZeltRuheStand } from './zeltruhe-api'
import './zeltruhe.css'

/**
 * Fork AI (cc.277, „Zelt ruhend"): Der Block „Betrieb" in der Zelt-Karte.
 *
 * Steht nur an dem Zelt, zu dem die Steuerung gehört (das Zelt des neuesten Grows). Zeigt In Betrieb oder Ruhend, die Geräte
 * mit Haken „im Ruhemodus ausschalten" und den Knopf zum Umschalten. Der Haken gilt für das nächste Ruhendschalten;
 * gespeichert wird er mit dem Knopf (Backend: `auswahl`).
 */
export function ZeltRuheBlock({ tentId, growName, growAbgeschlossen }: { tentId: number; growName?: string | null; growAbgeschlossen?: string | null }) {
  const [stand, setStand] = useState<ZeltRuheStand | null>(null)
  const [wahl, setWahl] = useState<Record<string, boolean>>({})
  const [arbeitet, setArbeitet] = useState(false)
  const [fehler, setFehler] = useState<string | null>(null)
  const [ergebnis, setErgebnis] = useState<ZeltRuheErgebnis | null>(null)

  const uebernehmen = useCallback((neu: ZeltRuheStand) => {
    setStand(neu)
    setWahl(Object.fromEntries(neu.geraete.filter((g) => g.vorhanden).map((g) => [g.kennung, g.ausschalten])))
  }, [])

  useEffect(() => {
    let abgebrochen = false
    ladeZeltRuhe().then((s) => { if (!abgebrochen) uebernehmen(s) }).catch(() => { if (!abgebrochen) setStand(null) })
    return () => { abgebrochen = true }
  }, [tentId, uebernehmen])

  // Nicht das Zelt der Steuerung, ein Trockenzelt, nichts zu schalten (Rollen fehlen) oder Home Assistant stumm: kein Block.
  if (!stand || stand.zeltId !== tentId || stand.zeltTyp === 'Drying' || !stand.geraete.some((g) => g.vorhanden)) return null

  const vorhanden = stand.geraete.filter((g) => g.vorhanden)
  const gewaehlt = vorhanden.filter((g) => wahl[g.kennung]).map((g) => g.kennung)
  // Ruhend, aber noch nicht alles ausgeschaltet, was in der Auswahl steht (ein Gerät hat nicht geantwortet): lässt sich nachholen.
  const offen = vorhanden.filter((g) => g.ausschalten && !g.gemerkt)

  const ausfuehren = async (aktion: () => Promise<ZeltRuheErgebnis>) => {
    setArbeitet(true)
    setFehler(null)
    try {
      const antwort = await aktion()
      setErgebnis(antwort)
      uebernehmen(antwort.stand)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Das Zelt ließ sich nicht umschalten.'))
    } finally {
      setArbeitet(false)
    }
  }

  const seit = stand.seitUtc ? new Date(stand.seitUtc).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : null

  return (
    <div className="tn-group zr-block" data-audit="zelt-ruhe">
      <div className="tn-group-label">Betrieb</div>
      <div className="tn-row">
        <span>Status</span>
        <strong>{stand.ruhend ? `Ruhend${seit ? ` seit ${seit}` : ''}` : 'In Betrieb'}</strong>
      </div>
      {!stand.ruhend && growName && growAbgeschlossen && (
        <div className="tn-row"><span>Grow</span><strong>{growName} · abgeschlossen {growAbgeschlossen}</strong></div>
      )}

      {!stand.ruhend ? (
        <>
          <div className="tn-group-label zr-unterlabel">Im Ruhemodus ausschalten</div>
          <div className="zr-liste">
            {vorhanden.map((g) => (
              <V1Switch
                key={g.kennung}
                label={g.titel}
                checked={wahl[g.kennung] ?? false}
                onChange={(an) => setWahl((aktuell) => ({ ...aktuell, [g.kennung]: an }))}
                hint={`${wahl[g.kennung] ? 'wird ausgeschaltet' : 'läuft weiter'} · jetzt: ${g.jetzt}`}
              />
            ))}
          </div>
          <div className="co-actions zr-knoepfe">
            <button
              type="button"
              className="ls-btn is-small is-primary"
              disabled={arbeitet || gewaehlt.length === 0}
              onClick={() => void ausfuehren(() => schalteRuhend(gewaehlt))}
            >
              {arbeitet ? 'Schaltet…' : 'Zelt ruhend schalten'}
            </button>
          </div>
        </>
      ) : (
        <>
          <div className="tn-row"><span>Ausgeschaltet</span><strong>{vorhanden.filter((g) => g.gemerkt).map((g) => g.titel).join(', ') || '–'}</strong></div>
          <div className="tn-row"><span>Läuft weiter</span><strong>{vorhanden.filter((g) => !g.gemerkt && !g.ausschalten).map((g) => g.titel).join(', ') || '–'}</strong></div>
          {offen.length > 0 && (
            <div className="tn-row" data-audit="zelt-ruhe-offen"><span>Noch offen</span><strong>{offen.map((g) => g.titel).join(', ')}</strong></div>
          )}
          <div className="co-actions zr-knoepfe">
            <button type="button" className="ls-btn is-small is-primary" disabled={arbeitet} onClick={() => void ausfuehren(schalteAktiv)}>
              {arbeitet ? 'Schaltet…' : 'Zelt wieder aktiv'}
            </button>
            {offen.length > 0 && (
              <button type="button" className="ls-btn is-small" disabled={arbeitet} onClick={() => void ausfuehren(() => schalteRuhend())}>
                Rest ausschalten
              </button>
            )}
          </div>
          <p className="zr-hinweis">Beim Wecken stellt der CC genau das wieder an, was er ausgeschaltet hat.</p>
        </>
      )}

      {fehler && <V1Alert tone="critical" message={fehler} />}
      {ergebnis && (
        <div className="zr-ergebnis" role="status" data-audit="zelt-ruhe-ergebnis">
          <p>{ergebnis.zusammenfassung}</p>
          {ergebnis.schritte.some((s) => !s.ok) && (
            <ul>
              {ergebnis.schritte.filter((s) => !s.ok).map((s) => <li key={s.kennung}><strong>{s.titel}:</strong> {s.text}</li>)}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}
