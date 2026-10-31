using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services.Knowledge;

namespace GrowDiary.Web.Services;

/// <summary>Eine Reinigung, die nach einem abgeschlossenen Grow empfohlen wird.</summary>
/// <param name="AktiveInstanzId">Läuft die Reinigung schon, steht hier ihre Instanz — der Knopf führt dann dorthin statt eine zweite zu starten.</param>
public sealed record ReinigungNachGrow(
    int GrowId, string GrowName, string? ZeltName, string SopId, string SopName, string Meldung, int? AktiveInstanzId);

/// <summary>
/// Fork AI: Ist ein Grow abgeschlossen und das System leer, empfiehlt der CC die Reinigung des leeren Systems.
/// </summary>
/// <remarks>
/// <para><b>Wann.</b> Der <i>neueste</i> abgeschlossene Grow eines RDWC/DWC-Systems, solange das System frei ist (kein laufender
/// oder geplanter Grow darauf), der Abschluss höchstens <see cref="TageFenster"/> Tage zurückliegt und noch keine Reinigung
/// (die für das leere System oder die Tiefenreinigung) danach beendet wurde. Ältere Grows desselben Systems schweigen:
/// sonst stünde nach einem halben Jahr für jeden eine Mahnung da.</para>
/// <para><b>Welche Reinigung.</b> <see cref="SopId"/>: dieselben Schritte wie die Tiefenreinigung, ohne „Pflanzen umsetzen"
/// und „Pflanzen zurück" — im leeren System gibt es keine. Der Inhalt kommt aus der vorhandenen Quelle, der CC erfindet
/// keine Mittel und keine Zeiten.</para>
/// <para>Wer die Begleitung auf „Experte" gestellt hat, hat sich die Stille bestellt (wie bei den fälligen Routinen).</para>
/// </remarks>
public sealed class ReinigungNachGrowService
{
    public const string SopId = "system-cleaning-empty";

    /// <summary>So lange nach dem Abschluss wird die Reinigung noch empfohlen.</summary>
    public const int TageFenster = 60;

    private readonly KnowledgeBaseLoader _wissen;
    private readonly GrowRepository _grows;
    private readonly SopRepository _instanzen;
    private readonly SopDueService _faellig;

    public ReinigungNachGrowService(KnowledgeBaseLoader wissen, GrowRepository grows, SopRepository instanzen, SopDueService faellig)
    {
        _wissen = wissen;
        _grows = grows;
        _instanzen = instanzen;
        _faellig = faellig;
    }

    public IReadOnlyList<ReinigungNachGrow> Empfohlen(DateTime heute)
    {
        if (_faellig.Stufe == "expert") return [];
        var sop = _wissen.Sops.FirstOrDefault(s => string.Equals(s.Id, SopId, StringComparison.OrdinalIgnoreCase));
        if (sop is null) return [];

        var alle = _grows.GetAllGrows();
        var tents = _grows.GetTents(includeArchived: true).ToDictionary(t => t.Id);
        var ergebnis = new List<ReinigungNachGrow>();

        foreach (var grow in NeuesteJeSystem(alle))
        {
            var instanzen = _instanzen.GetSopInstancesByGrow(grow.Id)
                .Where(i => string.Equals(i.SopId, SopId, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(i.SopId, "system-cleaning-deep", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var fertigNachAbschluss = instanzen.Any(i =>
                i.Status == SopInstanceStatus.Completed && i.CompletedAtUtc is { } fertig && fertig.ToLocalTime().Date >= grow.EndDate!.Value.Date);
            var laufend = instanzen.FirstOrDefault(i => i.Status == SopInstanceStatus.Active);

            var entscheidung = Pruefen(grow, alle, heute, fertigNachAbschluss);
            if (entscheidung is not true) continue;

            var zelt = grow.TentId is { } tentId && tents.TryGetValue(tentId, out var t) ? t.Name : null;
            ergebnis.Add(new ReinigungNachGrow(
                grow.Id, grow.Name, zelt, sop.Id, sop.Name,
                laufend is not null
                    ? $"{sop.Name} läuft."
                    : $"{sop.Name}: nach dem Grow empfohlen{Dauer(sop.EstimatedDurationMinutes)}.",
                laufend?.Id));
        }
        return ergebnis;
    }

    /// <summary>Die neuesten abgeschlossenen Grows — je System einer.</summary>
    private static IEnumerable<GrowRun> NeuesteJeSystem(IReadOnlyList<GrowRun> alle)
        => alle.Where(g => g.Status == GrowStatus.Completed && g.EndDate is not null && g.SystemId is not null)
               .GroupBy(g => g.SystemId!.Value)
               .Select(gruppe => gruppe.OrderByDescending(g => g.EndDate).ThenByDescending(g => g.Id).First());

    /// <summary>
    /// Die reine Regel: ja, wenn das System frei ist, der Abschluss nicht länger als das Fenster zurückliegt und noch keine
    /// Reinigung danach fertig wurde. <c>null</c> kommt nicht vor, der Typ hält die Tür für „unklar" offen.
    /// </summary>
    public static bool? Pruefen(GrowRun grow, IReadOnlyList<GrowRun> alle, DateTime heute, bool reinigungFertig)
    {
        if (grow.EndDate is not { } ende || grow.SystemId is not { } system) return false;
        if (reinigungFertig) return false;
        if ((heute.Date - ende.Date).TotalDays > TageFenster) return false;
        // Ein laufender oder geplanter Grow auf demselben System: es ist nicht leer.
        var belegt = alle.Any(g => g.Id != grow.Id && g.SystemId == system && g.Status is GrowStatus.Running or GrowStatus.Planning);
        return !belegt;
    }

    private static string Dauer(int? minuten)
        => minuten is { } m and > 0
            ? $" (ca. {(m % 60 == 0 ? $"{m / 60}" : (m / 60.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("de-DE")))} Std.)"
            : string.Empty;
}
