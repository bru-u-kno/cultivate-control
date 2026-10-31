using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Fertig-Erkennung der Trocknung (A-029, Etappe 4): aus den Wägungen einer Probe (oder der ganzen Charge) eine Kurve in
/// Prozent und eine Aussage, ob das Gewicht noch sinkt, sich hält oder das Ziel erreicht ist.
/// </summary>
/// <remarks>
/// <para><b>Bezug der Prozente.</b> Wird die ganze Charge gewogen und die Ernte kennt ihr Frischgewicht, ist das
/// Frischgewicht der Bezug (100 %), und es gibt eine Zielmarke. Sonst — eine Probe, oder das Frischgewicht ist
/// unbekannt — ist die <b>erste Wägung</b> der Startwert (100 %). Das Frischgewicht der Ernte gilt für die ganze Ernte;
/// eine Probe daran zu messen wäre falsch, deshalb gibt es die Zielmarke nur bei der ganzen Charge.</para>
/// <para><b>Faustregeln, ausdrücklich keine Gewissheit:</b> Ziel etwa 22 % des Frischgewichts; „hält sich“, wenn sich das
/// Gewicht an zwei aufeinanderfolgenden Wägetagen um weniger als 1 % geändert hat. Die Entscheidung „fertig“ trifft der
/// Nutzer (Knack-Test am Stiel).</para>
/// </remarks>
public static class FertigErkennung
{
    /// <summary>Faustregel: Restgewicht in Prozent des Frischgewichts, ab dem die Charge als trocken gilt.</summary>
    public const double ZielProzent = 22;

    /// <summary>Faustregel: Eine Änderung unter diesem Anteil zwischen zwei Wägetagen gilt als „hält sich“.</summary>
    public const double PlateauAnteil = 0.01;

    /// <summary>Wie oft gewogen werden soll (Tage). Täglich wiegen soll nicht nötig sein.</summary>
    public const int AbstandTage = 2;

    public enum Stufe
    {
        KeineWaegung,
        ErsteWaegung,
        Sinkt,
        HaeltSich,
        ZielErreicht,
    }

    public sealed record Punkt(int Id, int Tag, double Prozent, double GewichtG, DateTime ZeitpunktUtc);

    public sealed record Ergebnis(
        Stufe Stufe,
        bool BezugFrischgewicht,
        double? ReferenzG,
        IReadOnlyList<Punkt> Punkte,
        double? AenderungPunkte,
        DateTime? NaechsteAm);

    /// <param name="waegungen">Alle Wägungen des Grows (beliebige Reihenfolge).</param>
    /// <param name="frischG">Frischgewicht der Ernte, falls bekannt.</param>
    /// <param name="erntedatum">Erntedatum — der Erntetag selbst zählt als Trocknungstag 1, wie in <see cref="DryingWindow"/>.</param>
    public static Ergebnis Berechne(IReadOnlyList<TrocknungWaegung> waegungen, double? frischG, DateTime erntedatum)
    {
        var geordnet = waegungen.OrderBy(w => w.ZeitpunktUtc).ThenBy(w => w.Id).ToList();
        if (geordnet.Count == 0) return new Ergebnis(Stufe.KeineWaegung, false, null, [], null, null);

        var erste = geordnet[0];
        var bezugFrisch = erste.GanzeCharge && frischG is > 0;
        var referenz = bezugFrisch ? frischG!.Value : erste.GewichtG;

        var punkte = geordnet.Select(w => new Punkt(
            w.Id,
            Math.Max(1, (w.ZeitpunktUtc.ToLocalTime().Date - erntedatum.Date).Days + 1),
            w.GewichtG / referenz * 100,
            w.GewichtG,
            w.ZeitpunktUtc)).ToList();

        // Pro Kalendertag zählt die letzte Wägung: zwei Wägungen am selben Tag zeigen Waagen-Rauschen, keinen Trend.
        var tage = geordnet
            .GroupBy(w => w.ZeitpunktUtc.ToLocalTime().Date)
            .OrderBy(g => g.Key)
            .Select(g => g.Last())
            .ToList();

        var naechste = tage[^1].ZeitpunktUtc.ToLocalTime().Date.AddDays(AbstandTage);
        double? aenderung = tage.Count >= 2 ? (tage[^1].GewichtG - tage[^2].GewichtG) / referenz * 100 : null;

        // Eine Aussage über den Verlauf braucht zwei Wägetage — zwei Wägungen am selben Tag (Doppelklick, Nachwiegen)
        // sind noch keine Messung über die Zeit.
        if (tage.Count == 1) return new Ergebnis(Stufe.ErsteWaegung, bezugFrisch, referenz, punkte, null, naechste);

        var letzterProzent = tage[^1].GewichtG / referenz * 100;
        if (bezugFrisch && letzterProzent <= ZielProzent)
            return new Ergebnis(Stufe.ZielErreicht, true, referenz, punkte, aenderung, naechste);

        var haeltSich = tage.Count >= 3
            && Aenderung(tage[^2], tage[^1]) < PlateauAnteil
            && Aenderung(tage[^3], tage[^2]) < PlateauAnteil;
        return new Ergebnis(haeltSich ? Stufe.HaeltSich : Stufe.Sinkt, bezugFrisch, referenz, punkte, aenderung, naechste);
    }

    // Die 1-%-Regel misst relativ zur VORHERIGEN Wägung (so fällt „es bewegt sich kaum mehr“ unabhängig vom Bezug aus); die
    // angezeigten Prozentpunkte beziehen sich auf den Bezug der Kurve. Zwei Größen für zwei Fragen.
    private static double Aenderung(TrocknungWaegung von, TrocknungWaegung bis)
        => Math.Abs(bis.GewichtG - von.GewichtG) / von.GewichtG;
}
