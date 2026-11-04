import { useState } from 'react'
import { V1Alert, V1Button, V1Card, V1Page, V1Skeleton, V1Tabs } from '../../components/v1'
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
import { useZielwerteKurz } from './useZielwerteKurz'
import type { SteuerungModul } from './steuerung-typen'
import './steuerung.css'

/**
 * Ein weiterer Entfeuchter (`entfeuchter-i{n}`): dieselben Reiter wie die Seite „Entfeuchtung“, aber nur für diesen einen
 * Entfeuchter. Ein Zusatz-Entfeuchter gehört weiter allein zum ersten Entfeuchter und erscheint hier nicht. Unter den Reitern
 * stehen Name, Zelt und Löschen der Instanz.
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

  const instanz = instanzen?.find((i) => i.modul === modul)
  const titel = instanz?.name ?? module.find((m) => m.kennung === modul)?.titel ?? 'Entfeuchter'
  // In der Übersicht steht der erste Entfeuchter oft als „Entfeuchtung“ (mit dem Zusatz zusammengefasst).
  const ersterTitel = module.find((m) => m.kennung === 'entfeuchter' || m.kennung === 'entfeuchtung')?.titel ?? null
  const vorKopf = <SteuerungWechsel module={module} aktiv={aktiv} onWechsel={onWechsel} />

  if (loeschenOffen) {
    return (
      <InstanzLoeschen
        art="entfeuchter" modul={modul} titel={titel} ersterTitel={ersterTitel}
        onAbbrechen={() => setLoeschenOffen(false)} onGeloescht={onInstanzGeloescht}
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
  const speichern = async () => { if (!(await h.speichern())) fehlerZeigen() }

  return (
    <V1Page
      eyebrow="Betrieb"
      title={titel}
      subtitle={`${titel} — geregelt in Home Assistant`}
      vorKopf={vorKopf}
      action={h.geaendert ? <V1Button variant="primary" onClick={() => void speichern()} disabled={h.arbeitet}>{h.arbeitet ? 'Speichert …' : 'Speichern'}</V1Button> : undefined}
    >
      {h.fehler && <V1Alert tone="critical" message={h.fehler} />}
      {h.meldung && <V1Alert tone={h.meldung === 'Gespeichert.' ? 'ok' : 'warn'} message={h.meldung} />}
      <HaStummHinweis haErreichbar={hl.haErreichbar} eingerichtet={h.seite.geraeteZugeordnet > 0} />
      <BestandHinweis bestand={bestand} />
      {abweichungen.length > 0 && (
        <V1Alert tone="warn" title="Home Assistant regelt mit anderen Werten" message={`${abweichungsText(abweichungen)}. Geregelt wird nach den Werten in Home Assistant.`} />
      )}
      {hl.portOnline === false && <V1Alert tone="warn" title={`${titel} offline`} message="Der Port meldet sich nicht. Geschaltet wird erst, wenn er wieder da ist." />}
      {hl.automatikAn === false && <V1Alert tone="warn" title={`Automatik aus: ${titel}`} message={`Die Regelung ist angehalten. ${titel} bleibt, wie er gerade steht.`} />}

      <V1Tabs items={ENTFEUCHTUNG_REITER} active={reiter} onChange={setReiter} label="Bereich" insBild />

      {reiter === 'ueberblick' && <UeberblickTab h={h} z={null} haupt={titel} zusatz="" zielwerte={zielwerte} onSpringen={() => setReiter('einstellungen')} />}
      {reiter === 'einstellungen' && <EinstellungenTab h={h} z={null} haupt={titel} zusatz="" zielwerte={zielwerte} />}
      {reiter === 'einrichtung' && (
        <>
          <Klappkachel titel="Zuordnung" zusammenfassung={`${h.seite.geraeteZugeordnet} / ${h.seite.geraeteGesamt}`}>
            <V1Card>
              <GeraeteZeile zugeordnet={h.seite.geraeteZugeordnet} gesamt={h.seite.geraeteGesamt} pfad={rollenPfad(modul)} label={titel} />
            </V1Card>
          </Klappkachel>
          <BestandAbschnitt modul={modul} schaltet="den Entfeuchter" bestand={bestand} neuLaden={bestandNeuLaden} />
        </>
      )}

      <InstanzBlock art="entfeuchter" instanz={instanz} neuLaden={neuLaden} onGeaendert={onInstanzGeaendert} onLoeschen={() => setLoeschenOffen(true)} />
    </V1Page>
  )
}
