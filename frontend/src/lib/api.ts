export class ApiError extends Error {
  status: number
  constructor(message: string, status: number) {
    super(message)
    this.status = status
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
    try {
      const data = await res.json()
      if (data?.error) message = data.error
      else if (data?.title) message = data.title
    } catch { /* geen JSON */ }
    throw new ApiError(message, res.status)
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
