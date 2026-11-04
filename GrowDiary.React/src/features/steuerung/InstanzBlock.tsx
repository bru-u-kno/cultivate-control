import { useState } from 'react'
import { formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1Field, V1Page, V1Section, V1Switch } from '../../components/v1'
import { ZeltChips } from './ZeltChips'
import { instanzEntfernen, instanzUmbenennen, instanzZeltSetzen, useZelte } from './instanzen'
import { INSTANZ_BEZEICHNUNG, INSTANZ_WEIBLICH } from './steuerung-typen'
import type { InstanzArt, SteuerungInstanz } from './steuerung-typen'
import './steuerung.css'

/** „Dieser Wasserkühler“: Name und Zelt der Instanz ändern, Löschen öffnet die Rückfrage. */
export function InstanzBlock({ art = 'chiller', instanz, neuLaden, onGeaendert, onLoeschen }: {
  art?: InstanzArt
  instanz: SteuerungInstanz | undefined
  neuLaden: () => Promise<void>
  onGeaendert: () => void
  onLoeschen: () => void
}) {
  // Die Meldung liegt hier und nicht im Formular: das beginnt nach dem Speichern neu (siehe `key`) und verlöre sie sonst.
  const [meldung, setMeldung] = useState<string | null>(null)
  if (!instanz) return null
  // `key`: Nach dem Speichern (neuer Name, neues Zelt) beginnt das Formular mit dem gespeicherten Stand.
  return (
    <InstanzFormular
      key={`${instanz.name}|${instanz.zeltId}`} art={art} instanz={instanz} neuLaden={neuLaden} meldung={meldung} setMeldung={setMeldung}
      onGeaendert={onGeaendert} onLoeschen={onLoeschen}
    />
  )
}

function InstanzFormular({ art, instanz, neuLaden, meldung, setMeldung, onGeaendert, onLoeschen }: {
  art: InstanzArt
  instanz: SteuerungInstanz
  neuLaden: () => Promise<void>
  meldung: string | null
  setMeldung: (text: string | null) => void
  onGeaendert: () => void
  onLoeschen: () => void
}) {
  const bezeichnung = INSTANZ_BEZEICHNUNG[art]
  const zelte = useZelte()
  const [name, setName] = useState(instanz.name)
  const [zelt, setZelt] = useState<number | null>(instanz.zeltId)
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)

  const geaendert = name.trim() !== instanz.name || (zelt != null && zelt !== instanz.zeltId)
  const mehrereZelte = zelte.length >= 2

  const speichern = async () => {
    setArbeitet(true)
    setFehler(null)
    setMeldung(null)
    try {
      if (name.trim() !== instanz.name) await instanzUmbenennen(instanz.modul, name.trim())
      if (zelt != null && zelt !== instanz.zeltId) await instanzZeltSetzen(instanz.modul, zelt)
      await neuLaden()
      onGeaendert()
      setMeldung('Gespeichert.')
    } catch (caught) {
      setFehler(formatApiError(caught, 'Speichern fehlgeschlagen.'))
    } finally {
      setArbeitet(false)
    }
  }

  return (
    <V1Section title={`${INSTANZ_WEIBLICH[art] ? 'Diese' : 'Dieser'} ${bezeichnung}`}>
      {fehler && <V1Alert tone="critical" message={fehler} />}
      {meldung && <V1Alert tone="ok" message={meldung} />}
      <V1Card>
        <V1Field label="Name" wide>
          <input type="text" aria-label="Name" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} />
        </V1Field>
        {mehrereZelte && (
          <div className="st-feldzeile" style={{ display: 'block' }}>
            <span className="st-etikett">Zelt</span>
            <ZeltChips zelte={zelte} wert={zelt} onChange={setZelt} />
          </div>
        )}
      </V1Card>
      <div className="st-knopfleiste">
        <V1Button variant="danger" onClick={onLoeschen}>Löschen …</V1Button>
        <V1Button variant="primary" disabled={arbeitet || !geaendert || name.trim().length === 0} onClick={() => void speichern()}>{arbeitet ? 'Speichert …' : 'Speichern'}</V1Button>
      </div>
    </V1Section>
  )
}

/** Die Rückfrage vor dem Löschen. Entfernt wird die Instanz aus Cultivate Control; ihre Bausteine in Home Assistant bleiben stehen. */
export function InstanzLoeschen({ art = 'chiller', modul, titel, ersterTitel, onAbbrechen, onGeloescht }: {
  art?: InstanzArt
  modul: string
  titel: string
  ersterTitel: string | null
  onAbbrechen: () => void
  onGeloescht: () => void
}) {
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)
  const [ausHa, setAusHa] = useState(false)
  const b = INSTANZ_BEZEICHNUNG[art]
  // Zuluft ist weiblich: „Die Zuluft verschwindet … Ihre Regelung“, die anderen Arten männlich.
  const w = INSTANZ_WEIBLICH[art]
  const der = w ? 'Die' : 'Der'
  const sein = w ? 'Ihre' : 'Seine'

  const loeschen = async () => {
    setArbeitet(true)
    setFehler(null)
    try {
      await instanzEntfernen(modul, ausHa)
      onGeloescht()
    } catch (caught) {
      setFehler(formatApiError(caught, 'Löschen fehlgeschlagen.'))
      setArbeitet(false)
    }
  }

  return (
    <V1Page eyebrow="Steuerung" title={`${titel} löschen?`}>
      {fehler && <V1Alert tone="critical" message={fehler} />}
      <V1Section title="Wirklich löschen?">
        <V1Card>
          <p className="st-hinweis">
            {der} {b} verschwindet aus Cultivate Control: Seite, Zuordnung und Zelt.{' '}
            {ausHa
              ? `${sein} Automationen, Rechenwerte und Helfer werden auch aus Home Assistant entfernt.`
              : `${sein} Regelung in Home Assistant läuft weiter, bis du sie dort entfernst — Helfer, Rechenwerte und Automationen bleiben stehen.`}
          </p>
          <V1Switch
            label="Auch aus Home Assistant entfernen"
            checked={ausHa}
            onChange={setAusHa}
            hint={`Helfer, Rechenwerte und Automationen ${w ? 'dieser' : 'dieses'} ${b}${w ? '' : 's'}. Die Automationen werden vorher als Datei gesichert; von Hand Gebautes bleibt stehen. Klappt nicht alles, bleibt ${w ? 'die' : 'der'} ${b} stehen und du kannst es noch einmal versuchen.`}
          />
        </V1Card>
        {ersterTitel && (
          <p className="st-hinweis">{w ? 'Die erste' : 'Den ersten'} {b} („{ersterTitel}“) kannst du nicht löschen, nur in der Auswahl abwählen.</p>
        )}
        <div className="st-knopfleiste">
          <V1Button variant="ghost" onClick={onAbbrechen}>Abbrechen</V1Button>
          <V1Button variant="danger" disabled={arbeitet} onClick={() => void loeschen()}>{arbeitet ? 'Löscht …' : 'Löschen'}</V1Button>
        </div>
      </V1Section>
    </V1Page>
  )
}
