import { useNavigate } from 'react-router-dom'
import { V1Tabs } from '../../components/v1'

import { BEREICH_PFAD } from './bereiche'
import type { EinrichtungBereich } from './bereiche'

/** Oberste Reiter der Einrichtung (cc.287): beide Seiten, Einrichtung und Geräte & Entitäten, zeigen dieselbe Leiste. */
export function EinrichtungReiter({ aktiv }: { aktiv: EinrichtungBereich }) {
  const navigate = useNavigate()
  return (
    <V1Tabs
      label="Einrichtung"
      active={aktiv}
      onChange={(bereich) => navigate(BEREICH_PFAD[bereich])}
      items={[
        { value: 'schritte', label: 'Schritte' },
        { value: 'anlage', label: 'Anlage' },
        { value: 'technik', label: 'Technik' },
      ]}
    />
  )
}
