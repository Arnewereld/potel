import { ApiError } from './api'

// Wat er mis is met een openbare link: niet compleet, werkt niet (meer), of een andere fout van de server.
export type LinkError = { kind: 'incomplete' } | { kind: 'gone' } | { kind: 'other'; message: string }

export const linkError = (e: unknown): LinkError =>
  e instanceof ApiError && e.status === 404 ? { kind: 'gone' } : { kind: 'other', message: (e as Error).message }
