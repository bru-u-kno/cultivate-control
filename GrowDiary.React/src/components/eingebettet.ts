import { createContext } from 'react'

/**
 * cc.286: Steht eine ganze Seite als Reiter in einer anderen (Inventar & Wartung und Verbindung unter Geräte & Entitäten),
 * lässt `V1Page` Kopf und äußeres `<main>` weg — sonst gäbe es zwei Überschriften und ein `<main>` im `<main>`.
 */
export const EingebettetContext = createContext(false)
