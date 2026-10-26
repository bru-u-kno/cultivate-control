using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

public sealed class RecommendationEngine
{
    private readonly CultivationKnowledgeService _knowledgeService;
    private readonly MeasurementSanityService _measurementSanityService;

    public RecommendationEngine(CultivationKnowledgeService knowledgeService, MeasurementSanityService measurementSanityService)
    {
        _knowledgeService = knowledgeService;
        _measurementSanityService = measurementSanityService;
    }

    public IReadOnlyList<RecommendationCard> Evaluate(GrowRun grow, Measurement? current, Measurement? previous, DateTime? lastSolutionChangeAt)
    {
        var cards = new List<RecommendationCard>();

        if (current is null)
        {
            cards.Add(new RecommendationCard
            {
                Severity = Kartenschwere.Hinweis,
                Title = "Noch keine Messung vorhanden",
                Message = "Sobald du eine erste Messung anlegst, erscheinen hier systembezogene Empfehlungen."
            });

            return cards;
        }

        var program = _knowledgeService.MatchProgram(grow.Nutrients);

        cards.AddRange(_measurementSanityService.GetSanityCards(grow, current));

        EvaluateHydro(cards, grow, current, previous, lastSolutionChangeAt, program);

        EvaluateProgramSpecific(cards, grow, current, previous, program);

        if (cards.Count == 0)
        {
            cards.Add(new RecommendationCard
            {
                Severity = Kartenschwere.Gut,
                Title = "Keine akuten Auffälligkeiten",
                Message = "Die letzten Werte sehen stabil aus. Weiter beobachten und Trends im Verlauf vergleichen."
            });
        }

        return cards;
    }

    /// <remarks>
    /// <b>Ohne Grow.</b> Bis zum 02.09.2026 nahm diese Methode einen
    /// <c>GrowRun</c> entgegen und benutzte ihn in ihrem ganzen Rumpf nicht.
    /// Drei Aufrufer reichten ihn treu durch — ein Parameter, der nichts tut,
    /// erweckt den Eindruck, die Antwort haenge vom Grow ab.
    /// </remarks>
    public IReadOnlyList<RecommendationCard> BuildCardsFromDiagnostics(
        IReadOnlyList<GrowDeviation> deviations,
        IReadOnlyList<TreatmentRecommendationDto> treatmentRecommendations)
    {
        if (deviations.Count == 0)
        {
            return new[]
            {
                Success(
                    "Keine strukturierten Deviations",
                    "Die aktuellen Hydro-Diagnosen zeigen keine kritischen oder warnenden Abweichungen.")
            };
        }

        var recommendationsByDeviation = treatmentRecommendations
            .GroupBy(recommendation => recommendation.DeviationStableKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var cards = new List<RecommendationCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deviation in deviations)
        {
            if (!seen.Add(deviation.StableKey))
            {
                continue;
            }

            recommendationsByDeviation.TryGetValue(deviation.StableKey, out var relatedRecommendations);
            var primaryRecommendation = relatedRecommendations?
                .FirstOrDefault(recommendation =>
                    !string.IsNullOrWhiteSpace(recommendation.TreatmentName) ||
                    !string.IsNullOrWhiteSpace(recommendation.SopTitle));
            var actionName = primaryRecommendation?.TreatmentName ?? primaryRecommendation?.SopTitle;
            var message = string.IsNullOrWhiteSpace(actionName)
                ? deviation.Message
                : $"{deviation.Message} Handlungsvorschlag: {actionName}.";

            cards.Add(new RecommendationCard
            {
                Severity = ToCardSeverity(deviation.Severity),
                Title = $"{deviation.Metric}: {ToGermanSeverityLabel(deviation.Severity)}",
                Message = message
            });
        }

        return cards;
    }

    private static void EvaluateHydro(List<RecommendationCard> cards, GrowRun grow, Measurement current, Measurement? previous, DateTime? lastSolutionChangeAt, NutrientProgram? program)
    {
        var (targetMinEc, targetMaxEc, targetLabel) = GetHydroEcTarget(grow, current.Stage, current.TakenAt, program);

        if (current.ReservoirEc is { } ec)
        {
            if (ec > targetMaxEc + 0.30)
            {
                cards.Add(Critical(
                    "EC deutlich zu hoch für die aktuelle Phase",
                    $"Der Reservoir-EC liegt bei {ec:0.00}. Für {targetLabel} liegt der grobe Zielkorridor eher bei {targetMinEc:0.0}–{targetMaxEc:0.0} mS/cm. Das spricht für eine zu starke Lösung oder für Abstoßung. Addback entschärfen oder die Lösung vorsichtig mit RO/VE-Wasser verdünnen."));
            }
            else if (ec > targetMaxEc + 0.10)
            {
                cards.Add(Warning(
                    "EC oberhalb des Zielkorridors",
                    $"Der Reservoir-EC liegt bei {ec:0.00}. Für {targetLabel} wäre eher {targetMinEc:0.0}–{targetMaxEc:0.0} mS/cm passend. Beobachte Blattbild, Wasseraufnahme und pH-Drift. Wenn die Pflanze nicht sichtbar hungrig ist, den nächsten Addback leicht schwächer ansetzen."));
            }
            else if (ec < targetMinEc - 0.15)
            {
                cards.Add(Warning(
                    "EC eher zu niedrig für die aktuelle Phase",
                    $"Der Reservoir-EC liegt bei {ec:0.00}. Für {targetLabel} liegt der grobe Zielbereich eher bei {targetMinEc:0.0}–{targetMaxEc:0.0} mS/cm. Das kann bewusst funktionieren, falls du die Pflanze leicht hungrig fahren willst. Prüfe aber Wuchs, Farbe und Geschwindigkeit."));
            }

            if (program?.Key == "athena" && current.Stage == GrowStage.Veg && ec >= 2.0)
            {
                cards.Add(Critical(
                    "Athena-RDWC: Veg-EC sehr hoch",
                    $"Ein EC von {ec:0.00} in Veg ist für RDWC mit Athena Blended meist deutlich zu aggressiv. Die SKX-/RDWC-Protokolle liegen in Veg deutlich niedriger und lesen Addbacks trendbasiert statt blind hoch zu fahren."));
            }
            else if (program?.Key == "athena" && current.Stage == GrowStage.Veg && ec > 1.4)
            {
                cards.Add(Warning(
                    "Athena-RDWC: Veg eher aggressiv gefahren",
                    $"Mit {ec:0.00} EC liegt dein Veg-Wert bereits über dem typischen RDWC-Korridor. Prüfe Wasseraufnahme, pH-Drift und Blattspannung, bevor du weiter anziehst."));
            }
            else if (current.Stage == GrowStage.Veg && ec >= 2.0 && (program?.Key is null or not "hydro-research-vbx"))
            {
                cards.Add(Critical(
                    "Veg-EC für rezirkulierendes Hydro sehr hoch",
                    $"Ein EC von {ec:0.00} in Veg ist für die meisten rezirkulierenden Hydro-Runs aggressiv. Ausnahme: Programme wie Hydroponic Research VBX fahren bewusst höher. In RDWC/DWC mit Athena- oder konservativen Profilen wäre das eher ein Warnwert."));
            }
        }

        if (current.ReservoirPh is { } ph)
        {
            // Dieselben Konstanten wie im Abweichungs-Analyzer und im Trend-Wächter. Vorher
            // standen die Zahlen hier ein drittes Mal als Literale — genau so überlebt ein
            // Fix an einer Stelle den nächsten an der anderen nicht.
            if (ph < DeviationAnalyzerService.PhCriticalMin || ph > DeviationAnalyzerService.PhCriticalMax)
            {
                cards.Add(Critical(
                    "pH außerhalb der Sicherheitszone",
                    $"Der Reservoir-pH liegt bei {ph:0.00}. In RDWC gilt 5,8–6,2 als Arbeitsbereich; unter 5,5 oder über 6,5 solltest du korrigieren und das System auf Ursache prüfen."));
            }
            else if (ph < DeviationAnalyzerService.PhComfortMin || ph > DeviationAnalyzerService.PhComfortMax)
            {
                var note = current.Stage is GrowStage.Flower or GrowStage.Finish
                    ? "In der späten Blüte darf der pH etwas natürlicher arbeiten, solange er in der Safe-Zone bleibt."
                    : "In Veg und früher Entwicklung sollte der pH näher am Idealbereich bleiben.";

                cards.Add(Warning(
                    "pH leicht außerhalb des Zielbereichs",
                    $"Der Reservoir-pH liegt bei {ph:0.00}. Zielbereich für RDWC/Hydro ist meist 5,8–6,2. {note} Beobachte den Trend statt hektisch nachzuregeln."));
            }
        }

        if (current.ReservoirWaterTempC is { } waterTemp)
        {
            if (waterTemp >= 24.0)
            {
                cards.Add(Critical(
                    "Wassertemperatur kritisch",
                    $"Die Nährlösung liegt bei {waterTemp:0.0} °C. Hohe Wassertemperaturen senken den Sauerstoffgehalt stark und erhöhen das Risiko für Wurzelprobleme deutlich."));
            }
            else if (waterTemp >= 22.0)
            {
                cards.Add(Warning(
                    "Wassertemperatur erhöht",
                    $"Die Nährlösung liegt bei {waterTemp:0.0} °C. Über 21–22 °C wird RDWC deutlich riskanter. Kühlung, Umlauf und Sauerstoffversorgung im Auge behalten."));
            }
            else if (waterTemp < 18.0)
            {
                cards.Add(Warning(
                    "Wassertemperatur sehr kühl",
                    $"Die Nährlösung liegt bei {waterTemp:0.0} °C. Unter 18 °C kann die Nährstoffaufnahme zäher werden; bei frischen Transplants ist das besonders ungünstig."));
            }
            else if (waterTemp < 19.0)
            {
                cards.Add(Info(
                    "Wassertemperatur an der unteren Grenze",
                    $"Die Nährlösung liegt bei {waterTemp:0.0} °C. Das ist noch gut nutzbar, aber kühler als der oft genannte 19–20-°C-Bereich."));
            }
        }

        if (current.DissolvedOxygenMgL is { } dissolvedOxygen)
        {
            // Dieselbe SOP-Schwelle wie Diagnose und Protokoll — nicht abgetippt.
            if (dissolvedOxygen < DeviationAnalyzerService.DoActionThreshold)
            {
                cards.Add(Critical(
                    "Gelöster Sauerstoff zu niedrig",
                    $"Der DO-Wert liegt bei {dissolvedOxygen:0.0} mg/L. Prüfe Luftpumpe, Luftsteine, Umwälzung und Wassertemperatur sofort."));
            }
            else if (dissolvedOxygen < 7.0)
            {
                cards.Add(Warning(
                    "Gelöster Sauerstoff unter Ideal",
                    $"Der DO-Wert liegt bei {dissolvedOxygen:0.0} mg/L. In RDWC werden meist mindestens 7 mg/L angestrebt, besser mehr."));
            }
            else if (dissolvedOxygen < 7.5)
            {
                cards.Add(Info(
                    "DO noch okay, aber nicht üppig",
                    $"Der DO-Wert liegt bei {dissolvedOxygen:0.0} mg/L. Das ist nutzbar, aber Temperatur und Belüftung sollten sauber sitzen."));
            }
        }

        if (current.OrpMv is { } orp)
        {
            if (orp < 300)
            {
                cards.Add(Critical(
                    "ORP im anaeroben Risikobereich",
                    $"Der ORP-Wert liegt bei {orp:0} mV. Das erhöht das Risiko für Biofilm, Geruch und Wurzelprobleme. Wasserqualität, Hygiene und Sauerstoffversorgung prüfen."));
            }
            else if (orp < 350)
            {
                cards.Add(Warning(
                    "ORP eher niedrig",
                    $"Der ORP-Wert liegt bei {orp:0} mV. Für sauberes RDWC wird oft etwa 350–450 mV angepeilt. Bei Athena/Cleanse eher klein nachsteuern und nach 24–48 Stunden erneut messen."));
            }
            else if (orp <= 450)
            {
                cards.Add(Success(
                    "ORP im sauberen Arbeitsfenster",
                    $"Der ORP-Wert liegt bei {orp:0} mV. Das passt gut zum häufig genannten sauberen RDWC-Korridor von etwa 350–450 mV."));
            }
            else if (orp <= 500)
            {
                cards.Add(Warning(
                    "ORP recht hoch",
                    $"Der ORP-Wert liegt bei {orp:0} mV. Das kann direkt nach einer Oxidator-/Cleanse-Korrektur vorkommen, sollte aber nicht dauerhaft steigen."));
            }
            else
            {
                cards.Add(Critical(
                    "ORP-Schock möglich",
                    $"Der ORP-Wert liegt bei {orp:0} mV. Das ist stark oxidativ und kann Richtung ORP-Schock gehen. Überdosierte Oxidationsmittel reduzieren und die Pflanzen eng beobachten."));
            }
        }

        if (current.ReservoirWaterTempC is { } combinedTemp && current.DissolvedOxygenMgL is { } combinedDo)
        {
            if (combinedTemp >= 22.0 && combinedDo < 7.0)
            {
                cards.Add(Critical(
                    "Root-Zone-Risiko erhöht",
                    $"Wassertemperatur ({combinedTemp:0.0} °C) und DO ({combinedDo:0.0} mg/L) sind zusammen ungünstig. Genau diese Kombination begünstigt Stress und Wurzelprobleme im RDWC/DWC."));
            }
        }

        if (lastSolutionChangeAt.HasValue)
        {
            var daysSinceChange = (current.TakenAt.Date - lastSolutionChangeAt.Value.Date).TotalDays;
            if (daysSinceChange >= 10)
            {
                cards.Add(Critical(
                    "Lösungswechsel deutlich überfällig",
                    $"Seit dem letzten dokumentierten Lösungswechsel sind {daysSinceChange:0} Tage vergangen. In RDWC-Protokollen ist das ein klarer Kontrollpunkt."));
            }
            else if (daysSinceChange >= 7)
            {
                cards.Add(Warning(
                    "Wasserwechsel fällig",
                    $"Seit dem letzten dokumentierten Lösungswechsel sind {daysSinceChange:0} Tage vergangen. Ein wöchentlicher Reset ist in vielen RDWC-Protokollen der saubere Rhythmus."));
            }
            else if (current.Stage == GrowStage.Transition && daysSinceChange >= 5)
            {
                cards.Add(Info(
                    "Vor Stretch/Blüte an frischen Tank denken",
                    $"Seit dem letzten Lösungswechsel sind {daysSinceChange:0} Tage vergangen. Rund um Stretch und Blüteumstellung wird oft ein frischer Tank bevorzugt."));
            }
        }

        if (previous is null)
        {
            return;
        }

        var hasEcPair = current.ReservoirEc is not null && previous.ReservoirEc is not null;
        var hasPhPair = current.ReservoirPh is not null && previous.ReservoirPh is not null;

        var ecDelta = hasEcPair ? current.ReservoirEc!.Value - previous.ReservoirEc!.Value : 0.0;
        var phDelta = hasPhPair ? current.ReservoirPh!.Value - previous.ReservoirPh!.Value : 0.0;

        var litersDelta = (current.ReservoirLevelLiters, previous.ReservoirLevelLiters) switch
        {
            ({ } a, { } b) => a - b,
            _ => double.NaN
        };
        var cmDelta = (current.ReservoirLevelCm, previous.ReservoirLevelCm) switch
        {
            ({ } a, { } b) => a - b,
            _ => double.NaN
        };

        var waterDropped = (!double.IsNaN(litersDelta) && litersDelta < -0.1) || (!double.IsNaN(cmDelta) && cmDelta < -0.1);
        var waterHardDropped = (!double.IsNaN(litersDelta) && litersDelta <= -3.0) || (!double.IsNaN(cmDelta) && cmDelta <= -2.0) || current.TopOffLiters is >= 3.0;
        var waterBarelyMoved = (!double.IsNaN(litersDelta) && Math.Abs(litersDelta) < 0.6) || (!double.IsNaN(cmDelta) && Math.Abs(cmDelta) < 0.5) || current.TopOffLiters is > 0 and < 1.0;

        if (hasEcPair && hasPhPair)
        {
            if (waterHardDropped && ecDelta >= 0.05 && phDelta <= -0.10)
            {
                cards.Add(Warning(
                    "Addback-Szenario: konzentrierende Lösung",
                    $"Hoher Wasserverbrauch, EC {ecDelta:+0.00;-0.00} und pH {phDelta:+0.00;-0.00}. Das passt zum Muster 'mehr Wasser als Nährstoffe aufgenommen'. Empfehlung: nächsten Addback um ca. 0,2–0,3 EC schwächer fahren und pH wieder Richtung 6,0 einordnen."));
            }
            else if (ecDelta >= 0.05 && phDelta <= -0.10)
            {
                cards.Add(Warning(
                    "Wasserverbrauch hoch, Lösung wird konzentrierter",
                    $"Seit der letzten Messung steigt der EC um {ecDelta:+0.00;-0.00} und der pH fällt um {Math.Abs(phDelta):0.00}. Das passt zu dem Addback-Szenario 'mehr Wasser als Nährstoffe aufgenommen'. Empfehlung: erst mit RO/VE-Wasser oder schwächerem Addback gegensteuern und den pH wieder Richtung 6,0 bringen."));
            }

            if (waterBarelyMoved && ecDelta <= -0.05 && phDelta >= 0.10)
            {
                cards.Add(Info(
                    "Addback-Szenario: Pflanze zieht aktiv Nährstoffe",
                    $"Bei eher geringem Wasserverbrauch fällt der EC um {Math.Abs(ecDelta):0.00} und der pH steigt um {phDelta:0.00}. Das passt zu dem Muster 'mehr Nährstoffaufnahme als Wasseraufnahme'. Der nächste Addback darf typischerweise um ca. +0,1 bis +0,2 EC stärker sein."));
            }
            else if (ecDelta <= -0.05 && phDelta >= 0.10)
            {
                cards.Add(Info(
                    "Pflanze zieht aktiv Nährstoffe",
                    $"Der EC fällt um {Math.Abs(ecDelta):0.00} und der pH steigt um {phDelta:0.00}. Das passt zu dem Muster 'mehr Nährstoffaufnahme als Wasseraufnahme'. Der nächste Addback darf etwas stärker sein, typischerweise ca. +0,1 bis +0,2 EC."));
            }

            if (ecDelta >= 0.05 && phDelta >= 0.10)
            {
                cards.Add(Critical(
                    "pH und EC steigen gleichzeitig",
                    $"EC ({ecDelta:+0.00;-0.00}) und pH ({phDelta:+0.00;-0.00}) steigen zusammen. Das ist ein klares Stress-/Ungleichgewichts-Signal. Empfehlung: EC eher um rund 0,2 senken, pH regulieren und Systemfehler wie Temperatur, Verdunstung, Luft, Biofilm oder überstarken Addback prüfen."));
            }

            if (Math.Abs(ecDelta) <= 0.04 && phDelta <= -0.10)
            {
                cards.Add(Warning(
                    "Lösung wirkt etwas zu stark",
                    $"Der EC bleibt fast gleich ({ecDelta:+0.00;-0.00}), während der pH fällt ({phDelta:+0.00;-0.00}). Das passt zu der Empfehlung 'Lösung leicht entschärfen'. Den nächsten Addback eher um 0,1–0,2 EC schwächer fahren und pH Richtung 6,0 einordnen."));
            }

            if (ecDelta <= -0.10 && Math.Abs(phDelta) <= 0.08)
            {
                cards.Add(Success(
                    "Leicht hungriger Trend",
                    $"Der EC fällt um {Math.Abs(ecDelta):0.00}, während der pH weitgehend stabil bleibt. Das passt gut dazu, die Lösung eher leicht hungrig zu fahren."));
            }
            else if (Math.Abs(ecDelta) <= 0.04 && Math.Abs(phDelta) <= 0.08)
            {
                cards.Add(Info(
                    "Sehr stabiler Reservoir-Verlauf",
                    $"EC ({ecDelta:+0.00;-0.00}) und pH ({phDelta:+0.00;-0.00}) haben sich kaum bewegt. Das System läuft ruhig. Prüfe nur, ob diese Stabilität auch zu deinem Wachstumsziel passt."));
            }

            if (phDelta <= -0.15 && ecDelta >= 0.00)
            {
                cards.Add(Warning(
                    "pH fällt spürbar ab",
                    $"Der pH fällt um {Math.Abs(phDelta):0.00}, während der EC nicht mitfällt. Prüfe Wurzelzone, Wasserqualität, Mischreihenfolge und ob der Addback zu konzentriert oder chemisch unausgewogen war."));
            }

            if (Math.Abs(phDelta) >= 0.35)
            {
                cards.Add(Warning(
                    "pH driftet stark zwischen zwei Messungen",
                    $"Die pH-Verschiebung von {phDelta:+0.00;-0.00} ist für ein laufendes RDWC-System deutlich. Wenn das wiederholt vorkommt, ist ein voller Wechsel statt ständiger Pufferkorrektur oft sauberer."));
            }
        }

        if (hasEcPair)
        {
            if (waterDropped && ecDelta > 0.05)
            {
                cards.Add(Warning(
                    "EC steigt während Wasser sinkt",
                    $"Der Wasserstand ist gefallen und der EC steigt um {ecDelta:+0.00;-0.00}. Typisch bedeutet das: Die Pflanze trinkt relativ mehr Wasser als Nährstoffe. Mische den nächsten Addback etwas schwächer, meist rund 0,1 EC niedriger."));
            }

            if (waterDropped && ecDelta < -0.05)
            {
                cards.Add(Info(
                    "EC fällt während Wasser sinkt",
                    $"Der Wasserstand ist gefallen und der EC fällt um {Math.Abs(ecDelta):0.00}. Das spricht dafür, dass die Pflanze Nährstoffe zügig aufnimmt. Der nächste Addback darf etwas stärker sein, zum Beispiel rund +0,1 EC."));
            }

            if (!waterDropped && ecDelta > 0.08)
            {
                cards.Add(Info(
                    "EC steigt ohne klar dokumentierten Wasserverlust",
                    $"Der EC steigt um {ecDelta:+0.00;-0.00}, ohne dass ein deutlicher Pegelabfall dokumentiert ist. Prüfe Messgenauigkeit, Top-Off-Protokoll und ob das System eventuell schon konzentrierter zurückgemischt wurde."));
            }
        }

        if (waterHardDropped && current.TopOffLiters is { } topOffLiters)
        {
            cards.Add(Info(
                "Deutlicher Tagesverbrauch dokumentiert",
                $"Für diese Messung sind {topOffLiters:0.0} L Top-Off dokumentiert. In der Hydro-Hochphase kann dieser Verbrauch normal sein; wichtig ist dann ein sauber geführter Addback und tägliche Kontrolle von EC, pH und Wasserstand."));
        }
        else if (waterBarelyMoved && hasEcPair && ecDelta < -0.05)
        {
            cards.Add(Info(
                "Nährstoffzug ohne großen Wasserverlust",
                $"Der Wasserstand hat sich kaum bewegt, aber der EC fällt. Das spricht eher für aktive Nährstoffaufnahme als für reinen Wasserverbrauch. Prüfe, ob das Wachstum sichtbar dazu passt."));
        }

        if (current.AddbackEc is { } addbackEc && current.ReservoirEc is { } systemEc)
        {
            var addbackDelta = addbackEc - systemEc;
            if (addbackDelta > 0.60)
            {
                cards.Add(Warning(
                    "Addback deutlich stärker als das System",
                    $"Der dokumentierte Addback-EC liegt {addbackDelta:+0.00;-0.00} über dem aktuellen System-EC. Addbacks besser in kleinen Schritten fahren, damit das Reservoir nicht zu stark springt."));
            }
            else if (addbackDelta < -0.30)
            {
                cards.Add(Info(
                    "Addback deutlich schwächer als das System",
                    $"Der dokumentierte Addback-EC liegt {Math.Abs(addbackDelta):0.00} unter dem aktuellen System-EC. Das kann bewusst zum Abschwächen passen, sollte aber mit dem EC-Trend zusammen Sinn ergeben."));
            }
        }

        if (current.TopOffLiters is null && (current.ReservoirLevelLiters is not null || current.ReservoirLevelCm is not null))
        {
            cards.Add(Info(
                "Top-Off noch nicht dokumentiert",
                "Wenn du Wasserstand verfolgst, ist Top-Off als eigener Wert extrem hilfreich. Damit werden Verbrauch, Addback und spätere Empfehlungen deutlich präziser."));
        }
    }

    private static void EvaluateProgramSpecific(List<RecommendationCard> cards, GrowRun grow, Measurement current, Measurement? previous, NutrientProgram? program)
    {
        if (program is null)
        {
            return;
        }

        switch (program.Key)
        {
            case "hydro-research-vbx":
                var referenceEc = GetVbxTarget(current.Stage, grow.StartDate, current.TakenAt);
                // Immer das Reservoir: GrowthProfile.IsHydro ist "=> true", der
                // andere Zweig (current.IrrigationEc) wurde nie gelesen.
                var measuredEc = current.ReservoirEc;
                if (measuredEc is { } vbxEc)
                {
                    if (vbxEc > referenceEc.Max + 0.20)
                    {
                        cards.Add(Warning(
                            "VBX liegt über dem Chart",
                            $"Für {referenceEc.Label} fährt das offizielle VBX-Chart grob um {referenceEc.Min:0.0}–{referenceEc.Max:0.0} EC. Dein Wert liegt bei {vbxEc:0.00}. Prüfe, ob du absichtlich aggressiver fährst oder ob du schon Richtung Salzstress rutschst."));
                    }
                    else if (vbxEc < referenceEc.Min - 0.20)
                    {
                        cards.Add(Info(
                            "VBX unter dem Chart",
                            $"Für {referenceEc.Label} läge das offizielle VBX-Profil eher bei {referenceEc.Min:0.0}–{referenceEc.Max:0.0} EC. Falls die Pflanzen vital sind, kann das ein bewusst sparsamer Run sein."));
                    }
                }
                break;

            case "athena":
                if (current.OrpMv is { } athenaOrp)
                {
                    if (athenaOrp < 350)
                    {
                        cards.Add(Warning(
                            "Athena RDWC: Cleanse-/ORP-Fenster eher niedrig",
                            $"Mit {athenaOrp:0} mV liegt das System unter dem häufig genannten sauberen Fenster. Falls Geruch, Biofilm oder Trägheit dazukommen, Cleanse/HOCl klein nachsteuern und 24–48 Stunden später erneut messen."));
                    }
                    else if (athenaOrp > 500)
                    {
                        cards.Add(Critical(
                            "Athena RDWC: ORP-Schock-Risiko",
                            $"Mit {athenaOrp:0} mV ist das Wasser sehr oxidativ. Überdosierte Cleanse-/HOCl-Gaben können stressähnliche Mangelbilder erzeugen. Oxidator reduzieren und die Pflanzen eng beobachten."));
                    }
                }

                if (current.Stage == GrowStage.Veg && current.ReservoirEc is >= 1.8)
                {
                    cards.Add(Warning(
                        "Athena + RDWC bewusst konservativ fahren",
                        "Athena-Blended-Informationen aus dem Coco/Fertigation-Bereich sind oft deutlich aggressiver als konservative RDWC-Profile. In rezirkulierenden Systemen besser Trend lesen als blind hohe Input-ECs kopieren."));
                }
                break;

            case "canna-aqua":
                if (current.ReservoirPh is { } cannaAquaPh)
                {
                    if (cannaAquaPh < 5.2 || cannaAquaPh > 6.2)
                    {
                        cards.Add(Critical(
                            "Canna Aqua: pH außerhalb des Systems-Fensters",
                            $"Der Reservoir-pH liegt bei {cannaAquaPh:0.00}. Canna Aqua arbeitet mit einem engeren Fenster von 5,2–6,2, Optimal 5,5–5,8. Außerhalb dieses Bereichs leidet die Nährstoffverfügbarkeit deutlich."));
                    }
                    else if (cannaAquaPh < 5.5 || cannaAquaPh > 5.8)
                    {
                        cards.Add(Info(
                            "Canna Aqua: leicht außerhalb des Optimalfensters",
                            $"Der Reservoir-pH liegt bei {cannaAquaPh:0.00}. Canna Aqua zielt auf 5,5–5,8 als Optimum. Im akzeptablen Bereich, aber ein leichtes Nachregeln verbessert die Aufnahme."));
                    }
                }

                if (current.ReservoirEc is { } cannaAquaEc)
                {
                    var (cannaMin, cannaMax, cannaLabel) = current.Stage switch
                    {
                        GrowStage.Seedling or GrowStage.Clone => (0.4, 0.8, "Bewurzelung"),
                        GrowStage.Veg => (1.0, 1.4, "Veg"),
                        GrowStage.Transition => (1.4, 1.6, "Transition"),
                        GrowStage.Flower => (1.6, 2.0, "Blüte"),
                        GrowStage.Finish => (0.5, 0.8, "Flush"),
                        _ => (1.0, 1.8, current.Stage.ToString())
                    };
                    if (cannaAquaEc > cannaMax + 0.25)
                    {
                        cards.Add(Warning(
                            "Canna Aqua: EC über dem Stufenkorridor",
                            $"Der Reservoir-EC liegt bei {cannaAquaEc:0.00}. Für {cannaLabel} wäre laut Canna-Aqua-Schema eher {cannaMin:0.0}–{cannaMax:0.0} passend. Prüfe Addback-Stärke und Pflanzenreaktion."));
                    }
                    else if (cannaAquaEc < cannaMin - 0.20)
                    {
                        cards.Add(Info(
                            "Canna Aqua: EC unter dem Stufenkorridor",
                            $"Der Reservoir-EC liegt bei {cannaAquaEc:0.00}. Für {cannaLabel} liegt der grobe Canna-Aqua-Korridor eher bei {cannaMin:0.0}–{cannaMax:0.0}. Falls die Pflanze vital ist, kann das bewusst sein."));
                    }
                }
                break;

        }
    }

    private static (double Min, double Max, string Label) GetHydroEcTarget(GrowRun grow, GrowStage stage, DateTime measurementAt, NutrientProgram? program)
    {
        if (program?.Key == "hydro-research-vbx")
        {
            return GetVbxTarget(stage, grow.StartDate, measurementAt);
        }

        if (program?.Key == "athena")
        {
            return GetAthenaRdwcTarget(grow, stage, measurementAt);
        }

        if (program?.Key == "canna-aqua")
        {
            return stage switch
            {
                GrowStage.Seedling or GrowStage.Clone => (0.4, 0.8, "Bewurzelung (Canna Aqua)"),
                GrowStage.Veg => (1.0, 1.4, "Veg (Canna Aqua)"),
                GrowStage.Transition => (1.4, 1.6, "Transition (Canna Aqua)"),
                GrowStage.Flower => (1.6, 2.0, "Blüte (Canna Aqua)"),
                GrowStage.Finish => (0.5, 0.8, "Flush (Canna Aqua)"),
                _ => (1.0, 1.8, stage.ToString())
            };
        }

        var runDays = Math.Max(0, (measurementAt.Date - grow.StartDate.Date).Days);

        double min;
        double max;
        string label;

        switch (stage)
        {
            case GrowStage.Seedling:
            case GrowStage.Clone:
                min = 0.2;
                max = 0.5;
                label = "Keimling / Clone";
                break;

            case GrowStage.Veg:
                if (runDays < 14)
                {
                    min = 0.4;
                    max = 0.8;
                    label = "frühe Veg";
                }
                else if (runDays < 28)
                {
                    min = 0.6;
                    max = 1.0;
                    label = "mittlere Veg";
                }
                else
                {
                    min = 0.8;
                    max = 1.2;
                    label = "späte Veg";
                }
                break;

            case GrowStage.Transition:
                min = 1.0;
                max = 1.3;
                label = "Transition / Stretch";
                break;

            case GrowStage.Flower:
                min = 1.2;
                max = 1.6;
                label = "Blüte";
                break;

            case GrowStage.Finish:
                min = 0.8;
                max = 1.2;
                label = "Flush / Fade";
                break;

            default:
                min = 0.8;
                max = 1.2;
                label = stage.ToString();
                break;
        }

        var factor = grow.HydroStyle == HydroStyle.DWC ? 1.30 : 1.00;
        return (Math.Round(min * factor, 2), Math.Round(max * factor, 2), label + (grow.HydroStyle == HydroStyle.DWC ? " in DWC" : " in RDWC/Hydro"));
    }

    private static (double Min, double Max, string Label) GetAthenaRdwcTarget(GrowRun grow, GrowStage stage, DateTime measurementAt)
    {
        var runDays = Math.Max(0, (measurementAt.Date - grow.StartDate.Date).Days);

        double min;
        double max;
        string label;

        switch (stage)
        {
            case GrowStage.Seedling:
            case GrowStage.Clone:
                min = 0.2;
                max = 0.4;
                label = "Clone / frühe Bewurzelung";
                break;

            case GrowStage.Veg:
                if (runDays < 10)
                {
                    min = 0.3;
                    max = 0.6;
                    label = "frühe Veg";
                }
                else if (runDays < 24)
                {
                    min = 0.6;
                    max = 0.8;
                    label = "mittlere Veg";
                }
                else
                {
                    min = 0.8;
                    max = 1.2;
                    label = "späte Veg";
                }
                break;

            case GrowStage.Transition:
                min = 1.2;
                max = 1.4;
                label = "Transition / Stretch";
                break;

            case GrowStage.Flower:
                min = 1.4;
                max = 1.6;
                label = "Blüte";
                break;

            case GrowStage.Finish:
                min = 0.4;
                max = 0.6;
                label = "Flush / Fade";
                break;

            default:
                min = 0.8;
                max = 1.2;
                label = stage.ToString();
                break;
        }

        // Growplan Punkt 1 nennt +30 % als Startwert für DWC. Hier stand 1.35 ohne Beleg,
        // während TargetValueService mit 1.3 rechnete — zwei Zahlen für dieselbe Regel.
        var factor = grow.HydroStyle == HydroStyle.DWC ? TargetValueService.DwcEcMultiplier : 1.00;
        return (Math.Round(min * factor, 2), Math.Round(max * factor, 2), label + (grow.HydroStyle == HydroStyle.DWC ? " in DWC" : " in RDWC"));
    }

    private static (double Min, double Max, string Label) GetVbxTarget(GrowStage stage, DateTime startDate, DateTime measurementAt)
    {
        var runDays = Math.Max(0, (measurementAt.Date - startDate.Date).Days);

        return stage switch
        {
            GrowStage.Seedling or GrowStage.Clone => (0.9, 1.1, "Start / Rooting"),
            GrowStage.Veg when runDays < 21 => (1.2, 1.4, "Veg Phase 1"),
            GrowStage.Veg => (1.3, 1.5, "Veg Phase 2"),
            GrowStage.Transition => (1.5, 1.7, "Transition"),
            GrowStage.Flower when runDays < 63 => (1.7, 1.9, "Bloom Phase"),
            GrowStage.Flower => (1.5, 1.7, "Late Bloom"),
            GrowStage.Finish => (0.5, 0.7, "Flush"),
            _ => (1.2, 1.8, stage.ToString())
        };
    }

    private static RecommendationCard Info(string title, string message)
        => new() { Severity = Kartenschwere.Hinweis, Title = title, Message = message };

    private static RecommendationCard Warning(string title, string message)
        => new() { Severity = Kartenschwere.Warnung, Title = title, Message = message };

    private static RecommendationCard Critical(string title, string message)
        => new() { Severity = Kartenschwere.Gefahr, Title = title, Message = message };

    private static RecommendationCard Success(string title, string message)
        => new() { Severity = Kartenschwere.Gut, Title = title, Message = message };

    private static string ToCardSeverity(DeviationSeverity severity)
        => severity switch
        {
            DeviationSeverity.Critical => Kartenschwere.Gefahr,
            DeviationSeverity.Warning => Kartenschwere.Warnung,
            _ => Kartenschwere.Hinweis
        };

    private static string ToGermanSeverityLabel(DeviationSeverity severity)
        => severity switch
        {
            DeviationSeverity.Critical => "kritisch",
            DeviationSeverity.Warning => "Warnung",
            _ => "Info"
        };
}
