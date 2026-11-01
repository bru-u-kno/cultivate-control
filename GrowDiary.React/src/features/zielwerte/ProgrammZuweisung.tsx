import { useCallback, useEffect, useMemo, useState } from 'react'
import { apiFetch, formatApiError } from '../../api'
import { V1Alert, V1Button, V1Skeleton } from '../../components/v1'
import { GiltFuer } from './GiltFuer'
import { brauchenEntscheidung, ergebnisText, growZeile, type ZuweisungErgebnis, type ZuweisungGrow } from './plan-zuweisung'

type Meldung = { text: string; ton: 'ok' | 'warn' | 'critical' }

/**
 * Plan › Düngeplan › „Gilt für diese Grows“ (A-030): das gewählte Programm Grows zuweisen.
 *
 * Der Düngeplan gehört dem Grow, nicht dem Zelt. Das hier ist eine zweite Tür zu demselben Wechsel wie im
 * Grow-Formular — geplante Grows bekommen das Programm eingetragen, Grows mit Plan wechseln über den Plan.
 * Berührt der Wechsel eigene Änderungen am Plan, fragt die Liste vorher, ob sie mitgehen oder entfallen.
 */
export function ProgrammZuweisung({ programmId, programmName }: { programmId: string; programmName: string }) {
  const [grows, setGrows] = useState<ZuweisungGrow[] | null>(null)
  const [gewaehlt, setGewaehlt] = useState<ReadonlySet<number>>(new Set())
  const [frage, setFrage] = useState(false)
  const [arbeitet, setArbeitet] = useState(false)
  const [meldung, setMeldung] = useState<Meldung | null>(null)

  // Nach dem Zuweisen: die Liste neu holen. Schlägt das fehl, bleibt der Satz zum Zuweisen stehen (es ist ja gespeichert).
  const neuHolen = useCallback(async () => {
    try {
      setGrows(await apiFetch<ZuweisungGrow[]>(`/api/programme/${encodeURIComponent(programmId)}/zuweisung`))
      setGewaehlt(new Set())
    } catch {
      // Die alte Liste bleibt stehen; ein erneutes Öffnen der Seite holt sie.
    }
  }, [programmId])

  useEffect(() => {
    let aktiv = true
    apiFetch<ZuweisungGrow[]>(`/api/programme/${encodeURIComponent(programmId)}/zuweisung`)
      .then((liste) => { if (aktiv) setGrows(liste) })
      .catch((caught) => {
        if (!aktiv) return
        setGrows([])
        setMeldung({ ton: 'critical', text: formatApiError(caught, 'Die Grows konnten nicht geladen werden.') })
      })
    return () => { aktiv = false }
  }, [programmId])

  const zeilen = useMemo(
    () => (grows ?? []).map((g) => growZeile(g, g.zugewiesen || gewaehlt.has(g.growId))),
    [grows, gewaehlt],
  )

  if (grows === null) return <V1Skeleton rows={3} label="Lade Grows" />

  const entscheidung = brauchenEntscheidung(grows, gewaehlt)

  function umschalten(id: number) {
    setFrage(false)
    setMeldung(null)
    setGewaehlt((alt) => {
      const neu = new Set(alt)
      if (neu.has(id)) neu.delete(id)
      else neu.add(id)
      return neu
    })
  }

  async function zuweisen(behalten: boolean) {
    setArbeitet(true)
    setFrage(false)
    setMeldung(null)
    try {
      const ergebnisse = await apiFetch<ZuweisungErgebnis[]>(`/api/programme/${encodeURIComponent(programmId)}/zuweisung`, {
        method: 'POST',
        body: JSON.stringify({ growIds: [...gewaehlt], aenderungenBehalten: behalten }),
      })
      setMeldung(ergebnisText(ergebnisse))
      await neuHolen()
    } catch (caught) {
      setMeldung({ ton: 'critical', text: formatApiError(caught, 'Zuweisen hat nicht geklappt.') })
      // Ein Teil kann schon umgestellt sein — die Anzeige nicht veralten lassen.
      await neuHolen()
    } finally {
      setArbeitet(false)
    }
  }

  function speichern() {
    // Eigene Änderungen am Plan eines laufenden Grows gehen nie ungefragt verloren oder mit.
    if (entscheidung.length > 0) setFrage(true)
    else void zuweisen(true)
  }

  return (
    <div data-audit="programm-zuweisung">
      <GiltFuer
        titel="Gilt für diese Grows"
        merken="plan-gilt-duenge"
        audit="plan-gilt-duenge"
        zeilen={zeilen}
        onUmschalten={umschalten}
        leer="Es gibt noch keinen laufenden oder geplanten Grow."
        hinweis={`Ein Grow hat einen Düngeplan. Wählst du „${programmName}“ hier, löst es den bisherigen ab. Bei einem laufenden Grow wechseln die Wochenwerte; was schon abgeschlossen ist, bleibt unverändert.`}
      >
        {/* Ohne Grow gibt es nichts zuzuweisen — dann auch keinen Knopf. */}
        {grows.length > 0 && (
          <V1Button variant="primary" disabled={gewaehlt.size === 0 || arbeitet} onClick={speichern} audit="plan-zuweisen">
            Zuweisung speichern
          </V1Button>
        )}
      </GiltFuer>
      {frage && (
        <div className="gf-frage" data-audit="plan-zuweisen-frage">
          <V1Alert
            tone="warn"
            title="Eigene Änderungen am Plan"
            message={`${entscheidung.map((g) => `${g.name} (${g.eigeneAenderungen})`).join(', ')}: Die Änderungen können in die gleichnamigen Wochen des neuen Programms mitgehen. Wochen, die es dort nicht gibt, entfallen mit ihren Änderungen.`}
          />
          <div className="gf-knoepfe">
            <V1Button variant="primary" onClick={() => void zuweisen(true)}>Änderungen mitnehmen</V1Button>
            <V1Button variant="secondary" onClick={() => void zuweisen(false)}>Änderungen verwerfen</V1Button>
            <V1Button variant="ghost" onClick={() => setFrage(false)}>Abbrechen</V1Button>
          </div>
        </div>
      )}
      {meldung && <V1Alert tone={meldung.ton} message={meldung.text} />}
    </div>
  )
}
