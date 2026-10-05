import { useMemo, useState } from 'react'
import { CalendarDays, ChevronLeft, ChevronRight, Plus, List, LayoutGrid, Trash2, MapPin, Check } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'
import type { Appointment, Customer } from '../lib/types'
import { dateTime, parseDate, time, toDateInput, toDateTimeInput } from '../lib/format'
import { appointmentKinds, kindColor } from '../lib/status'
import { Empty, Field, Modal, PageHeader, SubTabs } from '../components/ui'

const START_HOUR = 7
const END_HOUR = 21
const HOUR_PX = 52
type View = 'week' | 'lijst'

function startOfWeek(d: Date) {
  const r = new Date(d.getFullYear(), d.getMonth(), d.getDate())
  r.setDate(r.getDate() - ((r.getDay() + 6) % 7))
  return r
}

export function PlanningPage() {
  const [weekStart, setWeekStart] = useState(() => startOfWeek(new Date()))
  const [view, setView] = useState<View>('week')
  const [editing, setEditing] = useState<Partial<Appointment> | null>(null)
  const { data, setData, reload } = useApi<Appointment[]>('/appointments')
  const { data: customers } = useApi<Customer[]>('/customers')
  const toast = useToast()

  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => {
    const d = new Date(weekStart); d.setDate(d.getDate() + i); return d
  }), [weekStart])
  const appointments = data ?? []
  const todayKey = toDateInput(new Date())

  const shiftWeek = (n: number) => setWeekStart(w => { const d = new Date(w); d.setDate(d.getDate() + n * 7); return d })

  const newAt = (day: Date, hour: number) => {
    const s = new Date(day); s.setHours(hour, 0, 0, 0)
    const e = new Date(s); e.setHours(hour + 1)
    setEditing({ title: '', kind: 'afspraak', start: toDateTimeInput(s), end: toDateTimeInput(e), done: false })
  }

  const toggleDone = async (a: Appointment) => {
    setData(prev => prev?.map(x => (x.id === a.id ? { ...x, done: !a.done } : x)) ?? null)
    try { await api.put(`/appointments/${a.id}`, { ...a, done: !a.done }) } catch (e) { toast((e as Error).message, 'error'); reload() }
  }

  const weekLabel = `${days[0]!.toLocaleDateString('nl-NL', { day: 'numeric', month: 'short' })} – ${days[6]!.toLocaleDateString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric' })}`
  const nowMinutes = new Date().getHours() * 60 + new Date().getMinutes()

  return (
    <>
      <PageHeader
        icon={<CalendarDays size={20} />}
        title="Planning"
        subtitle="Afspraken, taken en projecten op één plek."
        actions={<button className="btn btn-primary" onClick={() => newAt(new Date(), Math.min(END_HOUR - 1, new Date().getHours() + 1))}><Plus size={16} /> Inplannen</button>}
      />

      <div className="row between wrap" style={{ marginBottom: 16 }}>
        <SubTabs<View> value={view} onChange={setView} tabs={[
          { id: 'week', label: 'Week', icon: <LayoutGrid size={14} /> },
          { id: 'lijst', label: 'Takenlijst', icon: <List size={14} />, count: appointments.filter(a => !a.done).length },
        ]} />
        {view === 'week' && (
          <div className="row" style={{ marginBottom: 16 }}>
            <button className="btn btn-sm" onClick={() => setWeekStart(startOfWeek(new Date()))}>Vandaag</button>
            <button className="icon-btn" onClick={() => shiftWeek(-1)} aria-label="Vorige week"><ChevronLeft size={18} /></button>
            <strong style={{ minWidth: 170, textAlign: 'center' }}>{weekLabel}</strong>
            <button className="icon-btn" onClick={() => shiftWeek(1)} aria-label="Volgende week"><ChevronRight size={18} /></button>
          </div>
        )}
      </div>

      {view === 'week' && (
        <div className="card calendar">
          <div className="cal-header">
            <div />
            {days.map(d => (
              <div key={d.toISOString()} className={`cal-day-head ${toDateInput(d) === todayKey ? 'today' : ''}`}>
                <span>{d.toLocaleDateString('nl-NL', { weekday: 'short' })}</span>
                <strong>{d.getDate()}</strong>
              </div>
            ))}
          </div>
          <div className="cal-body" style={{ height: (END_HOUR - START_HOUR) * HOUR_PX }}>
            <div className="cal-hours">
              {Array.from({ length: END_HOUR - START_HOUR }, (_, i) => (
                <div key={i} style={{ height: HOUR_PX }}>{String(START_HOUR + i).padStart(2, '0')}:00</div>
              ))}
            </div>
            {days.map(d => {
              const key = toDateInput(d)
              const items = appointments.filter(a => toDateInput(parseDate(a.start)) === key)
              return (
                <div key={key} className="cal-col">
                  {Array.from({ length: END_HOUR - START_HOUR }, (_, i) => (
                    <div key={i} className="cal-slot" style={{ height: HOUR_PX }} onClick={() => newAt(d, START_HOUR + i)} />
                  ))}
                  {key === todayKey && nowMinutes >= START_HOUR * 60 && nowMinutes <= END_HOUR * 60 && (
                    <div className="cal-now" style={{ top: ((nowMinutes - START_HOUR * 60) / 60) * HOUR_PX }} />
                  )}
                  {items.map(a => {
                    const s = parseDate(a.start), e = parseDate(a.end)
                    const top = Math.max(0, ((s.getHours() * 60 + s.getMinutes() - START_HOUR * 60) / 60) * HOUR_PX)
                    const height = Math.max(24, ((e.getTime() - s.getTime()) / 3600000) * HOUR_PX - 3)
                    const color = kindColor(a.kind)
                    return (
                      <div key={a.id} className={`cal-event ${a.done ? 'done' : ''}`} style={{ top, height, borderLeftColor: color, background: `color-mix(in srgb, ${color} 18%, var(--panel))` }}
                        onClick={() => setEditing({ ...a, start: a.start.slice(0, 16), end: a.end.slice(0, 16) })}>
                        <strong className="truncate">{a.title}</strong>
                        <span>{time(a.start)} – {time(a.end)}</span>
                      </div>
                    )
                  })}
                </div>
              )
            })}
          </div>
        </div>
      )}

      {view === 'lijst' && (
        <div className="card">
          <div className="card-body">
            {appointments.length === 0 ? <Empty icon={<CalendarDays size={22} />} title="Nog niets gepland" /> : (
              <div className="list">
                {[...appointments].sort((a, b) => Number(a.done) - Number(b.done) || a.start.localeCompare(b.start)).map(a => (
                  <div key={a.id} className={`list-item task ${a.done ? 'done' : ''}`}>
                    <button className={`check ${a.done ? 'checked' : ''}`} onClick={() => toggleDone(a)} aria-label="Afvinken">{a.done && <Check size={13} />}</button>
                    <span className="kind-bar" style={{ background: kindColor(a.kind) }} />
                    <div className="grow clickable" onClick={() => setEditing({ ...a, start: a.start.slice(0, 16), end: a.end.slice(0, 16) })}>
                      <div className="task-title">{a.title}</div>
                      <div className="cell-sub row">{dateTime(a.start)}{a.location && <><MapPin size={11} /> {a.location}</>}</div>
                    </div>
                    <span className="badge badge-gray">{a.kind}</span>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}

      {editing && <AppointmentForm item={editing} customers={customers ?? []} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

function AppointmentForm({ item, customers, onClose, onSaved }: { item: Partial<Appointment>; customers: Customer[]; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState(item)
  const toast = useToast()
  const set = (k: keyof Appointment) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) =>
    setForm(f => ({ ...f, [k]: k === 'customerId' ? (Number(e.target.value) || null) : e.target.value }))

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      if (item.id) await api.put(`/appointments/${item.id}`, form)
      else await api.post('/appointments', form)
      toast(item.id ? 'Opgeslagen' : 'Ingepland')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const remove = async () => {
    await api.del(`/appointments/${item.id}`)
    toast('Verwijderd')
    onSaved()
  }

  return (
    <Modal title={item.id ? 'Planning bewerken' : 'Nieuw inplannen'} onClose={onClose} footer={<>
      {item.id && <button className="btn btn-danger" type="button" onClick={remove}><Trash2 size={15} /></button>}
      <span className="spacer" />
      <button className="btn" type="button" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="appt-form">Opslaan</button>
    </>}>
      <form id="appt-form" className="form-grid" onSubmit={save}>
        <Field label="Titel *" full><input required autoFocus value={form.title ?? ''} onChange={set('title')} /></Field>
        <Field label="Begin"><input type="datetime-local" required value={form.start ?? ''} onChange={set('start')} /></Field>
        <Field label="Einde"><input type="datetime-local" required value={form.end ?? ''} onChange={set('end')} /></Field>
        <Field label="Soort" full>
          <div className="kind-picker">
            {appointmentKinds.map(k => (
              <button type="button" key={k.id} className={form.kind === k.id ? 'selected' : ''} style={{ '--k': k.color } as React.CSSProperties} onClick={() => setForm(f => ({ ...f, kind: k.id }))}>
                <span className="legend-dot" style={{ background: k.color }} /> {k.label}
              </button>
            ))}
          </div>
        </Field>
        <Field label="Klant">
          <select value={form.customerId ?? 0} onChange={set('customerId')}>
            <option value={0}>Geen</option>
            {customers.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </Field>
        <Field label="Locatie"><input value={form.location ?? ''} onChange={set('location')} /></Field>
        <Field label="Notities" full><textarea value={form.notes ?? ''} onChange={set('notes')} /></Field>
        <label className="row field-full"><input type="checkbox" checked={!!form.done} onChange={e => setForm(f => ({ ...f, done: e.target.checked }))} /> Afgerond</label>
      </form>
    </Modal>
  )
}
