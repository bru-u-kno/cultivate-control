# Ernte, Trocknen, Aushärten und Archiv

> Das Ende eines Laufs: wiegen, trocknen, einglasen, lüften — und danach
> nachschlagen, was der Lauf gebracht und gekostet hat.

## Wo in der App

| Was | Wo |
|---|---|
| Ernte erfassen | `/grows/:growId/harvest` — Knopf „Ernte" im Grow-Detail |
| Glas anlegen | Abschnitt „Aushärten" im Grow-Detail (`CuringSection`) |
| Aushärten, alle Gläser | Menü Pflanzen → „Aushärten", `/aushaerten` |
| Gläser, die dran sind | `/aufgaben`, Abschnitt „Aushärten" (`data-audit="af-curing"`) |
| Ernte & Archiv | Menü Pflanzen → „Ernte & Archiv", `/archiv` (Altpfad `/analyse` leitet dorthin) |
| Trocknungs-Klima | Kacheln Temperatur und Feuchte, Vermerk „Trocknung · Tag N · Etappenname"; die Etappen stehen im Programm (Plan › „Plan von" › Reiter „Trocknung") |
| Trocknen/Aushärten im Zeitstrahl | Live, Grow-Liste, Grow-Detail |
| Strompreis eintragen | `/settings` |
| Für die eigene KI | MCP-Werkzeug `aushaerten` (lesend) |

## Was es tut

**Ernte.** Datum, Trocknungsdauer, Frisch- und Trockengewicht, dazu ein Gewicht
je Pflanze — am Trockenregal wiegt man Pflanze für Pflanze. Sind Einzelgewichte
da, gewinnen ihre Summen über die Grow-Felder. Die Trockenausbeute steht schon
beim Tippen daneben; ist trocken schwerer als nass, sagt die App „vermutlich
vertauscht". Dazu Bewertung, Blütenstruktur, Aroma, Effekt. „Ernte speichern" lässt den Grow offen (Teilernte möglich); der zweite Knopf
speichert **und** schließt ab (`POST /api/grows/:id/archive`, Enddatum = Erntedatum).

**Trocknen** hat keinen eigenen Bildschirm, sondern ist ein Zustand des Zelts:
läuft kein Grow mehr, ist der letzte kürzlich geerntet und hat noch kein
Trockengewicht, tragen die Kacheln Temperatur und Feuchte die Trockenziele, und
die Reservoir-Alarme pausieren.

Die **Trockenziele sind Etappen** (seit cc.280, A-029): je Etappe Name, Dauer in
Tagen, Lufttemperatur, Luftfeuchte-Ziel und eine Obergrenze der Feuchte. Sie
liegen im **Programm** des geernteten Grows (Feld `trocknung`), frei editierbar
unter Plan › „Plan von" › Reiter „Trocknung"; ein Programm ohne eigene Etappen
trocknet nach dem Standard („langsam": 3 Tage 20 °C/60 %, 4 Tage 19 °C/58 %,
3 Tage 18 °C/55 %, Obergrenze 65 %). Die Kachel zeigt ein Band um das Ziel
(±1 °C, ±2 Prozentpunkte, oben nie über der Obergrenze); hinter der letzten
Etappe gilt sie weiter, bis das Trockengewicht eingetragen ist. Mitgelieferte
Programme bleiben unverändert — Speichern legt ein eigenes Programm an.

**Aushärten.** Ein Glas, nicht ein Grow — mehrere Sorten härten getrennt aus,
und eine Sorte füllt oft mehrere Gläser. Feuchte und Lüftminuten werden getrennt
eingetragen: wer lüftet, ohne abzulesen, hält den Rhythmus und lernt nichts; wer
abliest, ohne zu lüften, weiß Bescheid und tut nichts. Dazu Feuchte-Ampel mit
Handlung und Quelle sowie der Rhythmus der laufenden Woche.

**Archiv.** Abgeschlossene Läufe mit Geerntet, Dauer, Trocken, g/Pflanze und
Kosten; zwei davon lassen sich nebeneinanderlegen.

## Die Zahlen und woher sie kommen

| Zahl | Wert | Woher |
|---|---|---|
| Feuchtefenster im Glas | 58–62 % | `CuringSchedule.TargetHumidityMin/Max`; budtrainer.com „The 62% RH Jar Curing Guide (2026)" |
| Schimmelgefahr im Glas | ab 65 % | `CuringRating.Rate` |
| Zu trocken | unter 55 % | `CuringRating.Rate` — Terpene werden spröde, nicht reparierbar |
| Woche 1 (Tag 1–7) | täglich, 5–10 min | `CuringSchedule.Fenster`; atmosiscience.com „How Long & How to Burp" |
| Woche 2 (Tag 8–14) | alle 2 Tage, 2–3 min | dito |
| Woche 3–4 (Tag 15–29) | wöchentlich, 1–2 min | dito |
| Mit Feuchtigkeitsregler | Abstand × 2 | `CuringSchedule.Fenster` (`faktor`) |
| Ab Tag 30 | kein Termin mehr, Hygrometer | `CuringSchedule.HygrometerPhaseFromDay` |
| Überfällig | Termin + 1 Tag | `CuringSchedule.Evaluate` |
| Mindestdauer Aushärten | 14 Tage | `CuringSchedule.MinimumCureDays` — **Konstante, nirgends ausgewertet** |
| Kürzlich fertige Gläser | 30 Tage | `CuringRepository.GetOpenJars` |
| Trocknung (Standard) | 3 Etappen: 20 °C/60 %, 19 °C/58 %, 18 °C/55 %; Obergrenze 65 % | `TrocknungsPlan.Standard`; je Programm änderbar (`trocknung`); vorher feste 18–20 °C, 55–60 % in `MoldGuard` (60/60-Faustregel) |
| Trocknung: Grenzen | höchstens 8 Etappen, zusammen höchstens 21 Tage, Name ≤ 60 Zeichen | `TrocknungsPlan.Pruefen`, `MaxTage` = `MoldGuard.DryingWindowDays` |
| Trocknung: Zielband der Kachel | ±1 °C, ±2 Prozentpunkte | `TrocknungsPlan.BandFuer` |
| Feuchtedeckel Dry / Cure | 60 % / 62 % | `MoldGuard.MaxHumidityPercent`; die 62 decken sich mit dem Glas-Fenster |
| Trockenraum-Fenster | 21 Tage nach der Ernte | `MoldGuard.DryingWindowDays`, gelesen in `DryingWindow.DayFor` |
| Zeitstrahl: Trocknen | 10 Tage (Bereich 7–14) | `phase-timeline.ts`, `TROCKNEN_TAGE` |
| Zeitstrahl: Aushärten | 30 Tage (üblich 30–60) | `phase-timeline.ts`, `AUSHAERTEN_TAGE` |
| Erwartetes Trockengewicht | 22 % vom Nassgewicht | `plant-weights-model.ts`, `TYPICAL_DRY_RATIO`; nie gespeichert |
| Strom ohne Lichtplan | 18 h bis zum Flip, danach 12 | `GrowCostService.Berechnen`, als „angenommen" beschriftet |

## Was es bewusst NICHT tut

- **Ab Tag 30 keinen Termin nennen.** Der Kalender gibt dort nichts mehr her;
  eine Frist zu erfinden wäre Scheingenauigkeit (`CuringSchedule`).
- **Nebenverbraucher nicht mitrechnen.** Pumpen, Lüfter, Chiller fehlen im
  Strom, Handzugaben von Dünger stehen in keinem Protokoll — lieber eine
  ehrliche Untergrenze als eine scheingenaue Gesamtzahl. Beides sagt die
  Herkunftszeile (`GrowCostService`).
- **Gleiche Licht-An- und -Aus-Zeit ist kein Dauerlicht**, sondern ein
  Tippfehler (`GrowCostService.Lichtstunden`).
- **Leere Einträge am Glas ablehnen.** Ohne Feuchte und ohne Minuten ist nichts
  passiert, es würde aber den nächsten Termin verschieben
  (`CuringApiController.AddReading`). Und kein „0 %": nie abgelesen ist etwas
  anderes als trocken.
- **Die Aushärte-Seite fragt nicht nach dem Grow-Status.** Die Ernte setzt den
  Grow auf beendet — nach laufenden Grows zu filtern hieße, nie ein Glas zu
  sehen.
- **Kein Durchschnitt über die Blüte im Vergleich.** Gezeigt wird, was beide
  Läufe wirklich haben: letzte Messwerte, Dauer, Ertrag (`buildCompareCells`).
- **Archivieren ist idempotent** — nur `Planning`/`Running` ändern sich
  (`GrowsApiController.Archive`).

## Im Code

| Aufgabe | Datei |
|---|---|
| Ernte-Formular | `GrowDiary.React/src/pages/HarvestPage.tsx` |
| Ausbeute, Einzelgewichte | `GrowDiary.React/src/features/harvest/harvest-yield.ts`, `plant-weights-model.ts` |
| Ernte lesen/schreiben | `GrowDiary.Web/Api/Controllers/GrowWorkflowApiController.cs`, `GrowDiary.Web/Infrastructure/HarvestRepository.cs` |
| Abschließen, archivieren | `GrowDiary.Web/Api/Controllers/GrowsApiController.cs` (`Archive`) |
| Lüft-Rhythmus, Feuchte-Ampel | `GrowDiary.Web/Services/CuringSchedule.cs`, `CuringRating.cs` |
| Gläser, Ablesungen, Endpunkte | `GrowDiary.Web/Infrastructure/CuringRepository.cs`, `GrowDiary.Web/Api/Controllers/CuringApiController.cs` |
| Aushärte-Seite, Glas anlegen | `GrowDiary.React/src/pages/CuringPage.tsx`, `src/features/curing/CuringSection.tsx` |
| Trockenraum erkennen | `GrowDiary.Web/Services/DryingWindow.cs` (`StandFor` nennt den Grow), `MoldGuard.cs` |
| Trocknungs-Etappen (Rechnung, Standard, Prüfregeln) | `GrowDiary.Web/Services/TrocknungsPlan.cs`, Schema `Services/Knowledge/Schema/TrockenEtappe.cs` |
| Trocknungs-Etappen lesen/speichern | `GrowDiary.Web/Api/Controllers/ProgrammeApiController.cs` (`GET/POST /api/programme/{id}/trocknung`) |
| Reiter „Trocknung" | `GrowDiary.React/src/features/zielwerte/TrocknungReiter.tsx`, `trocknung-plan.ts` |
| Kachel-Ziele beim Trocknen | `GrowDiary.Web/Services/GrowDashboardComposer.cs` (`ApplyDryingTargets`) |
| Kostenrechnung | `GrowDiary.Web/Services/GrowCostService.cs`, `GrowDiary.Web/Api/Controllers/CostsApiController.cs` |
| Archiv, Vergleich, Zeitstrahl | `GrowDiary.React/src/pages/ArchivePage.tsx`, `src/features/grows/phase-timeline.ts` |

## Fallen

- **„Fertig" war die einzige Handlung der ganzen App ohne Rückweg** (beta.45).
  Ein Fehlgriff, und das Glas war aus der Liste, sein Rhythmus gestoppt, nur über
  die Datenbank zurückzuholen. Jetzt gibt es Löschen und Wieder öffnen, und die
  Seite lädt mit `?auchKuerzlichFertige=true`.
- **`finishedAtUtc === null` ist immer falsch** (beta.45). Der Server lässt leere
  Felder ganz weg (`JsonIgnoreCondition.WhenWritingNull`), es kommt `undefined`
  an — „Wieder öffnen" hätte an jedem offenen Glas gehangen. Deshalb `== null`.
- **Die Tageszählung verglich UTC-Daten** (beta.45): zwischen Mitternacht und
  2 Uhr war das Glas einen Tag jünger, als es vor einem stand.
  `CuringSchedule.Tage` rechnet in Ortszeit.
- **Kein Weg zur Ernte-Seite ohne Handmessung** (beta.44). Der Knopf hing an der
  letzten Handmessung statt an der gerechneten Phase — wer Sensoren arbeiten
  ließ, sah ihn nie. `canHarvest` prüft jetzt `Flower`/`Finish`/`Dry`.
- **Das Archiv war leer** (beta.50). Der Demo-Säer fälschte nur die
  Home-Assistant-Seite; Ertrag und Kostenrechnung waren auf jedem frischen
  Rechner unsichtbar.
- **Im Zeitstrahl stand „TROCKNE…" statt „Trocknen 10 T"** — die Zahl fiel beim
  Kürzen weg. Seither steht sie vorn (`balkenText`).
- **Reservoir-Alarme liefen während der Trocknung weiter** (beta.18): die
  pH-Sonde liegt in Luft und hätte durch die kritischen Trockentage alarmiert.
