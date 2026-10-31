using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Fork AI: Was mit einem Grow geschieht, wenn er abgeschlossen wird — gleich auf welchem Weg (Ernte-Seite,
/// „Archivieren", Status im Formular).
/// </summary>
public static class GrowAbschluss
{
    /// <summary>
    /// Die Pflanzen des Grows stehen danach auf „geerntet". Bis cc.279 setzte den Status nichts: Pflanzen blieben
    /// nach Ernte und Abschluss „aktiv", obwohl sie hingen oder trockneten.
    /// </summary>
    /// <returns>Wie viele Pflanzen umgestellt wurden. Nur aktive Produktionspflanzen — Mutterpflanzen, aussortierte, archivierte oder schon geerntete bleiben.</returns>
    public static int PflanzenAlsGeerntet(SetupRepository setups, int growId, DateTime ende)
    {
        var anzahl = 0;
        // Nur Produktionspflanzen: eine Mutterpflanze, die an einem Grow hängt, wächst nach der Ernte weiter.
        foreach (var pflanze in setups.GetPlantsByGrow(growId).Where(p => p.PlantStatus == PlantStatus.Active && p.PlantRole == PlantRole.Production))
        {
            pflanze.PlantStatus = PlantStatus.Harvested;
            pflanze.EndedAt ??= ende;
            setups.UpdatePlant(pflanze);
            anzahl++;
        }
        return anzahl;
    }

    /// <summary>
    /// Einmalige, idempotente Korrektur beim Start: Pflanzen abgeschlossener Grows, die noch „aktiv" stehen (so entstanden
    /// bis cc.293, als der Abschluss den Status nicht setzte), werden „geerntet", mit dem Enddatum des Grows. Nur Grows mit
    /// Status „Abgeschlossen" — abgebrochene bleiben, wie sie sind.
    /// </summary>
    /// <returns>Wie viele Pflanzen umgestellt wurden. Beim zweiten Start 0.</returns>
    public static int BestandNachziehen(SetupRepository setups, GrowRepository grows)
    {
        var anzahl = 0;
        foreach (var grow in grows.GetAllGrows().Where(g => g.Status == GrowStatus.Completed))
            anzahl += PflanzenAlsGeerntet(setups, grow.Id, grow.EndDate ?? DateTime.Today);
        return anzahl;
    }
}
