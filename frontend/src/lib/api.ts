export class ApiError extends Error {
  status: number
  // De rest van het antwoord, zoals de actuele stand van de timer bij een conflict.
  data: unknown
  constructor(message: string, status: number, data?: unknown) {
    super(message)
    this.status = status
    this.data = data
  }
}

async function send(method: string, url: string, body?: unknown): Promise<Response> {
  const res = await fetch(`/api${url}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (res.status === 401 && !url.startsWith('/auth/')) {
    window.dispatchEvent(new Event('potel:unauthorized'))
  }
  if (!res.ok) {
    let message = `Er ging iets mis (${res.status})`
    let data: unknown
    try {
      data = await res.json()
      const body = data as { error?: string; title?: string } | null
      if (body?.error) message = body.error
      else if (body?.title) message = body.title
    } catch { /* geen JSON */ }
    throw new ApiError(message, res.status, data)
  }
  return res
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await send(method, url, body)
  if (res.status === 204) return undefined as T
  return res.json()
}

// Haalt een bestand op en laat de browser het opslaan, met dezelfde foutmeldingen als de rest van de API.
// Met body gaat het via POST, zoals bij de openbare factuur waar het token niet in de URL mag staan.
async function download(url: string, fileName: string, body?: unknown) {
  const res = await send(body === undefined ? 'GET' : 'POST', url, body)
  const href = URL.createObjectURL(await res.blob())
  const a = Object.assign(document.createElement('a'), { href, download: fileName })
  a.click()
  setTimeout(() => URL.revokeObjectURL(href), 1000)
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body: unknown) => request<T>('PUT', url, body),
  del: <T = void>(url: string, body?: unknown) => request<T>('DELETE', url, body),
  download,
}
