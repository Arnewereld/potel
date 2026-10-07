import { useState } from 'react'
import { api } from '../lib/api'
import type { Customer } from '../lib/types'
import { useToast } from '../lib/toast'
import { euCountries, inEu, isNetherlands, netherlands, otherCountries } from '../lib/invoice'
import { Field, Modal } from './ui'

const empty: Omit<Customer, 'id'> = { name: '', company: '', email: '', phone: '', address: '', city: '', country: netherlands, vatNumber: '', notes: '' }
const otherEu = euCountries.filter(c => c !== netherlands)

export function CustomerForm({ customer, onClose, onSaved }: {
  customer?: Customer | null
  onClose: () => void
  onSaved: (c: Customer) => void
}) {
  const [form, setForm] = useState({ ...empty, ...customer })
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const set = (k: keyof Customer) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm(f => ({ ...f, [k]: e.target.value }))
  // Een land dat niet in de lijsten staat, typ je zelf in.
  const country = form.country?.trim() || netherlands
  const listed = [...euCountries, ...otherCountries].includes(country)
  const [typing, setTyping] = useState(!listed)
  const pickCountry = (e: React.ChangeEvent<HTMLSelectElement>) => {
    if (e.target.value === '') setTyping(true)
    setForm(f => ({ ...f, country: e.target.value }))
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      const saved = customer?.id
        ? await api.put<Customer>(`/customers/${customer.id}`, form)
        : await api.post<Customer>('/customers', form)
      toast(customer?.id ? 'Klant opgeslagen' : 'Klant toegevoegd')
      onSaved(saved)
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title={customer?.id ? 'Klant bewerken' : 'Nieuwe klant'}
      onClose={onClose}
      footer={<>
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="customer-form" disabled={busy}>Opslaan</button>
      </>}
    >
      <form id="customer-form" className="form-grid" onSubmit={save}>
        <Field label="Naam *"><input required autoFocus value={form.name} onChange={set('name')} /></Field>
        <Field label="Bedrijf"><input value={form.company ?? ''} onChange={set('company')} /></Field>
        <Field label="E-mail"><input type="email" value={form.email ?? ''} onChange={set('email')} /></Field>
        <Field label="Telefoon"><input value={form.phone ?? ''} onChange={set('phone')} /></Field>
        <Field label="Adres"><input value={form.address ?? ''} onChange={set('address')} /></Field>
        <Field label="Postcode en plaats"><input value={form.city ?? ''} onChange={set('city')} /></Field>
        <Field label="Land">
          {typing ? (
            <input value={form.country ?? ''} placeholder="Bijv. Japan" onChange={set('country')} />
          ) : (
            <select value={country} onChange={pickCountry}>
              <option value={netherlands}>{netherlands}</option>
              <optgroup label="Andere EU-landen">{otherEu.map(c => <option key={c}>{c}</option>)}</optgroup>
              <optgroup label="Buiten de EU">{otherCountries.map(c => <option key={c}>{c}</option>)}</optgroup>
              <option value="">Ander land…</option>
            </select>
          )}
        </Field>
        <Field label="Btw-nummer">
          <input value={form.vatNumber ?? ''} onChange={set('vatNumber')}
            placeholder={isNetherlands(country) ? 'Bijv. NL001234567B01' : inEu(country) ? 'Nodig voor btw verlegd, bijv. DE123456789' : 'Optioneel'} />
        </Field>
        <Field label="Notities" full><textarea value={form.notes ?? ''} onChange={set('notes')} /></Field>
      </form>
    </Modal>
  )
}
