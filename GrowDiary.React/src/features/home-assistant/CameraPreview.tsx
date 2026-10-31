import { useState } from 'react'
import { resolveUrl } from '../../base'

function cameraLabel(entity: string, index: number): string {
  const short = entity.replace(/^(camera|image)\./i, '').replace(/[_-]+/g, ' ').trim()
  return short ? `Kamera ${index + 1} · ${short}` : `Kamera ${index + 1}`
}

/** Ein Live-Bild einer Kamera-Entität, mit eigenem Lade-/Fehlerzustand, damit eine kaputte Kamera die anderen nicht verdeckt. */
export function CameraPreview({ tentId, entity, index, nonce }: { tentId: number; entity: string; index: number; nonce: number }) {
  const [state, setState] = useState<'loading' | 'ok' | 'error'>('loading')
  const label = cameraLabel(entity, index)
  return (
    <figure className="ha-preview">
      <div className="ha-preview-frame">
        <img
          src={resolveUrl(`/api/live/tents/${tentId}/camera?entity=${encodeURIComponent(entity)}&t=${nonce}`)}
          alt={label}
          style={{ display: state === 'ok' ? 'block' : 'none' }}
          onLoad={() => setState('ok')}
          onError={() => setState('error')}
        />
        {state === 'loading' && <span>Lädt…</span>}
        {state === 'error' && <span>Kein Bild — in Home Assistant erreichbar?</span>}
      </div>
      <figcaption>{label}</figcaption>
    </figure>
  )
}
