import { Download, MoreVertical, Share, Smartphone } from 'lucide-react'
import { installApp, usePwa } from '../lib/pwa'

// Instellingen > Op je telefoon: Potel op je beginscherm zetten, op een iPhone en op Android.
export function InstallAppTab() {
  const { install, installed } = usePwa()
  return (
    <div className="grid grid-1-1">
      <div className="card">
        <div className="card-head"><h3><Smartphone size={16} /> Zet Potel op je telefoon</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Op je beginscherm opent Potel als een app: met een eigen icoon, zonder adresbalk en snel opgestart. Je timer staat op de
            server, dus je start hem op je laptop en stopt hem onderweg op je telefoon.
          </p>
          {installed && <div className="notice notice-ok" style={{ marginBottom: 0 }}>Je gebruikt Potel nu als app op dit apparaat.</div>}
          {!installed && install && (
            <button type="button" className="btn btn-primary" onClick={() => void installApp()}><Download size={15} /> Potel installeren</button>
          )}
          {!installed && !install && (
            <p className="muted" style={{ margin: 0, fontSize: 13 }}>Volg de stappen voor je telefoon hiernaast. Op een computer kan het ook, met het installatie-icoon in de adresbalk van Chrome of Edge.</p>
          )}
        </div>
      </div>
      <div className="card">
        <div className="card-head"><h3>Op een iPhone of iPad</h3></div>
        <div className="card-body">
          <ol className="steps">
            <li><div>Open Potel in <strong>Safari</strong> en log in.</div></li>
            <li><div>Tik op de deelknop <Share size={14} className="inline-icon" /> (het vierkantje met de pijl omhoog).</div></li>
            <li><div>Kies <strong>Zet op beginscherm</strong>. Zie je het niet, scrol dan iets omlaag.</div></li>
            <li><div>Tik op <strong>Voeg toe</strong>. Potel staat nu tussen je apps.</div></li>
          </ol>
        </div>
      </div>
      <div className="card">
        <div className="card-head"><h3>Op Android</h3></div>
        <div className="card-body">
          <ol className="steps">
            <li><div>Open Potel in <strong>Chrome</strong> en log in.</div></li>
            <li><div>Tik rechtsboven op de drie puntjes <MoreVertical size={14} className="inline-icon" />.</div></li>
            <li><div>Kies <strong>App installeren</strong> of <strong>Toevoegen aan startscherm</strong>.</div></li>
            <li><div>Bevestig met <strong>Installeren</strong>. Potel staat nu tussen je apps.</div></li>
          </ol>
        </div>
      </div>
      <div className="card">
        <div className="card-head"><h3>Goed om te weten</h3></div>
        <div className="card-body">
          <ul className="tax-reserve-list">
            <li>Zonder internet zie je een melding dat je offline bent. Je gegevens staan op de server en niet op je telefoon.</li>
            <li>Een timer die loopt, loopt gewoon door, ook als je de app sluit of je telefoon wegstopt.</li>
            <li>Komt er een nieuwe versie van Potel, dan zie je bovenin een balk om te vernieuwen. Doe je niets, dan krijg je hem de volgende keer dat je Potel opent.</li>
          </ul>
        </div>
      </div>
    </div>
  )
}
