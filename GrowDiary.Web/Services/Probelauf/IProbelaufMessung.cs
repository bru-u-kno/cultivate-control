using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Fork AI (A-010): Ein Messpunkt des Zelts samt der Lichtphase in diesem Moment.</summary>
public sealed record ProbelaufMomentaufnahme(ProbelaufMesswerte Werte, bool? TagPhase);

/// <summary>
/// Fork AI (A-010, 07.10.2026): Was der Probelauf vom Zelt messen will — an einer Naht, damit der
/// Dienst ohne Anlage prüfbar ist. Die Fühler werden über die zentrale Zuordnung gelesen.
/// </summary>
/// <remarks>
/// Mit <c>zeltId</c> misst sie das Zelt: die Fühler des Entfeuchters, der diesem Zelt zugewiesen ist. Ohne Zelt (ein Zelt, alte Läufe)
/// gilt wie bisher der Entfeuchter. Hat das Zelt keinen Entfeuchter mit Fühlern, gibt es keine Messung — nie die Werte eines anderen Zeltes.
/// </remarks>
public interface IProbelaufMessung
{
    /// <summary>Die aktuellen Werte; <c>null</c>, wenn Home Assistant nicht antwortet.</summary>
    Task<ProbelaufMomentaufnahme?> JetztAsync(CancellationToken ct, int? zeltId = null);

    /// <summary>Der Verlauf aus Home Assistant, auf Minuten zusammengelegt.</summary>
    Task<IReadOnlyList<ProbelaufMesswerte>> VerlaufAsync(DateTime vonUtc, DateTime bisUtc, CancellationToken ct, int? zeltId = null);

    /// <summary>Die vorgeschlagenen Grenzen aus den Pflanzenzielen (Luftfeuchte max., Temperatur max., VPD-Band).</summary>
    Task<ProbelaufGrenzen> VoreinstellungAsync(CancellationToken ct, int? zeltId = null);

    /// <summary>Der Verlauf des Zelts auf Minuten gelegt, mit der Lichtphase je Minute (Grundlage des Zielabgleichs).</summary>
    Task<IReadOnlyList<ZeltMinute>> ZeltverlaufAsync(DateTime vonUtc, DateTime bisUtc, CancellationToken ct, int? zeltId = null);

    /// <summary>Die Ziele des Plans je Lichtphase.</summary>
    Task<Zielbaender> ZielbaenderAsync(CancellationToken ct, int? zeltId = null);
}

/// <summary>Fork AI (A-010): Wie der Nutzer benachrichtigt wird, wenn das Zurückstellen nicht gelingt.</summary>
public interface IProbelaufMeldung
{
    Task SendenAsync(string titel, string text, CancellationToken ct);
}
