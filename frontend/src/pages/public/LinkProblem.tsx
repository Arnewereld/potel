import type { ReactNode } from 'react'
import { t, type DocKey } from '../../lib/docTexts'
import type { LinkError } from '../../lib/linkError'

// Werkt de link niet, dan weten we niet in welke taal de klant leest. Daarom staat de melding er in het Nederlands en
// het Engels. Een andere fout komt van de server in het Nederlands; daar zetten we een algemene Engelse zin onder.
export function LinkProblem({ icon, title, gone, incomplete = 'public.linkIncomplete', error }: {
  icon: ReactNode; title: DocKey; gone: DocKey; incomplete?: DocKey; error: LinkError
}) {
  const text = (lang: 'nl' | 'en') =>
    error.kind === 'incomplete' ? t(lang, incomplete) : error.kind === 'gone' ? t(lang, gone) : lang === 'nl' ? error.message : t(lang, 'public.error')
  return (
    <div className="login login-single">
      <div className="login-card">
        <h1>{icon} {t('nl', title)}</h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{text('nl')}</p>
        <h2 lang="en" style={{ fontSize: 16, margin: '18px 0 0' }}>{t('en', title)}</h2>
        <p lang="en" className="muted" style={{ margin: '6px 0 0' }}>{text('en')}</p>
      </div>
    </div>
  )
}
