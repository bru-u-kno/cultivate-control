using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Das Register der weiteren Instanzen — prozessweit wie <see cref="Co2Zelte"/>: Katalog, Rollen und Auswahl fragen danach, ohne eine
/// Datenbank anzufassen. Gesetzt wird es beim Start und nach jeder Änderung.
/// </summary>
public static class SteuerungInstanzen
{
    private static volatile IReadOnlyList<SteuerungInstanz> _liste = Array.Empty<SteuerungInstanz>();

    /// <summary>Alle weiteren Instanzen, je Art nach Nummer.</summary>
    public static IReadOnlyList<SteuerungInstanz> Aktuell => _liste;

    public static void Festlegen(IEnumerable<SteuerungInstanz> instanzen)
        => _liste = instanzen.OrderBy(i => i.Art, StringComparer.Ordinal).ThenBy(i => i.Nummer).ToList();

    /// <summary>Die Module der weiteren Instanzen einer Art (leer, solange es keine gibt).</summary>
    public static IReadOnlyList<string> ModuleVon(string art)
        => _liste.Where(i => string.Equals(i.Art, art, StringComparison.Ordinal)).Select(i => i.Modul).ToList();

    /// <summary>Die Module aller weiteren Instanzen.</summary>
    public static IReadOnlyList<string> Module => _liste.Select(i => i.Modul).ToList();

    private static volatile IReadOnlyDictionary<string, int> _zelte = new Dictionary<string, int>();

    /// <summary>
    /// Das Zelt jeder weiteren Instanz, wie die Zuweisung es gerade nennt. Prozessweit wie das Register selbst: der Katalog einer
    /// Entfeuchter-Instanz braucht es, um auf das CO₂ des richtigen Zeltes zu zeigen, ohne eine Datenbank anzufassen.
    /// Gesetzt von <see cref="SteuerungZeltService"/>.
    /// </summary>
    public static void ZelteFestlegen(IReadOnlyDictionary<string, int?> zuordnung)
        => _zelte = zuordnung.Where(p => p.Value is not null && InstanzModul.IstInstanz(p.Key)).ToDictionary(p => p.Key, p => p.Value!.Value, StringComparer.Ordinal);

    /// <summary>Das Zelt einer weiteren Instanz — <c>null</c>, solange es nicht bekannt ist.</summary>
    public static int? ZeltFuer(string modul) => _zelte.TryGetValue(modul, out var zelt) ? zelt : null;
}

/// <summary>Anlegen, Umbenennen und Entfernen weiterer Instanzen. Sie entstehen nur dadurch — ein Bestand ohne Instanz bleibt, wie er ist.</summary>
public sealed class SteuerungInstanzService
{
    public const string Modul = "steuerung-instanzen";
    public const int NameMax = 60;

    private readonly SteuerungRepository _repo;

    public SteuerungInstanzService(SteuerungRepository repo) => _repo = repo;

    private SteuerungInstanzenStand Stand => _repo.GetEinstellungen<SteuerungInstanzenStand>(Modul) ?? new SteuerungInstanzenStand();

    /// <summary>Beim Start und nach jeder Änderung: das Register aus dem Gespeicherten setzen.</summary>
    public static void Aktualisieren(SteuerungRepository repo)
        => SteuerungInstanzen.Festlegen((repo.GetEinstellungen<SteuerungInstanzenStand>(Modul) ?? new SteuerungInstanzenStand()).Instanzen);

    public IReadOnlyList<SteuerungInstanz> Liste() => Stand.Instanzen.OrderBy(i => i.Art, StringComparer.Ordinal).ThenBy(i => i.Nummer).ToList();

    /// <summary>Eine weitere Instanz anlegen. Die Nummer ist die nächste freie der Art und wird nie wieder vergeben.</summary>
    /// <returns>Die Instanz, oder <c>null</c> mit dem Fehlertext.</returns>
    public (SteuerungInstanz? Instanz, string? Fehler) Anlegen(string art, string name)
    {
        if (!InstanzModul.Arten.Contains(art, StringComparer.Ordinal)) return (null, "Diese Steuerung lässt sich nicht vervielfachen.");
        var pruefung = NamePruefen(name);
        if (pruefung is not null) return (null, pruefung);

        var stand = Stand;
        if (stand.Instanzen.Any(i => i.Art == art && string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return (null, "So heißt schon eine andere Instanz dieser Steuerung.");

        var nummer = Math.Max(stand.Vergeben.GetValueOrDefault(art, 1), 1) + 1;
        stand.Vergeben[art] = nummer;
        var instanz = new SteuerungInstanz { Art = art, Nummer = nummer, Name = name.Trim() };
        stand.Instanzen.Add(instanz);
        Speichern(stand);
        return (instanz, null);
    }

    /// <summary>Eine Instanz umbenennen. Nur der Name im Fork ändert sich, nichts in Home Assistant.</summary>
    public string? Umbenennen(string modul, string name)
    {
        var pruefung = NamePruefen(name);
        if (pruefung is not null) return pruefung;
        var stand = Stand;
        var instanz = stand.Instanzen.FirstOrDefault(i => i.Modul == modul);
        if (instanz is null) return "Diese Instanz gibt es nicht.";
        if (stand.Instanzen.Any(i => i.Art == instanz.Art && i != instanz && string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "So heißt schon eine andere Instanz dieser Steuerung.";
        instanz.Name = name.Trim();
        Speichern(stand);
        return null;
    }

    /// <summary>Eine Instanz aus dem Fork entfernen. Ihre Bausteine in Home Assistant bleiben stehen; die Nummer bleibt vergeben.</summary>
    public string? Entfernen(string modul)
    {
        var stand = Stand;
        if (stand.Instanzen.RemoveAll(i => i.Modul == modul) == 0) return "Diese Instanz gibt es nicht.";
        Speichern(stand);
        return null;
    }

    private void Speichern(SteuerungInstanzenStand stand)
    {
        _repo.SetEinstellungen(Modul, stand);
        SteuerungInstanzen.Festlegen(stand.Instanzen);
    }

    private static string? NamePruefen(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Bitte einen Namen eingeben.";
        return name.Trim().Length > NameMax ? $"Der Name darf höchstens {NameMax} Zeichen lang sein." : null;
    }
}
