using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Welche Zelte eine eigene CO₂-Steuerung haben und welches davon das Bestand-Zelt ist (Modul <see cref="Co2Modul.Basis"/>).
/// </summary>
/// <remarks>
/// Prozessweit wie <see cref="Kennungen.Raum"/>: Katalog, Rollen und Vorlagen fragen danach, ohne eine Datenbank anzufassen.
/// Gesetzt wird der Stand beim Start und nach jeder Änderung an den Zelten (Flag „mit CO₂-Begasung“).
/// </remarks>
public static class Co2Zelte
{
    /// <summary>Ein Stand: Bestand-Zelt (oder null) und alle Zelte mit CO₂ in aufsteigender Reihenfolge.</summary>
    public sealed record Stand(int? BestandZeltId, IReadOnlyList<int> ZeltIds);

    private static volatile Stand _stand = new(null, Array.Empty<int>());

    public static Stand Aktuell => _stand;

    /// <summary>Den Stand setzen. Das Bestand-Zelt gehört immer dazu.</summary>
    public static void Festlegen(int? bestandZeltId, IEnumerable<int> zeltIds)
    {
        var alle = zeltIds.Distinct().OrderBy(i => i).ToList();
        if (bestandZeltId is { } b && !alle.Contains(b)) alle.Insert(0, b);
        _stand = new Stand(bestandZeltId, alle);
    }

    /// <summary>Das Modul eines Zeltes.</summary>
    public static string ModulFuer(int zeltId) => Co2Modul.Fuer(zeltId, _stand.BestandZeltId);

    /// <summary>Die Module aller CO₂-Zelte, das Bestand-Zelt zuerst.</summary>
    public static IReadOnlyList<string> Module
        => _stand.ZeltIds
            .OrderBy(i => i == _stand.BestandZeltId ? 0 : 1).ThenBy(i => i)
            .Select(ModulFuer)
            .ToList();
}
