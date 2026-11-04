import { useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { formatApiError } from '../../api'
import { V1Alert, V1Button, V1Card, V1Field, V1Page, V1Section } from '../../components/v1'
import { SteuerungWechsel } from './SteuerungWechsel'
import { ZeltChips } from './ZeltChips'
import { chillerAnlegen, useZelte } from './instanzen'
import type { SteuerungModul } from './steuerung-typen'
import './steuerung.css'

/**
 * Einen weiteren Wasserkühler anlegen: Name und (ab zwei Zelten) das Zelt. Angelegt wird nur der Eintrag in Cultivate Control;
 * die Geräte und die Bausteine in Home Assistant folgen auf der Seite des neuen Wasserkühlers. Bei einem Zelt kommt keine
 * Zelt-Frage dazu.
 */
export function InstanzAnlegen({ module, onAngelegt, onWechsel }: {
  module: SteuerungModul[]
  onAngelegt: () => void
  onWechsel: (kennung: string) => void
}) {
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const zelte = useZelte()
  const gewuenscht = Number(params.get('zelt'))
  const [name, setName] = useState('')
  const [zeltWahl, setZeltWahl] = useState<number | null>(Number.isFinite(gewuenscht) && gewuenscht > 0 ? gewuenscht : null)
  const [fehler, setFehler] = useState<string | null>(null)
  const [arbeitet, setArbeitet] = useState(false)

  const mehrereZelte = zelte.length >= 2
  const zelt = mehrereZelte ? (zeltWahl ?? zelte[0].id) : null

  const weiter = async () => {
    setArbeitet(true)
    setFehler(null)
    try {
      const neu = await chillerAnlegen(name.trim(), zelt)
      onAngelegt()
      navigate(`/steuerung/${neu.modul}`)
    } catch (caught) {
      setFehler(formatApiError(caught, 'Der Wasserkühler konnte nicht angelegt werden.'))
    } finally {
      setArbeitet(false)
    }
  }

  return (
    <V1Page eyebrow="Steuerung" title="Weiteren Wasserkühler anlegen" vorKopf={<SteuerungWechsel module={module} aktiv="" onWechsel={onWechsel} />}>
      {fehler && <V1Alert tone="critical" message={fehler} />}
      <V1Section title="Neuer Wasserkühler">
        <V1Card>
          <V1Field label="Name" hint="So heißt er in der Übersicht" wide>
            <input type="text" aria-label="Name" value={name} maxLength={60} placeholder="z. B. Wasserkühler Reserve" onChange={(e) => setName(e.target.value)} />
          </V1Field>
          {mehrereZelte && (
            <div className="st-feldzeile" style={{ display: 'block' }}>
              <span className="st-etikett">Zelt</span>
              <ZeltChips zelte={zelte} wert={zelt} onChange={setZeltWahl} />
            </div>
          )}
        </V1Card>
        <p className="st-hinweis">Er bekommt eigene Geräte, eigene Zieltemperaturen und eigene Bausteine in Home Assistant. Der erste Wasserkühler bleibt unverändert.</p>
        <p className="st-hinweis">Danach: Geräte zuordnen und Bausteine anlegen — wie im Einrichtungs-Assistenten, nur für genau diesen Wasserkühler.</p>
        <div className="st-knopfleiste">
          <V1Button variant="ghost" onClick={() => navigate('/steuerung')}>Abbrechen</V1Button>
          <V1Button variant="primary" disabled={arbeitet || name.trim().length === 0} onClick={() => void weiter()}>{arbeitet ? 'Legt an …' : 'Weiter'}</V1Button>
        </div>
      </V1Section>
    </V1Page>
  )
}
