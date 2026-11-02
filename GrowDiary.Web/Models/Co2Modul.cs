using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GrowDiary.Web.Models;

/// <summary>
/// Namenslogik für die CO₂-Steuerung <b>je Zelt</b>: Modulschlüssel, Kennungen, Namen und Vorlagentexte eines Zeltes.
/// </summary>
/// <remarks>
/// <para><b>Wozu.</b> Die CO₂-Steuerung war ein einziger Satz von Objekten (<c>input_number.co2_*</c>, vier Automationen, eine
/// Flasche) ohne Bezug zu einem Zelt. Wer zwei Zelte mit CO₂ hat — jedes mit eigener Flasche —, braucht je Zelt einen
/// eigenen Satz, ohne dass sich die Namen in die Quere kommen.</para>
/// <para><b>Das Bestand-Zelt behält alles.</b> Das Zelt, dem die vorhandenen Objekte gehören (<see cref="Basis"/>), heißt
/// weiter Modul <c>co2</c> und behält jede Kennung. Nichts wird umbenannt, doppelt angelegt oder überschrieben. Jedes weitere Zelt
/// bekommt das Modul <c>co2-z{id}</c>; seine Objekte tragen <c>z{id}</c> hinter dem Präfix: <c>input_number.co2_zielwert</c> wird
/// zu <c>input_number.co2_z2_zielwert</c>, der Name „CO2 Zielwert“ zu „CO2 Z2 Zielwert“ (Home Assistant leitet die Kennung aus
/// dem Namen ab; beides muss zusammenpassen).</para>
/// <para>Alles hier ist rein — ohne Datenbank, ohne Home Assistant — und damit einzeln prüfbar.</para>
/// </remarks>
public static class Co2Modul
{
    /// <summary>Das Modul des Bestand-Zeltes — und die Vorlage, aus der die Module weiterer Zelte abgeleitet werden.</summary>
    public const string Basis = "co2";

    private const string Praefix = Basis + "-z";

    private static readonly Regex ModulMuster = new("^co2-z(?<id>[1-9][0-9]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Eine Entitäts-Kennung der CO₂-Steuerung: Domäne, Punkt, „co2_“. Der Punkt davor schließt die Rollen-Platzhalter
    // wie [[co2_sensor]] aus — sie gehören dem Nutzer und bleiben, wie sie sind.
    private static readonly Regex KennungMuster = new(
        @"(?<=\b(?:input_number|input_boolean|input_datetime|counter|sensor|binary_sensor|automation|script)\.)co2_",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Das Modul eines Zeltes: das Bestand-Zelt <see cref="Basis"/>, jedes andere <c>co2-z{id}</c>.</summary>
    /// <param name="zeltId">Das Zelt.</param>
    /// <param name="bestandZeltId">Das Zelt, dem die vorhandenen Objekte gehören — oder <c>null</c>, wenn es noch keins gibt.</param>
    public static string Fuer(int zeltId, int? bestandZeltId)
        => bestandZeltId is { } b && b == zeltId ? Basis : Praefix + zeltId;

    /// <summary>Gehört das Modul zur CO₂-Steuerung (Basis oder ein Zelt-Modul)?</summary>
    public static bool IstCo2(string? modul)
        => string.Equals(modul, Basis, StringComparison.Ordinal) || (modul is not null && ModulMuster.IsMatch(modul));

    /// <summary>Das Zelt eines Zelt-Moduls — <c>null</c> für das Basis-Modul und alles, was kein CO₂-Modul ist.</summary>
    public static int? ZeltId(string? modul)
    {
        if (modul is null) return null;
        var m = ModulMuster.Match(modul);
        return m.Success ? int.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Das Modul, dessen Definitionen (Katalog, Rollen, Vorlagen) gelten: für ein Zelt-Modul <see cref="Basis"/>.</summary>
    public static string BasisModul(string modul) => IstCo2(modul) ? Basis : modul;

    /// <summary>Die Entitäts-Kennung des Bestands in die eines Zeltes übersetzen. Ohne Zelt (Bestand) unverändert.</summary>
    /// <exception cref="ArgumentException">Die Kennung beginnt nicht mit <c>{domäne}.co2_</c> — dann gehört sie nicht zu diesem Modul.</exception>
    public static string Id(string bestandId, int? zeltId)
    {
        if (zeltId is null) return bestandId;
        if (!KennungMuster.IsMatch(bestandId))
        {
            throw new ArgumentException($"„{bestandId}“ ist keine CO₂-Kennung (erwartet: {{domäne}}.co2_…).", nameof(bestandId));
        }
        return KennungMuster.Replace(bestandId, $"co2_z{zeltId}_", 1);
    }

    /// <summary>Den Namen in Home Assistant übersetzen: „CO2 Zielwert“ → „CO2 Z2 Zielwert“. Ohne Zelt unverändert.</summary>
    public static string Name(string bestandName, int? zeltId)
    {
        if (zeltId is null) return bestandName;
        if (!bestandName.StartsWith("CO2 ", StringComparison.Ordinal))
        {
            throw new ArgumentException($"„{bestandName}“ beginnt nicht mit „CO2 “.", nameof(bestandName));
        }
        return $"CO2 Z{zeltId} " + bestandName["CO2 ".Length..];
    }

    /// <summary>
    /// Ein Vorlagentext (Automation, Rechen-Sensor): alle Kennungen der CO₂-Steuerung auf das Zelt umstellen, Rollen-Platzhalter
    /// und fremde Kennungen unberührt. Ohne Zelt unverändert.
    /// </summary>
    public static string Text(string vorlage, int? zeltId)
        => zeltId is null ? vorlage : KennungMuster.Replace(vorlage, $"co2_z{zeltId}_");

    /// <summary>
    /// Eine Automations-Vorlage für ein Zelt: Kennungen im Text und der Alias („CO2 Wächter“ → „CO2 Z2 Wächter“) werden umgestellt,
    /// Rollen-Platzhalter bleiben. Ohne Zelt kommt dieselbe Vorlage zurück. Die Eingabe wird nicht verändert.
    /// </summary>
    public static JsonObject Vorlage(JsonObject vorlage, int? zeltId)
    {
        if (zeltId is null) return vorlage;
        var kopie = JsonNode.Parse(Text(vorlage.ToJsonString(), zeltId)) as JsonObject
            ?? throw new InvalidOperationException("Die Vorlage ist nach dem Umstellen kein Objekt mehr.");
        if (kopie["alias"] is JsonValue alias && alias.TryGetValue<string>(out var text) && text.StartsWith("CO2 ", StringComparison.Ordinal))
        {
            kopie["alias"] = Name(text, zeltId);
        }
        return kopie;
    }
}
