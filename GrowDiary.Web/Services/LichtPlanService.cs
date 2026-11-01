using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Services.Knowledge.Schema;

namespace GrowDiary.Web.Services;

/// <summary>Was die Automatik „Stufe nach Plan" in dieser Minute tut.</summary>
public enum LichtAutoEntscheidung
{
    Nichts,
    Setzen,
    HandErkannt,
}

/// <summary>
/// Fork AI: Lichtplan — welche Lichtphase gilt, welcher Lichtwert der Plan je Woche nennt und welche Lampenstufe dazu passt.
/// </summary>
/// <remarks>
/// <para>Die Phase (Veggie oder Blüte) kommt allein aus dem Grow-Plan; es gibt keinen zweiten Schalter am Zelt.</para>
/// <para>Die Automatik der Stufe ist aus, bis der Nutzer sie einschaltet. Stellt jemand die Stufe von Hand um, schweigt sie bis zur
/// nächsten Planwoche — eine Handstellung (etwa Stufe 3 gegen Wärme) gewinnt immer.</para>
/// </remarks>
public sealed class LichtPlanService
{
    public const string Modul = "licht_kalibrierung";

    private readonly SteuerungRepository _repo;
    private readonly GrowRepository _grows;
    private readonly KnowledgeBaseLoader _wissen;
    private readonly LichtSteuerungService _licht;
    private readonly ILogger<LichtPlanService> _logger;
    private readonly Func<DateTime> _heute;

    public LichtPlanService(
        SteuerungRepository repo,
        GrowRepository grows,
        KnowledgeBaseLoader wissen,
        LichtSteuerungService licht,
        ILogger<LichtPlanService> logger,
        Func<DateTime>? heute = null)
    {
        _repo = repo;
        _grows = grows;
        _wissen = wissen;
        _licht = licht;
        _logger = logger;
        _heute = heute ?? (() => DateTime.Today);
    }

    public LichtKalibrierung Kalibrierung => _repo.GetEinstellungen<LichtKalibrierung>(Modul) ?? new LichtKalibrierung();

    // ------------------------------------------------------------------ Phase und Woche

    /// <summary>„veggie" oder „bluete" aus der Phase des Grows; null bei Trocknung, Reifung oder ohne Grow.</summary>
    public static string? PhaseFuer(GrowStage stufe) => stufe switch
    {
        GrowStage.Seedling or GrowStage.Clone or GrowStage.Veg => "veggie",
        GrowStage.Transition or GrowStage.Flower or GrowStage.Finish => "bluete",
        _ => null,
    };

    /// <summary>Die Phase, die jetzt im Zelt gilt — aus dem aktiven Grow, sonst null.</summary>
    public string? AktuellePhase()
        => _grows.GetActiveGrows().FirstOrDefault() is { } grow ? PhaseFuer(GrowStageResolver.Resolve(grow, _heute())) : null;

    /// <summary>Das Programm, dessen Wochen gezeigt werden, und die gerade laufende Woche (nur bei aktivem Grow).</summary>
    private (NutrientProgramDefinition? Programm, FeedChartColumn? Jetzt) ProgrammUndWoche()
    {
        var programme = _wissen.NutrientPrograms;
        var aktiv = _grows.GetActiveGrows().FirstOrDefault();
        var grow = aktiv ?? _grows.GetArchivedGrows().FirstOrDefault();
        if (grow is null) return (null, null);
        var programm = MischplanService.ProgrammFuerGrow(grow, programme);
        if (programm?.FeedChart is not { Columns.Count: > 0 } chart) return (null, null);
        var jetzt = aktiv is null ? null : MischplanService.SpalteFuer(chart, aktiv, _heute());
        return (programm, jetzt);
    }

    // ------------------------------------------------------------------ Stand

    public LichtPlanStand Stand()
    {
        var k = Kalibrierung;
        var kurve = LichtKennlinie.Kurve(k);
        var (programm, jetzt) = ProgrammUndWoche();
        var wochen = new List<LichtPlanWoche>();
        foreach (var spalte in programm?.FeedChart?.Columns ?? [])
        {
            double? ppfd = spalte.PpfdMin ?? spalte.PpfdMax;
            int? stufe = ppfd is { } p ? LichtKennlinie.StufeFuer(kurve, p) : null;
            double? erreicht = stufe is { } s && kurve.Count > 0 ? kurve[s - 1].Ppfd : null;
            wochen.Add(new LichtPlanWoche(spalte.Id, spalte.Label, spalte.Stage, ppfd, stufe, erreicht, jetzt?.Id == spalte.Id));
        }

        return new LichtPlanStand(k, kurve.Count > 0, kurve, programm?.Name, AktuellePhase(), wochen, Hinweis(k, kurve.Count > 0, jetzt));
    }

    private static string Hinweis(LichtKalibrierung k, bool kalibriert, FeedChartColumn? jetzt)
    {
        if (!k.AutomatikStufe) return "Aus – der Plan zeigt nur einen Vorschlag, geschaltet wird nichts.";
        if (!kalibriert) return "An, aber ohne Kalibrierung kann keine Stufe gewählt werden.";
        if (jetzt is null) return "An – es läuft gerade kein Grow mit Plan, deshalb passiert nichts.";
        if (k.HandBisWoche == jetzt.Id) return "An – du hast die Stufe von Hand verstellt, die Automatik wartet bis zur nächsten Planwoche.";
        return "An – die Stufe wird beim Wechsel der Planwoche gesetzt.";
    }

    // ------------------------------------------------------------------ Speichern

    public (LichtKalibrierung? Gespeichert, Dictionary<string, string> Fehler) Speichern(LichtKalibrierung neu)
    {
        var fehler = LichtKennlinie.Pruefen(neu);
        if (fehler.Count > 0) return (null, fehler);

        var alt = Kalibrierung;
        // Die Merker der Automatik gehören der Automatik, nicht der Oberfläche.
        neu.AutoStufe = alt.AutoStufe;
        neu.AutoWoche = alt.AutoWoche;
        neu.HandBisWoche = alt.HandBisWoche;
        if (neu.AutomatikStufe && !alt.AutomatikStufe)
        {
            // Beim Einschalten gilt die laufende Woche als erledigt: Gewechselt wird erst zur nächsten.
            var (_, jetzt) = ProgrammUndWoche();
            neu.AutoWoche = jetzt?.Id;
            neu.AutoStufe = null;
            neu.HandBisWoche = null;
        }

        if (!neu.AutomatikStufe)
        {
            neu.AutoStufe = null;
            neu.AutoWoche = null;
            neu.HandBisWoche = null;
        }

        _repo.SetEinstellungen(Modul, neu);
        return (neu, fehler);
    }

    // ------------------------------------------------------------------ Automatik

    /// <summary>Die Entscheidung der Automatik — rein, damit sie sich ohne Lampe prüfen lässt.</summary>
    public static (LichtAutoEntscheidung Entscheidung, int? Ziel) Entscheiden(
        LichtKalibrierung k, string? woche, int? vorschlag, int? liveStufe)
    {
        if (!k.AutomatikStufe || woche is null || vorschlag is null) return (LichtAutoEntscheidung.Nichts, null);
        if (k.HandBisWoche == woche) return (LichtAutoEntscheidung.Nichts, null);

        if (k.AutoWoche == woche)
        {
            // Schon gesetzt (oder beim Einschalten übernommen): nur noch auf eine Handstellung achten.
            return k.AutoStufe is { } gesetzt && liveStufe is { } ist && ist != gesetzt
                ? (LichtAutoEntscheidung.HandErkannt, null)
                : (LichtAutoEntscheidung.Nichts, null);
        }

        return (LichtAutoEntscheidung.Setzen, vorschlag);
    }

    /// <summary>Ein Durchgang im Minutentakt (vom Alarm-Wächter gerufen).</summary>
    public async Task AutomatikLaufAsync(CancellationToken ct)
    {
        var k = Kalibrierung;
        if (!k.AutomatikStufe) return;

        var kurve = LichtKennlinie.Kurve(k);
        var (_, jetzt) = ProgrammUndWoche();
        if (jetzt is null || kurve.Count == 0) return;

        double? ppfd = jetzt.PpfdMin ?? jetzt.PpfdMax;
        int? vorschlag = ppfd is { } p ? LichtKennlinie.StufeFuer(kurve, p) : null;

        var live = await _licht.LiveAsync(ct);
        // Zelt ruhend oder Licht bewusst aus: nichts anfassen.
        if (!live.HaErreichbar || live.ControllerOnline == false || live.Modus == AcModi.Aus) return;

        var (entscheidung, ziel) = Entscheiden(k, jetzt.Id, vorschlag, live.Stufe);
        switch (entscheidung)
        {
            case LichtAutoEntscheidung.HandErkannt:
                k.HandBisWoche = jetzt.Id;
                _repo.SetEinstellungen(Modul, k);
                _logger.LogInformation("Licht-Plan: Stufe von Hand verstellt ({Ist}), Automatik wartet bis zur nächsten Planwoche.", live.Stufe);
                break;

            case LichtAutoEntscheidung.Setzen when ziel is { } stufe:
                if (live.Stufe != stufe && !await _licht.BefehlAsync("stufe", null, stufe, ct))
                {
                    _logger.LogWarning("Licht-Plan: Stufe {Stufe} für {Woche} nicht bestätigt, nächster Versuch in einer Minute.", stufe, jetzt.Label);
                    return;
                }

                k.AutoWoche = jetzt.Id;
                k.AutoStufe = stufe;
                _repo.SetEinstellungen(Modul, k);
                _logger.LogInformation("Licht-Plan: {Woche} – Stufe {Stufe} gesetzt (Plan {Ppfd} µmol/m²s).", jetzt.Label, stufe, ppfd);
                break;
        }
    }
}
