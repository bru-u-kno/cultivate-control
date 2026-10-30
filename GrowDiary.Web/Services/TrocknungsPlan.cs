using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Services.Knowledge.Schema;

namespace GrowDiary.Web.Services;

/// <summary>
/// Der Plan der Trocknung: welche Etappe gilt am wievielten Tag, und welches Zielband die Kacheln zeigen.
/// </summary>
/// <remarks>
/// <para><b>Der Anlass (30.10.2026).</b> Das Trocknungs-Klima stand als feste Zahlen in
/// <c>MoldGuard</c> (18–20 °C, 55–60 %) für alle Tage gleich. Gewollt sind Etappen — erst milder,
/// dann trockener — die jeder frei ändern kann. Sie gehören ins Programm, wie die Wochenwerte.</para>
///
/// <para><b>Rechnung ohne Oberfläche.</b> Alles hier ist rein; Kachel und Seite fragen nur.</para>
/// </remarks>
public static class TrocknungsPlan
{
    public const int MaxEtappen = 8;
    public const int NameMax = 60;
    public const int MaxTage = MoldGuard.DryingWindowDays;

    /// <summary>
    /// Zielband der Kachel um den Zielwert: ±1 K bei der Temperatur, ±2 Prozentpunkte bei der
    /// Feuchte (nach oben nie über die Obergrenze der Etappe).
    /// </summary>
    public const double BandTempK = 1;
    public const double BandFeuchtePunkte = 2;

    /// <summary>
    /// Der Standard, solange ein Programm keine eigenen Etappen führt: langsam trocknen
    /// (Anfang milder und feuchter, am Ende kühler und trockener).
    /// </summary>
    public static IReadOnlyList<TrockenEtappe> Standard { get; } =
    [
        new() { Name = "Anfang, langsam", DauerTage = 3, LufttemperaturC = 20, LuftfeuchteZiel = 60, LuftfeuchteMax = 65 },
        new() { Name = "Hauptphase", DauerTage = 4, LufttemperaturC = 19, LuftfeuchteZiel = 58, LuftfeuchteMax = 65 },
        new() { Name = "Ausklang", DauerTage = 3, LufttemperaturC = 18, LuftfeuchteZiel = 55, LuftfeuchteMax = 65 },
    ];

    /// <summary>Die Etappen, die für dieses Programm gelten: seine eigenen oder der Standard.</summary>
    public static IReadOnlyList<TrockenEtappe> Fuer(NutrientProgramDefinition? programm)
        => programm?.Trocknung is { Count: > 0 } eigene ? eigene : Standard;

    public static bool HatEigene(NutrientProgramDefinition? programm) => programm?.Trocknung is { Count: > 0 };

    /// <summary>Die Etappe an einem Trocknungstag (1 = Erntetag + 1).</summary>
    /// <param name="Index">Null-basiert.</param>
    /// <param name="TagInEtappe">1-basiert.</param>
    /// <param name="Gehalten">Der Tag liegt hinter der letzten Etappe: die letzte gilt weiter.</param>
    public sealed record Stand(int Index, TrockenEtappe Etappe, int TagInEtappe, bool Gehalten);

    public static Stand? EtappeAm(IReadOnlyList<TrockenEtappe> etappen, int tag)
    {
        if (etappen.Count == 0 || tag < 1) return null;

        var bis = 0;
        for (var i = 0; i < etappen.Count; i++)
        {
            var dauer = Math.Max(1, etappen[i].DauerTage);
            if (tag <= bis + dauer) return new Stand(i, etappen[i], tag - bis, Gehalten: false);
            bis += dauer;
        }

        // Hinter dem Plan: die letzte Etappe gilt weiter, bis das Trockengewicht eingetragen wird.
        return new Stand(etappen.Count - 1, etappen[^1], tag - (bis - Math.Max(1, etappen[^1].DauerTage)), Gehalten: true);
    }

    /// <summary>Das Zielband der Kachel für eine Etappe.</summary>
    public readonly record struct Band(double TempMin, double TempMax, double FeuchteMin, double FeuchteMax);

    public static Band BandFuer(TrockenEtappe etappe)
    {
        var feuchteMax = Math.Min(etappe.LuftfeuchteZiel + BandFeuchtePunkte, etappe.LuftfeuchteMax);
        return new Band(
            etappe.LufttemperaturC - BandTempK,
            etappe.LufttemperaturC + BandTempK,
            Math.Min(etappe.LuftfeuchteZiel - BandFeuchtePunkte, feuchteMax),
            feuchteMax);
    }

    /// <summary>
    /// Prüft eine Liste von Etappen, bevor sie gespeichert wird — leer heißt: in Ordnung.
    /// Die Meldung ist für die Oberfläche geschrieben.
    /// </summary>
    public static string? Pruefen(IReadOnlyList<TrockenEtappe> etappen)
    {
        if (etappen.Count == 0) return "Mindestens eine Etappe muss bleiben.";
        if (etappen.Count > MaxEtappen) return $"Höchstens {MaxEtappen} Etappen.";

        var summe = 0;
        for (var i = 0; i < etappen.Count; i++)
        {
            var e = etappen[i];
            var nr = $"Etappe {i + 1}";
            var name = (e.Name ?? string.Empty).Trim();
            if (name.Length == 0) return $"{nr}: Der Name fehlt.";
            if (name.Length > NameMax) return $"{nr}: Der Name darf höchstens {NameMax} Zeichen lang sein.";
            if (e.DauerTage < 1 || e.DauerTage > MaxTage) return $"{nr}: Die Dauer muss zwischen 1 und {MaxTage} Tagen liegen.";
            if (!Zahl(e.LufttemperaturC, 10, 35)) return $"{nr}: Die Temperatur muss zwischen 10 und 35 °C liegen.";
            if (!Zahl(e.LuftfeuchteZiel, 30, 80)) return $"{nr}: Die Luftfeuchte muss zwischen 30 und 80 % liegen.";
            if (!Zahl(e.LuftfeuchteMax, 40, 90)) return $"{nr}: Die Obergrenze der Luftfeuchte muss zwischen 40 und 90 % liegen.";
            if (e.LuftfeuchteMax < e.LuftfeuchteZiel)
                return $"{nr}: Die Obergrenze ({Text(e.LuftfeuchteMax)} %) liegt unter dem Ziel ({Text(e.LuftfeuchteZiel)} %).";
            summe += e.DauerTage;
        }

        return summe > MaxTage
            ? $"Zusammen {summe} Tage — das Trocknungsfenster endet {MaxTage} Tage nach der Ernte."
            : null;
    }

    /// <summary>Eine saubere Kopie: Namen beschnitten, Zahlen auf sinnvolle Stellen gerundet.</summary>
    public static List<TrockenEtappe> Bereinigt(IEnumerable<TrockenEtappe> etappen)
        => etappen.Select(e => new TrockenEtappe
        {
            Name = (e.Name ?? string.Empty).Trim(),
            DauerTage = e.DauerTage,
            LufttemperaturC = Math.Round(e.LufttemperaturC, 1),
            LuftfeuchteZiel = Math.Round(e.LuftfeuchteZiel, 1),
            LuftfeuchteMax = Math.Round(e.LuftfeuchteMax, 1),
        }).ToList();

    private static bool Zahl(double wert, double min, double max)
        => !double.IsNaN(wert) && !double.IsInfinity(wert) && wert >= min && wert <= max;

    private static string Text(double wert) => wert.ToString("0.##", AppCulture.German);
}
