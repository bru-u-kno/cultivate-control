using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Ist dieses Zelt gerade ein Trockenraum — und der wievielte Tag ist es?
/// </summary>
/// <remarks>
/// Eine Frage, zwei Abnehmer: die Kacheln (Trocknungs-Klimaziele) und die
/// Alarme (Reservoir-Regeln pausieren — die Sonden liegen trocken und messen
/// Unsinn). Deshalb EINE Stelle; zwei Kopien wuerden irgendwann verschieden
/// antworten.
///
/// Trocknung liegt vor, wenn kein Grow mehr laeuft, der letzte in den
/// vergangenen drei Wochen geerntet wurde und noch kein Trockengewicht
/// eingetragen ist. Das Gewicht ist der natuerliche Abschluss: gewogen wird
/// nach dem Trocknen.
/// </remarks>
public static class DryingWindow
{
    /// <summary>Der Trocknungs-Tag (1 = Erntetag + 1), oder null wenn keine Trocknung.</summary>
    public static int? DayFor(GrowRepository? grows, HarvestRepository? harvests, int tentId, DateTime today)
        => StandFor(grows, harvests, tentId, today)?.Tag;

    /// <summary>Was trocknet gerade: der Grow, dessen Ernte hängt, und der wievielte Trocknungstag es ist.</summary>
    public sealed record Stand(GrowRun Grow, int Tag);

    /// <summary>
    /// Wie <see cref="DayFor"/>, nennt aber auch den Grow — dessen Programm führt die Etappen der
    /// Trocknung (<see cref="TrocknungsPlan"/>).
    /// </summary>
    public static Stand? StandFor(GrowRepository? grows, HarvestRepository? harvests, int tentId, DateTime today)
    {
        if (grows is null || harvests is null) return null;

        // Ein Trockenzelt (cc.281): Es trocknet die Ernte, die ihm bei der Ernte zugewiesen wurde — gleich, welcher
        // Grow sie war. Es hat selbst keinen Grow, deshalb gilt hier die Bedingung „kein aktiver Grow“ nicht.
        // Der Trocknungstag zählt ab dem ERNTEDATUM, nicht ab dem Enddatum des Grows: Das Enddatum fehlt bei einem
        // Grow, der anders als über die Ernte abgeschlossen wurde, und bei einer Teilernte läuft der Grow noch.
        foreach (var zugewiesen in harvests.GetDryingInTent(tentId))
        {
            if (grows.GetGrow(zugewiesen.GrowId) is not { } fremder) continue;
            if (TrocknungsTag(today, zugewiesen.HarvestedAt) is { } fremderTag) return new Stand(fremder, fremderTag);
        }

        if (grows.GetActiveGrowsForTent(tentId).Count > 0) return null;

        var geerntet = grows.GetAllGrows()
            .Where(grow => grow.TentId == tentId
                && grow.Status == GrowStatus.Completed
                && grow.EndDate is { } ende
                && (today.Date - ende.Date).TotalDays <= MoldGuard.DryingWindowDays)
            .OrderByDescending(grow => grow.EndDate)
            .FirstOrDefault();
        if (geerntet is null) return null;

        var ernte = harvests.GetForGrow(geerntet.Id);
        if (ernte is null || ernte.DryWeightG is not null) return null;

        // Hängt die Ernte in einem anderen Zelt, ist dieses Zelt (das des Grows) schon frei.
        if (ernte.DryingTentId is { } anderes && anderes != tentId) return null;

        return TrocknungsTag(today, geerntet.EndDate!.Value) is { } tag ? new Stand(geerntet, tag) : null;
    }

    /// <summary>
    /// Der Trocknungstag zu einem Erntedatum — oder <c>null</c>, wenn es keinen gibt.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Anlass (01.09.2026).</b> Hier stand nur die Subtraktion.
    /// Bei einem Erntedatum in der <b>Zukunft</b> kamen 0 oder negative
    /// Trocknungstage heraus, und das Fenster galt trotzdem als offen.</para>
    ///
    /// <para><b>Was das kostet.</b> Im Trocknungsfenster schaltet
    /// <c>GrowDashboardComposer</c> auf Trocknungsziele um — die
    /// Reservoir-Alarme des noch laufenden Grows sind damit stillgelegt. Ein
    /// Vertipper um ein Jahr beim Erntedatum genügt, und auf den Kacheln steht
    /// „Trocknung – Tag 0".</para>
    ///
    /// <para>Eigene Methode, damit die Schranke eine eigene Prüfung bekommt:
    /// über <c>DayFor</c> bräuchte sie zwei Ablagen und einen ganzen Grow.</para>
    /// </remarks>
    public static int? TrocknungsTag(DateTime heute, DateTime geerntetAm)
    {
        var tage = (heute.Date - geerntetAm.Date).Days;
        if (tage < 0) return null;
        if (tage > MoldGuard.DryingWindowDays) return null;
        return tage + 1;
    }

    /// <summary>
    /// Messgroessen, die waehrend der Trocknung nichts mehr bedeuten: das
    /// Reservoir ist abgelassen, die Sonden liegen trocken und melden Unsinn.
    /// </summary>
    public static readonly string[] ReservoirKeys =
    [
        "reservoir-ph", "reservoir-ec", "reservoir-temp", "orp",
        "dissolved-oxygen", "reservoir-level", "reservoir-level-cm",
    ];

    public static bool IsReservoirKey(string metricKey)
        => ReservoirKeys.Contains(metricKey, StringComparer.OrdinalIgnoreCase);
}
