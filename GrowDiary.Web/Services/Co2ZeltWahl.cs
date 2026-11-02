using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Der einmal getroffene Entscheid, welches Zelt das Bestand-Zelt der CO₂-Steuerung ist (Modul <c>co2</c>, alle bisherigen Kennungen).</summary>
public sealed class Co2BestandStand
{
    public int? ZeltId { get; set; }

    /// <summary>Woran der Fork den Entscheid festgemacht hat — nur zum Nachlesen.</summary>
    public string Grund { get; set; } = "";
}

/// <summary>
/// Legt fest, welches Zelt dem vorhandenen CO₂-Objektsatz (Modul <c>co2</c>) gehört, und hält das Register <see cref="Co2Zelte"/> aktuell.
/// </summary>
/// <remarks>
/// <para><b>Die Regel.</b> Das Bestand-Zelt ist das erste Zelt (kleinste Id) mit „CO₂-Begasung“, sobald es eins gibt. Einmal
/// entschieden, wird es gespeichert und danach nie umgehängt: Die Helfer und Automationen in Home Assistant heißen unverändert
/// weiter, und ein später hinzugekommenes Zelt mit CO₂ bekommt seinen eigenen Satz (<see cref="Co2Modul"/>).</para>
/// <para><b>Nichts bricht.</b> Wird dem Bestand-Zelt das Flag später genommen, bleibt es Bestand — seine Regelung läuft weiter, wie vor
/// der Umstellung, als das Flag die Steuerung gar nicht berührte. Gibt es noch kein Zelt mit CO₂, bleibt das Bestand-Zelt offen
/// (<c>null</c>) und der Dienst arbeitet wie bisher ohne Zeltbezug.</para>
/// </remarks>
public static class Co2ZeltWahl
{
    /// <summary>Unter diesem Namen liegt der Entscheid in den Steuerungs-Einstellungen.</summary>
    public const string Modul = "co2-bestand";

    /// <summary>Die reine Entscheidung — ohne Datenbank.</summary>
    /// <param name="gespeichert">Ein früherer Entscheid; er gilt, solange das Zelt noch existiert.</param>
    /// <param name="vorhandeneZelte">Alle Zelt-Ids (auch archivierte).</param>
    /// <param name="zelteMitCo2">Die aktiven Zelte mit „CO₂-Begasung“, in beliebiger Reihenfolge.</param>
    public static (int? Bestand, IReadOnlyList<int> Zelte, string Grund) Bestimmen(
        Co2BestandStand? gespeichert, IReadOnlyCollection<int> vorhandeneZelte, IReadOnlyCollection<int> zelteMitCo2)
    {
        var mitCo2 = zelteMitCo2.Distinct().OrderBy(i => i).ToList();

        if (gespeichert?.ZeltId is { } alt && vorhandeneZelte.Contains(alt))
        {
            return (alt, mitCo2, gespeichert.Grund);
        }

        if (mitCo2.Count > 0) return (mitCo2[0], mitCo2, "erstes Zelt mit CO₂-Begasung");
        return (null, mitCo2, "noch kein Zelt mit CO₂-Begasung");
    }

    /// <summary>Entscheiden, beim ersten Mal speichern, das Register setzen. Beim Start und nach jeder Änderung an den Zelten aufrufen.</summary>
    public static Co2Zelte.Stand Aktualisieren(TentRepository zelte, SteuerungRepository repo, ILogger? log = null)
    {
        var alle = zelte.GetTents(includeArchived: true);
        var gespeichert = repo.GetEinstellungen<Co2BestandStand>(Modul);
        var (bestand, mitCo2, grund) = Bestimmen(
            gespeichert,
            alle.Select(t => t.Id).ToList(),
            alle.Where(t => t.Status == TentStatus.Active && t.HasCo2Enrichment).Select(t => t.Id).ToList());

        if (bestand is { } b && gespeichert?.ZeltId != b)
        {
            repo.SetEinstellungen(Modul, new Co2BestandStand { ZeltId = b, Grund = grund });
            log?.LogInformation("CO₂-Bestand-Zelt festgelegt: Zelt {Zelt} ({Grund}).", b, grund);
        }

        Co2Zelte.Festlegen(bestand, mitCo2);
        return Co2Zelte.Aktuell;
    }
}
