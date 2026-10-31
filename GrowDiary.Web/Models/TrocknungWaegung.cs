namespace GrowDiary.Web.Models;

/// <summary>
/// Eine Wägung während der Trocknung (A-029, Etappe 4): das Gewicht einer Probe — oder der ganzen Charge — zu einem
/// Zeitpunkt. Aus den Wägungen eines Grows entsteht die Kurve der Fertig-Erkennung.
/// </summary>
public sealed class TrocknungWaegung
{
    public int Id { get; set; }

    /// <summary>Der Grow, dessen Ernte trocknet (die Wägung gehört zur Charge, nicht zum Zelt).</summary>
    public int GrowId { get; set; }

    public DateTime ZeitpunktUtc { get; set; } = DateTime.UtcNow;

    public double GewichtG { get; set; }

    /// <summary>
    /// <c>true</c> = es wurde die ganze Charge gewogen; dann rechnet die Kurve gegen das Frischgewicht der Ernte.
    /// <c>false</c> = eine Probe; dann ist die erste Wägung der Startwert (100 %).
    /// </summary>
    public bool GanzeCharge { get; set; }
}
