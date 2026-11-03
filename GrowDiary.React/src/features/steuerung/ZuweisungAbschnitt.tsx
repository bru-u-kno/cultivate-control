import { useCallback, useEffect, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card } from '../../components/v1'
import type { SteuerungZuweisung } from './steuerung-typen'
import './steuerung.css'

/**
 * Steuerungen Zelten zuweisen: je Steuerung die Zelte als Auswahl, ein Zelt je Steuerung. Erscheint nur, wenn es zwei oder mehr
 * Zelte gibt. Ändert die Gliederung der Übersicht — Geräte, Helfer und Automationen bleiben, wie sie sind. Das CO₂ gehört nicht
 * hierher: es läuft je Zelt und trägt sein Zelt selbst.
 */
export function ZuweisungAbschnitt({ zelteAnzahl, onGespeichert }: { zelteAnzahl: number; onGespeichert: () => void }) {
  const [offen, setOffen] = useState(false)
  const [stand, setStand] = useState<SteuerungZuweisung | null>(null)
  const [wahl, setWahl] = useState<Record<string, number>>({})
  const [arbeitet, setArbeitet] = useState(false)
  const [meldung, setMeldung] = useState<{ ton: 'ok' | 'fehler'; text: string } | null>(null)

  const laden = useCallback(async () => {
    try {
      const geladen = await apiFetch<SteuerungZuweisung>('/api/steuerung/zuweisung')
      setStand(geladen)
      setWahl(Object.fromEntries(geladen.eintraege.filter((e) => e.zeltId != null).map((e) => [e.kennung, e.zeltId as number])))
    } catch (caught) {
      setMeldung({ ton: 'fehler', text: formatApiError(caught, 'Die Zuweisung konnte nicht gelesen werden.') })
    }
  }, [])

  useEffect(() => { if (offen && !stand) void laden() }, [offen, stand, laden])

  if (zelteAnzahl < 2) return null

  const geaendert = stand?.eintraege.filter((e) => wahl[e.kennung] != null && wahl[e.kennung] !== e.zeltId) ?? []

  const speichern = async () => {
    setArbeitet(true)
    setMeldung(null)
    try {
      const neu = await apiFetch<SteuerungZuweisung>('/api/steuerung/zuweisung', {
        method: 'PUT',
        body: JSON.stringify({ zuordnungen: Object.fromEntries(geaendert.map((e) => [e.kennung, wahl[e.kennung]])) }),
      })
      setStand(neu)
      setMeldung({ ton: 'ok', text: 'Gespeichert. Die Übersicht ist neu gegliedert.' })
      onGespeichert()
    } catch (caught) {
      setMeldung({ ton: 'fehler', text: formatApiError(caught, 'Das Speichern hat nicht geklappt.') })
    } finally {
      setArbeitet(false)
    }
  }

  return (
    <div data-audit="steuerung-zuweisung" style={{ marginTop: 14 }}>
      <V1Button variant="ghost" onClick={() => setOffen(!offen)} aria-expanded={offen}>
        {offen ? 'Zuweisung schließen' : 'Steuerungen Zelten zuweisen'}
      </V1Button>
      {offen && (
        <div style={{ marginTop: 10 }}>
          {meldung && <V1Alert tone={meldung.ton === 'ok' ? 'ok' : 'critical'} message={meldung.text} />}
          {stand && stand.eintraege.length === 0 && <p className="st-hinweis">Keine Steuerung gewählt, die sich einem Zelt zuweisen ließe.</p>}
          {stand?.eintraege.map((e) => (
            <V1Card key={e.kennung}>
              <div className="st-feldzeile">
                <span className="st-etikett">{e.titel}<small>Zu welchem Zelt gehört sie?</small></span>
              </div>
              <div className="st-wechsel" role="group" aria-label={`Zelt für ${e.titel}`}>
                {stand.zelte.map((z) => (
                  <button
                    key={z.id}
                    type="button"
                    className="st-chip"
                    aria-current={wahl[e.kennung] === z.id}
                    onClick={() => setWahl({ ...wahl, [e.kennung]: z.id })}
                  >
                    <i className={wahl[e.kennung] === z.id ? 'is-an' : undefined} aria-hidden="true" />
                    {z.name}
                  </button>
                ))}
              </div>
            </V1Card>
          ))}
          {stand && stand.eintraege.length > 0 && (
            <>
              <p className="st-hinweis">Das CO₂ gehört nicht hierher: Jedes Zelt hat seine eigene CO₂-Steuerung. Geräte, Helfer und Automationen bleiben, wie sie sind — die Zuweisung gliedert die Übersicht.</p>
              <div className="st-knopfleiste">
                <V1Button variant="primary" onClick={() => void speichern()} disabled={arbeitet || geaendert.length === 0}>
                  {arbeitet ? 'Speichert …' : 'Speichern'}
                </V1Button>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  )
}
