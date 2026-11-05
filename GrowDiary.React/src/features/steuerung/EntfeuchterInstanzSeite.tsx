import { useState } from 'react'
import { V1Alert, V1Button, V1Card, V1LinkButton, V1Page, V1Section, V1Skeleton, V1Tabs } from '../../components/v1'
import { rollenPfad } from '../geraete/rollenPfad'
import { abweichungsText } from './entfeuchter-abweichungen'
import { BestandAbschnitt, BestandHinweis } from './Bestand'
import { useBestand } from './bestand-laden'
import { GeraeteZeile } from './EntfeuchtungBausteine'
import { EinstellungenTab } from './EntfeuchtungEinstellungen'
import { UeberblickTab } from './EntfeuchtungUeberblick'
import { ENTFEUCHTUNG_REITER } from './entfeuchtung-modell'
import type { EntfeuchtungReiter } from './entfeuchtung-modell'
import { useFehlerZeigen } from './fehler-reiter'
import { HaStummHinweis } from './HaStummHinweis'
import { InstanzBlock, InstanzLoeschen } from './InstanzBlock'
import { useInstanzen } from './instanzen'
import { Klappkachel } from './Klappkachel'
import { SteuerungWechsel } from './SteuerungWechsel'
import { useEntfeuchterHaupt } from './useEntfeuchterHaupt'
import { useEntfeuchterZusatz } from './useEntfeuchterZusatz'
import { useZielwerteKurz } from './useZielwerteKurz'
import type { SteuerungModul } from './steuerung-typen'
import './steuerung.css'

/**
 * Ein weiterer Entfeuchter (`entfeuchter-i{n}`): dieselben Reiter wie die Seite „Entfeuchtung“, aber nur für diesen einen
 * Entfeuchter. Sein Zusatz-Entfeuchter (`entfeuchter-zusatz-i{n}`, gleiche Nummer, höchstens einer) steht mit auf dieser Seite —
 * er hat keine eigene. Unter den Reitern stehen Name, Zelt und Löschen der Instanz und, falls vorhanden, des Zusatzes.
 */
export default function EntfeuchterInstanzSeite({ module, aktiv, modul, onWechsel, onInstanzGeaendert, onInstanzGeloescht }: {
  module: SteuerungModul[]
  aktiv: string
  modul: string
  onWechsel: (kennung: string) => void
  onInstanzGeaendert: () => void
  onInstanzGeloescht: () => void
}) {
  const [reiter, setReiter] = useState<EntfeuchtungReiter>('ueberblick')
  const fehlerZeigen = useFehlerZeigen(reiter, setReiter, ENTFEUCHTUNG_REITER)
  const h = useEntfeuchterHaupt(modul)
  const zielwerte = useZielwerteKurz()
  const { instanzen, neuLaden } = useInstanzen()
  const { bestand, neuLaden: bestandNeuLaden } = useBestand(modul)
  const [loeschenOffen, setLoeschenOffen] = useState(false)
  const [zusatzLoeschenOffen, setZusatzLoeschenOffen] = useState(false)

  const instanz = instanzen?.find((i) => i.modul === modul)
  const zusatzModul = modul.replace('entfeuchter-', 'entfeuchter-zusatz-')
  const zusatzInstanz = instanzen?.find((i) => i.modul === zusatzModul)
  const z = useEntfeuchterZusatz(zusatzInstanz != null, zusatzModul)
  const { bestand: zusatzBestand, neuLaden: zusatzBestandNeuLaden } = useBestand(zusatzInstanz ? zusatzModul : 'entfeuchter-zusatz')
  const zusatzModell = zusatzInstanz && z.seite ? z : null
  const zusatzTitel = zusatzInstanz?.name ?? 'Zusatz-Entfeuchter'
  const titel = instanz?.name ?? module.find((m) => m.kennung === modul)?.titel ?? 'Entfeuchter'
  // In der Übersicht steht der erste Entfeuchter oft als „Entfeuchtung“ (mit dem Zusatz zusammengefasst).
  const ersterTitel = module.find((m) => m.kennung === 'entfeuchter' || m.kennung === 'entfeuchtung')?.titel ?? null
  const vorKopf = <SteuerungWechsel module={module} aktiv={aktiv} onWechsel={onWechsel} />

  if (loeschenOffen) {
    return (
      <InstanzLoeschen
        art="entfeuchter" modul={modul} titel={titel} ersterTitel={ersterTitel} zusatzTitel={zusatzInstanz?.name ?? null}
        onAbbrechen={() => setLoeschenOffen(false)} onGeloescht={onInstanzGeloescht}
      />
    )
  }
  if (zusatzLoeschenOffen && zusatzInstanz) {
    return (
      <InstanzLoeschen
        art="entfeuchter-zusatz" modul={zusatzModul} titel={zusatzInstanz.name} ersterTitel={null} hauptTitel={titel}
        onAbbrechen={() => setZusatzLoeschenOffen(false)}
        onGeloescht={() => { setZusatzLoeschenOffen(false); void neuLaden(); onInstanzGeaendert() }}
      />
    )
  }
  if (h.laedt && !h.seite) {
    return <V1Page eyebrow="Betrieb" title={titel} vorKopf={vorKopf}><V1Skeleton rows={4} tiles={1} label="Wird geladen" /></V1Page>
  }
  if (!h.seite || !h.entwurf || !h.anzeige) {
    return (
      <V1Page eyebrow="Betrieb" title={titel} vorKopf={vorKopf}>
        {h.fehler && <V1Alert tone="critical" message={h.fehler} />}
      </V1Page>
    )
  }

  const hl = h.seite.live
  const abweichungen = h.seite.abweichungen ?? []
  const geaendert = h.geaendert || (zusatzModell?.geaendert ?? false)
  const arbeitet = h.arbeitet || (zusatzModell?.arbeitet ?? false)
  const speichern = async () => {
    let ok = true
    if (h.geaendert) ok = (await h.speichern()) && ok
    if (zusatzModell?.geaendert) ok = (await zusatzModell.speichern()) && ok
    // Beide lesen dieselben Helfer (Höchsttemperatur): danach den anderen Stand frisch holen.
    await Promise.all([h.auffrischen(), zusatzModell ? zusatzModell.auffrischen() : Promise.resolve()])
    if (!ok) fehlerZeigen()
  }
  const zl = zusatzModell?.seite?.live ?? null
  const fehler = h.fehler ?? zusatzModell?.fehler ?? null
  const meldung = h.meldung ?? zusatzModell?.meldung ?? null

  return (
    <V1Page
      eyebrow="Betrieb"
      title={titel}
      subtitle={zusatzModell ? `${titel} und ${zusatzTitel} arbeiten zusammen — geregelt in Home Assistant` : `${titel} — geregelt in Home Assistant`}
      vorKopf={vorKopf}
      action={geaendert ? <V1Button variant="primary" onClick={() => void speichern()} disabled={arbeitet}>{arbeitet ? 'Speichert …' : 'Speichern'}</V1Button> : undefined}
    >
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {meldung && <V1Alert tone={meldung === 'Gespeichert.' ? 'ok' : 'warn'} message={meldung} />}
      <HaStummHinweis haErreichbar={hl.haErreichbar} eingerichtet={h.seite.geraeteZugeordnet > 0} />
      <BestandHinweis bestand={bestand} />
      {zusatzModell && <BestandHinweis bestand={zusatzBestand} />}
      {abweichungen.length > 0 && (
        <V1Alert tone="warn" title="Home Assistant regelt mit anderen Werten" message={`${abweichungsText(abweichungen)}. Geregelt wird nach den Werten in Home Assistant.`} />
      )}
      {hl.portOnline === false && <V1Alert tone="warn" title={`${titel} offline`} message="Der Port meldet sich nicht. Geschaltet wird erst, wenn er wieder da ist." />}
      {hl.automatikAn === false && <V1Alert tone="warn" title={`Automatik aus: ${titel}`} message={`Die Regelung ist angehalten. ${titel} bleibt, wie er gerade steht.`} />}

      {zl && zl.zusatzOnline === false && <V1Alert tone="warn" title={`${zusatzTitel} ist offline`} message="Die Steckdose meldet sich nicht. Geschaltet wird erst, wenn sie wieder da ist." />}
      {zl && zl.automatikAn === false && <V1Alert tone="warn" title={`Automatik aus: ${zusatzTitel}`} message={`Die Regelung ist angehalten. ${zusatzTitel} bleibt, wie er gerade steht.`} />}

      <V1Tabs items={ENTFEUCHTUNG_REITER} active={reiter} onChange={setReiter} label="Bereich" insBild />

      {reiter === 'ueberblick' && <UeberblickTab h={h} z={zusatzModell} haupt={titel} zusatz={zusatzTitel} zielwerte={zielwerte} onSpringen={() => setReiter('einstellungen')} />}
      {reiter === 'einstellungen' && <EinstellungenTab h={h} z={zusatzModell} haupt={titel} zusatz={zusatzTitel} zielwerte={zielwerte} />}
      {reiter === 'einrichtung' && (
        <>
          <Klappkachel titel="Zuordnung" zusammenfassung={`${h.seite.geraeteZugeordnet} / ${h.seite.geraeteGesamt}`}>
            <V1Card>
              <GeraeteZeile zugeordnet={h.seite.geraeteZugeordnet} gesamt={h.seite.geraeteGesamt} pfad={rollenPfad(modul)} label={titel} />
            </V1Card>
          </Klappkachel>
          {zusatzModell && (
            <Klappkachel titel={`Zuordnung · ${zusatzTitel}`} zusammenfassung={`${zusatzModell.seite?.geraeteZugeordnet ?? 0} / ${zusatzModell.seite?.geraeteGesamt ?? 0}`}>
              <V1Card>
                <GeraeteZeile zugeordnet={zusatzModell.seite?.geraeteZugeordnet ?? 0} gesamt={zusatzModell.seite?.geraeteGesamt ?? 0} pfad={rollenPfad(zusatzModul)} label={zusatzTitel} />
              </V1Card>
            </Klappkachel>
          )}
          <BestandAbschnitt modul={modul} schaltet="den Entfeuchter" bestand={bestand} neuLaden={bestandNeuLaden} />
          {zusatzModell && <BestandAbschnitt modul={zusatzModul} schaltet="den Zusatz-Entfeuchter" bestand={zusatzBestand} neuLaden={zusatzBestandNeuLaden} />}
        </>
      )}

      {instanz && !zusatzInstanz && (
        <V1Section title="Zusatz-Entfeuchter">
          <V1Card>
            <p className="st-hinweis" style={{ margin: 0 }}>Zu „{titel}“ gibt es noch keinen Zusatz-Entfeuchter. Ein Zusatz hilft ihm mit, wenn er allein nicht reicht, und gehört immer zu genau diesem Entfeuchter.</p>
          </V1Card>
          <div className="st-knopfleiste"><V1LinkButton to={`/steuerung/entfeuchter-zusatz-neu?haupt=${modul}`} variant="primary">+ Zusatz-Entfeuchter dazu</V1LinkButton></div>
        </V1Section>
      )}
      {zusatzInstanz && <InstanzBlock art="entfeuchter-zusatz" instanz={zusatzInstanz} neuLaden={neuLaden} onGeaendert={onInstanzGeaendert} onLoeschen={() => setZusatzLoeschenOffen(true)} />}
      <InstanzBlock art="entfeuchter" instanz={instanz} neuLaden={neuLaden} onGeaendert={onInstanzGeaendert} onLoeschen={() => setLoeschenOffen(true)} />
    </V1Page>
  )
}
