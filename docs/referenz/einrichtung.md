# Einrichtung — von der Installation bis zum laufenden Betrieb

> Eine Stelle für alles, was man nach der Installation einmal einrichtet: Schritte mit Live-Stand, die Anlage und die Technik (cc.287).

## Wo in der App

| Was | Wo |
|---|---|
| Schritte (Stand der Einrichtung) | Menü *Einrichtung* → `/einrichtung` (alte Adresse `/start` leitet hierher) |
| Anlage: Zelte & Räume, Hydro-Systeme, Wasser | `/einrichtung?bereich=anlage`; die Seiten selbst unter `/zelte`, `/hydro`, `/wasser` |
| Technik | `/geraete` — Reiter Geräte, Zuordnung, Rollen, Räume, Inventar & Wartung, Verbindung |
| Handy, KI-Assistent | eigene Menüpunkte unter *Einrichtung* (`/handy`, `/ki`); je ein Schritt in der Liste |

Oben tragen `/einrichtung` und `/geraete` dieselbe Reiterleiste (Schritte, Anlage, Technik).

## Was es tut

Die Schritteliste liest bei jedem Öffnen den Stand: Verbindung zu Home Assistant, Zelte, Zuordnung (Temperatur und Feuchte je Zelt), Steuerungen, Hydro-Systeme, Wasserprofil, Push, optional der KI-Assistent. Jede Zeile führt zur Seite, an der man den Schritt erledigt. Oben stehen „x von y erledigt“ und der nächste offene Schritt.

## Die Zahlen und woher sie kommen

Alles sind Zählungen aus vorhandenen Abfragen (`/api/settings`, `/api/home-assistant/entities`, `/api/steuerung/auswahl`, `/api/hydro-setups`, `/api/water-profile`, `/api/notifications/settings`), berechnet in `features/einrichtung/schritte.ts`. Archivierte Zelte zählen nicht; optionale und nicht lesbare Schritte zählen nicht in „x von y“.

## Was es bewusst NICHT tut

- Es prüft nicht gegen bestimmte Geräte oder Entitäten: Wer andere Geräte hat, sieht dieselbe Liste.
- Ein Abruf, der scheitert, zeigt „nicht lesbar“, nie „erledigt“.
- Es legt nichts an und ändert nichts; erledigt wird auf den Seiten dahinter.

## Im Code

`pages/EinrichtungPage.tsx`, `features/einrichtung/` (`schritte.ts` mit Test, `EinrichtungReiter.tsx`, `bereiche.ts`), Menüeintrag und Weiterleitung `/start` in `navigation.ts`.
