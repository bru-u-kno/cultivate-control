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
    /// Die Abweichungen eines abgeschlossenen Grows schließen sich mit ihm: „VPD: Abweichung prüfen" gehört zu einem Lauf gegen
    /// seinen Plan, und ein Lauf, den es nicht mehr gibt, hat keine Abweichung mehr. Das Risiko stand sonst unter Aufgaben, bis
    /// jemand es von Hand auf „Erledigt" setzte (30.10.2026: „nicht mehr geprüft", 19-mal seit Wochen).
    /// </summary>
    /// <remarks>
    /// Nur Risiken der Quelle „Abweichung" und ohne Gerät. Pumpen-, Strom-, Verbindungs- und Handmeldungen bleiben: sie
    /// betreffen die Anlage und nicht den Lauf, und ein Grow-Ende macht eine stehende Pumpe nicht heil. Geschlossen wird,
    /// nicht gelöscht — der Befund bleibt mit Datum und Vermerk im Verlauf.
    /// </remarks>
    /// <returns>Wie viele Risiken geschlossen wurden.</returns>
    public static int AbweichungenSchliessen(HardwareRepository hardware, int growId, DateTime jetztUtc)
    {
        var anzahl = 0;
        foreach (var risiko in hardware.GetRiskEventsByGrow(growId).Where(r =>
                     r.Source == RiskEventSource.Deviation && r.HardwareItemId is null
                     && r.Status is RiskEventStatus.Open or RiskEventStatus.Acknowledged))
        {
            hardware.ResolveRiskEvent(risiko.Id, jetztUtc, "Grow abgeschlossen — die Abweichung gehörte zu diesem Lauf.");
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
    public static int BestandNachziehen(SetupRepository setups, GrowRepository grows, HardwareRepository? hardware = null)
    {
        var anzahl = 0;
        foreach (var grow in grows.GetAllGrows().Where(g => g.Status == GrowStatus.Completed))
        {
            anzahl += PflanzenAlsGeerntet(setups, grow.Id, grow.EndDate ?? DateTime.Today);
            if (hardware is not null) anzahl += AbweichungenSchliessen(hardware, grow.Id, DateTime.UtcNow);
        }
        return anzahl;
    }
}
