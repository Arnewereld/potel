import { useRef } from 'react'
import { ImagePlus, Trash2 } from 'lucide-react'
import type { Settings } from '../lib/types'
import { useToast } from '../lib/toast'

export const brandColors = ['#ff6d5a', '#4ea5ff', '#3ecf8e', '#9b7bff', '#f5b83d', '#ff5ca8', '#2fc6c6', '#1d1d2b']
// Dezelfde regels als de server (SettingsEndpoints): PNG, JPG of WebP tot 300 KB. Geen SVG, daar kan script in.
const MaxLogoBytes = 300 * 1024
const logoTypes = ['image/png', 'image/jpeg', 'image/webp']

// Logo en accentkleur voor op de factuur.
export function BrandFields({ value, onChange, disabled = false }: {
  value: Pick<Settings, 'logoDataUrl' | 'brandColor' | 'companyName'>
  onChange: (patch: Partial<Settings>) => void
  disabled?: boolean
}) {
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()

  const pick = (file?: File) => {
    if (!file || disabled) return
    if (!logoTypes.includes(file.type)) { toast('Kies een PNG-, JPG- of WebP-afbeelding', 'error'); return }
    if (file.size > MaxLogoBytes) { toast('Je logo mag maximaal 300 KB zijn', 'error'); return }
    const reader = new FileReader()
    reader.onload = () => onChange({ logoDataUrl: String(reader.result) })
    reader.readAsDataURL(file)
  }

  const color = value.brandColor || brandColors[0]!
  return (
    <div className="brand-fields">
      <div>
        <div className="field-label">Logo</div>
        <div className="logo-drop" onClick={() => !disabled && input.current?.click()}
          onDragOver={e => e.preventDefault()} onDrop={e => { e.preventDefault(); pick(e.dataTransfer.files[0]) }}>
          {value.logoDataUrl
            ? <img src={value.logoDataUrl} alt="Logo" />
            : <div className="logo-placeholder" style={{ background: color }}>{(value.companyName || '?').slice(0, 1)}</div>}
          <div>
            <strong>{value.logoDataUrl ? 'Ander logo kiezen' : 'Logo uploaden'}</strong>
            <div className="muted">PNG, JPG of WebP, maximaal 300 KB. Sleep het hierheen of klik.</div>
          </div>
          {value.logoDataUrl && !disabled && <button type="button" className="icon-btn danger" title="Logo verwijderen" onClick={e => { e.stopPropagation(); onChange({ logoDataUrl: null }) }}><Trash2 size={16} /></button>}
          {!value.logoDataUrl && <ImagePlus size={20} className="muted" />}
        </div>
        <input ref={input} type="file" accept={logoTypes.join(',')} hidden disabled={disabled} onChange={e => pick(e.target.files?.[0])} />
      </div>
      <div>
        <div className="field-label">Accentkleur</div>
        <div className="color-row">
          {brandColors.map(c => <button type="button" key={c} disabled={disabled} className={`color-swatch ${color === c ? 'selected' : ''}`} style={{ background: c }} onClick={() => onChange({ brandColor: c })} aria-label={c} />)}
          <label className="color-swatch color-custom" title="Eigen kleur" style={{ background: brandColors.includes(color) ? undefined : color }}>
            <input type="color" value={color} disabled={disabled} onChange={e => onChange({ brandColor: e.target.value })} />
          </label>
        </div>
      </div>
    </div>
  )
}
