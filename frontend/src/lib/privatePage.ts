import { useEffect } from 'react'

// Niet in zoekmachines en het adres niet doorgeven aan andere sites. De server stuurt dezelfde regels mee als header.
// Voor de factuur achter de link en voor het klantportaal.
export function usePrivatePage() {
  useEffect(() => {
    const tags = [['robots', 'noindex, nofollow'], ['referrer', 'no-referrer']].map(([name, content]) => {
      const meta = Object.assign(document.createElement('meta'), { name, content })
      document.head.appendChild(meta)
      return meta
    })
    return () => tags.forEach(t => t.remove())
  }, [])
}
