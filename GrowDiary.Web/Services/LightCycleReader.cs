using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Liest den gelernten Lichtzyklus — nur lesen, nichts melden.
/// </summary>
/// <remarks>
/// Bewusst getrennt vom <see cref="LightWatchService"/>: der verschickt
/// Nachrichten und haengt damit am Benachrichtigungsdienst, der je Anfrage
/// lebt. Der Live-Bildschirm dagegen ist ein Singleton und braucht nur die
/// Auskunft. Ein Singleton, der einen Scoped-Dienst festhaelt, ist ein Fehler,
/// den .NET beim Start meldet — und das zu Recht.
/// </remarks>
public sealed class LightCycleReader
{
    /// <summary>Ueber so viele Tage wird der Zyklus gelesen.</summary>
    public const int LookbackDays = 5;

    private readonly LightRepository _lights;

    public LightCycleReader(LightRepository lights)
    {
        _lights = lights;
    }

    /// <summary>Der gelernte Zyklus eines Zelts, oder null solange zu wenig vorliegt.</summary>
    public LearnedCycle? CycleFor(int tentId, DateTime nowUtc)
    {
        var seit = nowUtc.AddDays(-LookbackDays);
        var flanken = _lights.GetLightTransitionsByTent(tentId)
            .Where(transition => transition.OccurredAtUtc >= seit)
            .ToList();

        var zone = ZoneFor(tentId);
        return LightCycleLearner.Learn(flanken, utc => zone.GetUtcOffset(utc));
    }

    /// <summary>
    /// Verschiebung von UTC auf die Uhrzeit im Zelt — nur fuer die Anzeige.
    /// </summary>
    /// <remarks>
    /// Aus der Zeitzone des Lichtplans, wenn dort eine steht; sonst die des
    /// Servers. Fuer die DAUER eines Zyklus ist das egal, fuer „aus um 20:00"
    /// nicht.
    /// </remarks>
    public TimeSpan LocalOffset(int tentId) => LocalOffset(tentId, DateTime.UtcNow);

    /// <summary>Der Versatz zu einem bestimmten Zeitpunkt — vor und nach der Zeitumstellung verschieden.</summary>
    public TimeSpan LocalOffset(int tentId, DateTime zeitpunktUtc) => ZoneFor(tentId).GetUtcOffset(zeitpunktUtc);

    private TimeZoneInfo ZoneFor(int tentId)
    {
        var zone = _lights.GetActiveLightScheduleForTent(tentId)?.TimeZoneId;
        if (!string.IsNullOrWhiteSpace(zone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(zone);
            }
            catch (TimeZoneNotFoundException)
            {
                // Eine falsch geschriebene Zone darf die Anzeige nicht stoppen.
            }
        }

        return TimeZoneInfo.Local;
    }
}
