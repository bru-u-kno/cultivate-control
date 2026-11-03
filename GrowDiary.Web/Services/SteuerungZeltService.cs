using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Was gespeichert wird: je Steuerung das Zelt, zu dem sie gehört.</summary>
public sealed class SteuerungZeltZuordnung
{
    public Dictionary<string, int> Zelte { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Steuerungen je Zelt (Etappe 1): Zu welchem Zelt gehört eine Steuerung? Der Anwender entscheidet.
/// </summary>
/// <remarks>
/// <para><b>Nur Zuordnung und Anzeige.</b> Die Zuordnung gliedert die Übersicht nach Zelten. Geräte, Helfer und Automationen
/// ändert sie nicht — die gehören weiter dem Steuerungszelt, bis es Instanzen je Steuerung gibt.</para>
/// <para><b>Das CO₂ gehört nicht hierher.</b> Es läuft ohnehin je Zelt (<see cref="Co2Zelte"/>) und trägt sein Zelt selbst.</para>
/// <para><b>Bestand bleibt, wo er war.</b> Hat der Nutzer ein einziges aktives Zelt, wird nichts gespeichert: Alles gehört dorthin. Kommt ein zweites
/// Zelt dazu, schreibt der erste Blick den heutigen Stand fest (Steuerungszelt = Zelt des laufenden Grows, sonst das CO₂-Bestand-Zelt), damit
/// eine Steuerung nicht stumm das Zelt wechselt, wenn später ein anderer Grow läuft.</para>
/// </remarks>
public sealed class SteuerungZeltService
{
    public const string Modul = "steuerung-zelte";

    /// <summary>Die Steuerungen, die einem Zelt zugewiesen werden (alle außer dem CO₂ und seiner Abluft).</summary>
    private static readonly string[] ErsteInstanzen = ["entfeuchter", "entfeuchter-zusatz", "zuluft", "chiller", "licht"];

    /// <summary>Die Steuerungen, die einem Zelt zugewiesen werden: die ersten Instanzen und alle weiteren aus dem Register.</summary>
    public static IReadOnlyList<string> Zuweisbar => ErsteInstanzen.Concat(SteuerungInstanzen.Module).ToList();

    private readonly SteuerungRepository _repo;
    private readonly TentRepository _zelte;
    private readonly GrowRepository _grows;

    public SteuerungZeltService(SteuerungRepository repo, TentRepository zelte, GrowRepository grows)
    {
        _repo = repo;
        _zelte = zelte;
        _grows = grows;
    }

    /// <summary>Die aktiven Zelte in der Reihenfolge der Oberfläche.</summary>
    public IReadOnlyList<Tent> AktiveZelte() => _zelte.GetTents().Where(t => t.Status == TentStatus.Active).ToList();

    /// <summary>Das Zelt, in dem alles liegt, was noch keine eigene Wahl hat.</summary>
    public int? StandardZelt()
    {
        var aktive = AktiveZelte();
        if (aktive.Count == 0) return null;
        bool Aktiv(int? id) => id is { } z && aktive.Any(t => t.Id == z);

        var laufend = ZeltRuheService.SteuerungsZeltId(_grows);
        if (Aktiv(laufend)) return laufend;
        var co2 = Co2Zelte.Aktuell.BestandZeltId;
        if (Aktiv(co2)) return co2;
        return (aktive.FirstOrDefault(t => t.TentType != TentType.Drying) ?? aktive[0]).Id;
    }

    /// <summary>
    /// Zelt je Steuerung, wie es jetzt gilt. Mit höchstens einem aktiven Zelt gehört jede dorthin, ohne dass etwas gespeichert wird;
    /// mit mehreren wird der Stand beim ersten Lesen festgeschrieben.
    /// </summary>
    public IReadOnlyDictionary<string, int?> Zuordnung()
    {
        var aktive = AktiveZelte();
        var standard = StandardZelt();
        var gespeichert = _repo.GetEinstellungen<SteuerungZeltZuordnung>(Modul);

        if (gespeichert is null && aktive.Count >= 2 && standard is { } z)
        {
            gespeichert = new SteuerungZeltZuordnung();
            foreach (var kennung in Zuweisbar) gespeichert.Zelte[kennung] = z;
            _repo.SetEinstellungen(Modul, gespeichert);
        }

        var ergebnis = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kennung in Zuweisbar)
        {
            // Ein Zelt, das es nicht mehr gibt (archiviert, gelöscht), gilt wie „nie gewählt“.
            ergebnis[kennung] = gespeichert is not null && gespeichert.Zelte.TryGetValue(kennung, out var wahl) && aktive.Any(t => t.Id == wahl)
                ? wahl
                : standard;
        }
        return ergebnis;
    }

    /// <summary>Das Zelt einer Steuerung (null: kein Zelt vorhanden oder unbekannte Steuerung).</summary>
    public int? ZeltFuer(string kennung) => Zuordnung().TryGetValue(kennung, out var z) ? z : null;

    /// <summary>
    /// Zuordnungen setzen. Unbekannte Steuerungen und Zelte, die es nicht (mehr) gibt, werden abgelehnt — nicht still verworfen.
    /// Nur die genannten Steuerungen ändern sich.
    /// </summary>
    /// <returns>Der Fehlertext, oder <c>null</c>, wenn gespeichert wurde.</returns>
    public string? Speichern(IReadOnlyDictionary<string, int> aenderung)
    {
        var unbekannt = aenderung.Keys.Where(k => !Zuweisbar.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unbekannt.Count > 0) return $"Diese Steuerung lässt sich keinem Zelt zuweisen: {string.Join(", ", unbekannt)}.";

        var aktive = AktiveZelte();
        var fremd = aenderung.Values.Where(id => aktive.All(t => t.Id != id)).Distinct().ToList();
        if (fremd.Count > 0) return $"Dieses Zelt gibt es nicht (oder es ist archiviert): {string.Join(", ", fremd)}.";

        var jetzt = Zuordnung();
        var neu = new SteuerungZeltZuordnung();
        foreach (var kennung in Zuweisbar)
        {
            var wahl = aenderung.FirstOrDefault(p => string.Equals(p.Key, kennung, StringComparison.OrdinalIgnoreCase));
            var zelt = wahl.Key is not null ? wahl.Value : jetzt[kennung];
            if (zelt is { } id) neu.Zelte[kennung] = id;
        }
        // Der Zusatz-Entfeuchter steht neben dem Entfeuchter und folgt ihm ins Zelt, solange er nicht selbst genannt wird.
        if (aenderung.Keys.Any(k => string.Equals(k, "entfeuchter", StringComparison.OrdinalIgnoreCase))
            && !aenderung.Keys.Any(k => string.Equals(k, "entfeuchter-zusatz", StringComparison.OrdinalIgnoreCase))
            && neu.Zelte.TryGetValue("entfeuchter", out var haupt))
        {
            neu.Zelte["entfeuchter-zusatz"] = haupt;
        }
        _repo.SetEinstellungen(Modul, neu);
        return null;
    }
}
