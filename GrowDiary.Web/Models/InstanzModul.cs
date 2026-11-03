using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GrowDiary.Web.Models;

/// <summary>
/// Namenslogik für weitere <b>Instanzen</b> einer Steuerung (Entfeuchter, Zusatz-Entfeuchter, Wasserkühler, Zuluft, Lampe):
/// Modulschlüssel, Kennungen, Namen und Vorlagentexte einer Instanz.
/// </summary>
/// <remarks>
/// <para><b>Die erste Instanz behält alles.</b> Das Modul <c>chiller</c> und jede seiner Kennungen bleiben, wie sie sind — es
/// gibt keine Migration und kein Umbenennen. Jede weitere Instanz bekommt das Modul <c>{art}-i{n}</c> (n ab 2, je Art
/// fortlaufend, nie wiederverwendet), zum Beispiel <c>chiller-i2</c>; ihre Seite liegt unter <c>/steuerung/chiller-i2</c>.</para>
/// <para><b>Kennungen mit Endung statt Präfix.</b> <c>input_number.chiller_zieltemperatur_tag</c> wird zu
/// <c>input_number.chiller_zieltemperatur_tag_i2</c>, der Name „Chiller Zieltemperatur Tag“ zu „Chiller Zieltemperatur Tag I2“
/// (Home Assistant leitet die Kennung aus dem Namen ab; beides muss zusammenpassen). Die Endung geht bei jeder Kennung, auch bei
/// denen ohne Art-Präfix (<c>vpd_ziel_unten</c>, <c>water_chiller_regelung</c>). In Vorlagen werden die vollständigen Kennungen
/// des Katalogs ersetzt, nie ein Muster: Was nicht im Katalog der Art steht (Rollen-Platzhalter, Kennungen anderer Steuerungen),
/// bleibt unberührt.</para>
/// <para><b>Gemeinsame Bausteine</b> (<see cref="Gemeinsam"/>) gehören keiner Instanz: Luft draußen und im Keller ist für alle
/// dieselbe. Sie behalten ihre Kennung.</para>
/// <para>Alles hier ist rein — ohne Datenbank, ohne Home Assistant — und damit einzeln prüfbar.</para>
/// </remarks>
public static class InstanzModul
{
    /// <summary>Die Arten, die sich vervielfachen lassen. Die längere Kennung zuerst: „entfeuchter-zusatz“ vor „entfeuchter“.</summary>
    public static IReadOnlyList<string> Arten { get; } = ["entfeuchter-zusatz", "entfeuchter", "chiller", "zuluft", "licht"];

    /// <summary>Kennungen, die für alle Instanzen einer Art dieselben bleiben (Messwerte der Außen- und Kellerluft).</summary>
    public static IReadOnlySet<string> Gemeinsam { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sensor.absolute_feuchte_draussen",
        "sensor.absolute_feuchte_keller",
    };

    private static readonly Regex ModulMuster = new(
        "^(?<art>entfeuchter-zusatz|entfeuchter|chiller|zuluft|licht)-i(?<n>[2-9]|[1-9][0-9]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly System.Text.Json.JsonSerializerOptions Unescaped = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Das Modul einer weiteren Instanz: <c>{art}-i{nummer}</c>. Nummer 1 ist die erste Instanz und heißt wie die Art.</summary>
    public static string Fuer(string art, int nummer)
    {
        if (!Arten.Contains(art, StringComparer.Ordinal)) throw new ArgumentException($"„{art}“ ist keine Steuerung, die sich vervielfachen lässt.", nameof(art));
        return nummer <= 1 ? art : $"{art}-i{nummer}";
    }

    /// <summary>Ist das Modul eine weitere Instanz (nicht die erste, nicht CO₂)?</summary>
    public static bool IstInstanz(string? modul) => modul is not null && ModulMuster.IsMatch(modul);

    /// <summary>Die Art einer Instanz — <c>null</c>, wenn das Modul keine weitere Instanz ist.</summary>
    public static string? Art(string? modul)
        => modul is not null && ModulMuster.Match(modul) is { Success: true } m ? m.Groups["art"].Value : null;

    /// <summary>Die Nummer einer Instanz (ab 2) — <c>null</c> für alles, was keine weitere Instanz ist.</summary>
    public static int? Nummer(string? modul)
        => modul is not null && ModulMuster.Match(modul) is { Success: true } m
            ? int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

    /// <summary>Das Modul, dessen Definitionen (Katalog, Rollen, Vorlagen) gelten: für eine Instanz die Art, für CO₂-Zelte <c>co2</c>, sonst das Modul selbst.</summary>
    public static string BasisModul(string modul) => Co2Modul.BasisModul(Art(modul) ?? modul);

    /// <summary>Die Entitäts-Kennung der ersten Instanz in die einer weiteren übersetzen: <c>…_i{n}</c> hinter dem Objektteil.</summary>
    public static string Id(string basisId, int nummer)
    {
        if (!basisId.Contains('.', StringComparison.Ordinal)) throw new ArgumentException($"„{basisId}“ ist keine Entitäts-Kennung (domäne.objekt).", nameof(basisId));
        return Gemeinsam.Contains(basisId) ? basisId : $"{basisId}_i{nummer}";
    }

    /// <summary>Den Namen in Home Assistant übersetzen: „Chiller Hysterese“ → „Chiller Hysterese I2“.</summary>
    public static string Name(string basisName, int nummer, string? basisId = null)
        => basisId is not null && Gemeinsam.Contains(basisId) ? basisName : $"{basisName} I{nummer}";

    /// <summary>
    /// Ein Vorlagentext (Automation, Rechen-Sensor): jede der übergebenen Kennungen auf die Instanz umstellen. Die längste zuerst
    /// und nur an Wortgrenzen — <c>input_number.chiller_hysterese</c> trifft nicht <c>input_number.chiller_hysterese_extra</c>.
    /// </summary>
    /// <param name="vorlage">Der Text der ersten Instanz.</param>
    /// <param name="nummer">Die Nummer der Instanz (ab 2).</param>
    /// <param name="kennungen">Alle Kennungen im Katalog der Art (auch die gemeinsamen — die bleiben).</param>
    public static string Text(string vorlage, int nummer, IEnumerable<string> kennungen)
    {
        var eigene = kennungen.Where(k => !Gemeinsam.Contains(k)).Distinct(StringComparer.Ordinal).OrderByDescending(k => k.Length).ToList();
        if (eigene.Count == 0) return vorlage;
        var muster = new Regex(
            @"(?<![A-Za-z0-9_.])(?:" + string.Join("|", eigene.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);
        return muster.Replace(vorlage, m => $"{m.Value}_i{nummer}");
    }

    /// <summary>
    /// Eine Automations-Vorlage für eine Instanz: Kennungen im Text und der Alias („Water Chiller Regelung“ → „Water Chiller Regelung I2“)
    /// werden umgestellt, Rollen-Platzhalter bleiben. Ist das Modul keine weitere Instanz, kommt dieselbe Vorlage zurück.
    /// Die Eingabe wird nicht verändert.
    /// </summary>
    public static JsonObject Vorlage(JsonObject vorlage, string modul, IEnumerable<string> kennungen)
    {
        if (Nummer(modul) is not { } n) return vorlage;
        var kopie = JsonNode.Parse(Text(vorlage.ToJsonString(Unescaped), n, kennungen)) as JsonObject
            ?? throw new InvalidOperationException("Die Vorlage ist nach dem Umstellen kein Objekt mehr.");
        if (kopie["alias"] is JsonValue alias && alias.TryGetValue<string>(out var text)) kopie["alias"] = Name(text, n);
        return kopie;
    }
}
