using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services.Knowledge.Schema;

namespace GrowDiary.Web.Services;

/// <summary>
/// Prüft Änderungen an Wochenwerten, bevor sie gespeichert werden — für jeden
/// Weg, der sie schreibt (Plan eines Grows, Programm ohne Grow).
/// </summary>
/// <remarks>
/// <para><b>Warum eine Stelle.</b> Die Regeln (Bereich je Feld, „von" nicht über
/// „bis", EC-Ziel im Band) standen im Controller des Wochenplans. Der zweite
/// Weg — ein Programm ohne Grow bearbeiten — hätte sie kopiert, und zwei Kopien
/// laufen irgendwann auseinander.</para>
///
/// <para><b>Alles oder nichts.</b> Die Prüfung läuft über die ganze Änderungsliste;
/// ist eine ungültig, wird keine gespeichert. Ein halb gespeicherter Plan wäre
/// schlimmer als ein abgelehnter.</para>
/// </remarks>
public static class WochenwertPruefung
{
    /// <summary>Eine geprüfte Änderung; <c>Wert = null</c> heißt „zurück auf den Planwert".</summary>
    public readonly record struct Geprueft(string SpalteId, string Feld, double? Wert);

    /// <summary>Das Ergebnis: entweder die geprüften Änderungen oder eine Meldung für die Oberfläche.</summary>
    public sealed record Ergebnis(IReadOnlyList<Geprueft> Aenderungen, string? Fehler)
    {
        public bool Gueltig => Fehler is null;
    }

    /// <param name="spalten">Die Spalten des Programms oder Plans, in denen geändert wird.</param>
    /// <param name="programmName">Für die Meldung „Die Woche … gibt es im Programm … nicht".</param>
    /// <param name="planwert">Der Vergleichswert, wenn ein Feld einer Änderung zurück auf den Plan soll.</param>
    /// <param name="bandWandertMit">Nur der Grow-Plan führt das EC-Band mit; ein Programm ohne Plan nicht.</param>
    public static Ergebnis Pruefen(
        IReadOnlyList<FeedChartColumn> spalten,
        string programmName,
        IReadOnlyList<WochenwertAenderung> aenderungen,
        Func<FeedChartColumn, Wochenwertfelder.Feld, double?> planwert,
        bool bandWandertMit)
    {
        if (aenderungen.Count == 0) return Fehler("Es gibt nichts zu speichern.");

        var geprueft = new List<Geprueft>();
        // Was nach dem Speichern in jeder betroffenen Spalte stünde — für die Paar-Prüfung.
        var danach = new Dictionary<(string Spalte, string Feld), double?>();

        foreach (var aenderung in aenderungen)
        {
            var spalte = spalten.FirstOrDefault(s => string.Equals(s.Id, aenderung.SpalteId, StringComparison.OrdinalIgnoreCase));
            if (spalte is null)
                return Fehler($"Die Woche „{aenderung.SpalteId}“ gibt es im Programm {programmName} nicht.");

            if (Wochenwertfelder.Finden(aenderung.Feld) is not { } feld)
                return Fehler($"„{aenderung.Feld}“ lässt sich nicht bearbeiten.");

            if (aenderung.Wert is { } wert
                && (double.IsNaN(wert) || double.IsInfinity(wert) || wert < feld.Min || wert > feld.Max))
                return Fehler($"{spalte.Label}: {feld.Bezeichnung} muss zwischen {Zahl(feld.Min)} und {Zahl(feld.Max)} liegen.");

            danach[(spalte.Id, feld.Name)] = aenderung.Wert ?? planwert(spalte, feld);
            geprueft.Add(new Geprueft(spalte.Id, feld.Name, aenderung.Wert));
        }

        foreach (var spalte in spalten)
        {
            foreach (var feld in Wochenwertfelder.Alle.Where(f => f.Paar is not null))
            {
                var partner = Wochenwertfelder.Finden(feld.Paar!)!;
                var von = danach.TryGetValue((spalte.Id, feld.Name), out var a) ? a : feld.Lesen(spalte);
                var bis = danach.TryGetValue((spalte.Id, partner.Name), out var b) ? b : partner.Lesen(spalte);
                if (von is { } v && bis is { } z && v > z + 1e-9)
                    return Fehler($"{spalte.Label}: {feld.Bezeichnung} ({Zahl(v)}) liegt über {partner.Bezeichnung} ({Zahl(z)}).");
            }

            // Das EC-Ziel hat kein Paar — es gehört ins Band.
            var dieseSpalte = danach.Where(d => d.Key.Spalte == spalte.Id)
                .ToDictionary(d => d.Key.Feld, d => d.Value, StringComparer.OrdinalIgnoreCase);
            if (Wochenwertfelder.EcPruefen(spalte.Label, spalte, dieseSpalte, bandWandertMit) is { } ecFehler)
                return Fehler(ecFehler);
        }

        return new Ergebnis(geprueft, null);
    }

    private static Ergebnis Fehler(string text) => new([], text);

    private static string Zahl(double wert) => wert.ToString("0.##", AppCulture.German);
}
