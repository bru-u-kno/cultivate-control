namespace GrowDiary.Web.Models;

/// <summary>Eine weitere Instanz einer Steuerung (die erste ist die Steuerung selbst und steht nicht hier).</summary>
public sealed class SteuerungInstanz
{
    /// <summary>Die Art: <c>entfeuchter</c>, <c>entfeuchter-zusatz</c>, <c>chiller</c>, <c>zuluft</c> oder <c>licht</c>.</summary>
    public string Art { get; set; } = string.Empty;

    /// <summary>Die Nummer ab 2, je Art fortlaufend und nie wiederverwendet.</summary>
    public int Nummer { get; set; }

    /// <summary>Wie die Instanz in der Oberfläche heißt — nur im Fork; in Home Assistant bleiben die Namen technisch.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Das Modul dieser Instanz.</summary>
    public string Modul => InstanzModul.Fuer(Art, Nummer);
}

/// <summary>Was gespeichert wird: die weiteren Instanzen und je Art die nächste freie Nummer.</summary>
public sealed class SteuerungInstanzenStand
{
    public List<SteuerungInstanz> Instanzen { get; set; } = [];

    /// <summary>Je Art die zuletzt vergebene Nummer. Wird nie zurückgesetzt, damit ein gelöschter Satz Bausteine in Home Assistant nie zu einer neuen Instanz passt.</summary>
    public Dictionary<string, int> Vergeben { get; set; } = new(StringComparer.Ordinal);
}
