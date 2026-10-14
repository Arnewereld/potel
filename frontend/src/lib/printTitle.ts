import { useEffect } from 'react'

// Tijdens printen heet het tabblad zoals het document, want daar maakt de browser de bestandsnaam van de PDF van.
export function usePrintTitle(title: string) {
  useEffect(() => {
    let previous = document.title
    const before = () => { previous = document.title; document.title = title }
    const after = () => { document.title = previous }
    window.addEventListener('beforeprint', before)
    window.addEventListener('afterprint', after)
    return () => {
      window.removeEventListener('beforeprint', before)
      window.removeEventListener('afterprint', after)
    }
  }, [title])
}
