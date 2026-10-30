using System.Text.Json.Serialization;

namespace GrowDiary.Web.Services.Knowledge.Schema;

/// <summary>
/// Eine Etappe der Trocknung: so lange, bei diesem Klima.
/// </summary>
/// <remarks>
/// Ein Programm trägt eine Liste davon (<see cref="NutrientProgramDefinition.Trocknung"/>).
/// Sie liegt bewusst NICHT als weitere Spalte im Wochen-Chart: Wochen zählt der Plan ab Vegi-Start
/// und Flip, die Trocknung zählt Tage ab der Ernte — beides in einer Liste hätte die ganze
/// Wochen-Mechanik (Planwochen, Spaltenwahl, Wochenplan-Seite) angefasst.
/// </remarks>
public sealed class TrockenEtappe
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Wie viele Tage die Etappe dauert; danach beginnt die nächste.</summary>
    [JsonPropertyName("dauerTage")]
    public int DauerTage { get; set; }

    /// <summary>Ziel-Lufttemperatur in °C.</summary>
    [JsonPropertyName("lufttemperaturC")]
    public double LufttemperaturC { get; set; }

    /// <summary>Ziel-Luftfeuchte in %.</summary>
    [JsonPropertyName("luftfeuchteZiel")]
    public double LuftfeuchteZiel { get; set; }

    /// <summary>
    /// Obergrenze der Luftfeuchte in %: darüber wächst das Schimmelrisiko. Gilt als Schutz und
    /// begrenzt das Zielband der Kachel.
    /// </summary>
    [JsonPropertyName("luftfeuchteMax")]
    public double LuftfeuchteMax { get; set; }
}
