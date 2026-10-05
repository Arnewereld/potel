export class ApiError extends Error {}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api${url}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) {
    let message = `Er ging iets mis (${res.status})`
    try {
      const data = await res.json()
      if (data?.error) message = data.error
      else if (data?.title) message = data.title
    } catch { /* geen JSON */ }
    throw new ApiError(message)
  }
  if (res.status === 204) return undefined as T
  return res.json()
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T>(url: string, body: unknown) => request<T>('PUT', url, body),
  del: (url: string) => request<void>('DELETE', url),
}
