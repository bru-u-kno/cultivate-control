using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Was der Wächter gefunden hat.</summary>
public enum WaechterBefund
{
    /// <summary>Sensor und Plan stimmen überein.</summary>
    Ok,
    /// <summary>Controller oder Home Assistant nicht erreichbar — es wird nichts geschaltet.</summary>
    NichtErreichbar,
    /// <summary>Ein Lichtsensor ist zugeordnet, liefert aber keinen Wert; Betriebsart und Zeiten stimmen.</summary>
    SensorAusgefallen,
    /// <summary>Betriebsart steht nicht auf Zeitplan.</summary>
    ModusFalsch,
    /// <summary>Die Zeiten im Controller weichen vom Lichtplan ab.</summary>
    ZeitenFalsch,
    /// <summary>Zeitplan und Zeiten stimmen, aber die Leistungsstufe ist 0.</summary>
    StufeNull,
    /// <summary>Alles stimmt, trotzdem passt der Sensor nicht zum Plan: Lampe, Netzteil oder Sensor.</summary>
    Ungeklaert,
}

/// <summary>Die Messwerte und Sollwerte, über die der Wächter urteilt.</summary>
/// <param name="SollHell">Der Lichtplan verlangt gerade Licht.</param>
/// <param name="Sensor">Lichtsensor in Prozent; null = liefert nichts.</param>
/// <param name="SensorZugeordnet">Ob überhaupt ein Lichtsensor zugeordnet ist.</param>
/// <param name="HellAb">Schwelle in Prozent.</param>
/// <param name="Erreichbar">Home Assistant und Controller antworten.</param>
/// <param name="Modus">Betriebsart des Controllers, wie Home Assistant sie meldet.</param>
/// <param name="EinZeit">Ein-Zeit im Controller (HH:mm).</param>
/// <param name="AusZeit">Aus-Zeit im Controller (HH:mm).</param>
/// <param name="Stufe">Leistungsstufe im Controller.</param>
/// <param name="PlanEin">Ein-Zeit laut Lichtplan (HH:mm).</param>
/// <param name="PlanAus">Aus-Zeit laut Lichtplan (HH:mm).</param>
public sealed record WaechterLage(
    bool SollHell, double? Sensor, bool SensorZugeordnet, int HellAb, bool Erreichbar,
    string? Modus, string? EinZeit, string? AusZeit, int? Stufe, string PlanEin, string PlanAus);

/// <summary>
/// Die Regeln des Licht-Wächters — reine Funktionen, ohne Uhr und ohne Home Assistant.
/// </summary>
/// <remarks>
/// Die Reihenfolge ist die der Fehlersuche von Hand: Controller erreichbar? Betriebsart
/// Zeitplan? Zeiten und Stufe richtig? Erst wenn all das stimmt, bleibt die Hardware als
/// Ursache — und die kann der Fork nicht beheben.
/// </remarks>
public static class LichtWaechterRegeln
{
    public static WaechterBefund Beurteilen(WaechterLage lage)
    {
        if (!lage.Erreichbar) return WaechterBefund.NichtErreichbar;

        var sensorOk = lage.Sensor is { } wert
            && (lage.SollHell ? wert >= lage.HellAb : wert < lage.HellAb);
        if (sensorOk) return WaechterBefund.Ok;

        var modusStimmt = string.Equals(lage.Modus, AcModi.Zeitplan, StringComparison.OrdinalIgnoreCase);
        if (!modusStimmt) return WaechterBefund.ModusFalsch;

        if (!Gleich(lage.EinZeit, lage.PlanEin) || !Gleich(lage.AusZeit, lage.PlanAus))
            return WaechterBefund.ZeitenFalsch;

        if (lage.Stufe is 0) return WaechterBefund.StufeNull;

        if (lage.Sensor is null)
            return lage.SensorZugeordnet ? WaechterBefund.SensorAusgefallen : WaechterBefund.Ok;

        return WaechterBefund.Ungeklaert;
    }

    /// <summary>Nur falsche Betriebsart und falsche Zeiten setzt der Fork selbst zurück.</summary>
    public static bool Korrigierbar(WaechterBefund befund)
        => befund is WaechterBefund.ModusFalsch or WaechterBefund.ZeitenFalsch;

    /// <summary>Gleiche Uhrzeit, egal ob „05:00“ oder „05:00:00“.</summary>
    public static bool Gleich(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
           && a.Length >= 5 && b.Length >= 5
           && string.Equals(a[..5], b[..5], StringComparison.Ordinal);

    /// <summary>Wie lange nach der geplanten Zeit gewartet wird, bevor der Sensor zählt.</summary>
    public static readonly TimeSpan Wartezeit = TimeSpan.FromMinutes(5);

    /// <summary>Nach dieser Zeit ist die Prüfung der Phase vorbei; später wird nichts mehr nachgeholt.</summary>
    public static readonly TimeSpan Fenster = TimeSpan.FromMinutes(65);

    /// <summary>Abstand zwischen einer Korrektur und der Kontrolle, ob sie gewirkt hat.</summary>
    public static readonly TimeSpan Kontrollabstand = TimeSpan.FromMinutes(2);

    public const int MaxKorrekturen = 2;

    /// <summary>Die jüngste geplante Ein- oder Ausschaltzeit bis <paramref name="jetzt"/> (Ortszeit).</summary>
    public static (DateTime Zeitpunkt, bool Hell)? LetzteGrenze(DateTime jetzt, string planEin, string planAus)
    {
        if (!TimeOnly.TryParse(planEin, out var ein) || !TimeOnly.TryParse(planAus, out var aus) || ein == aus)
            return null;

        (DateTime, bool)? beste = null;
        foreach (var tag in new[] { jetzt.Date.AddDays(-1), jetzt.Date })
        {
            foreach (var (zeit, hell) in new[] { (ein, true), (aus, false) })
            {
                var punkt = tag + zeit.ToTimeSpan();
                if (punkt <= jetzt && (beste is null || punkt > beste.Value.Item1)) beste = (punkt, hell);
            }
        }

        return beste;
    }

    // ------------------------------------------------------------------ Texte

    public static string Ursache(WaechterBefund befund, WaechterLage l) => befund switch
    {
        WaechterBefund.NichtErreichbar => "Controller oder Cloud nicht erreichbar.",
        WaechterBefund.SensorAusgefallen => "Der Lichtsensor liefert keinen Wert; Betriebsart und Zeiten stimmen.",
        WaechterBefund.ModusFalsch => $"Betriebsart stand auf „{AcModi.Name(l.Modus)}“ statt „Zeitplan“.",
        WaechterBefund.ZeitenFalsch => $"Zeiten im Controller {l.EinZeit?[..Math.Min(5, l.EinZeit.Length)]}–{l.AusZeit?[..Math.Min(5, l.AusZeit.Length)]} statt {l.PlanEin}–{l.PlanAus}.",
        WaechterBefund.StufeNull => "Die Leistungsstufe steht auf 0.",
        WaechterBefund.Ungeklaert => "Zeitplan, Betriebsart und Zeiten stimmen, der Controller ist erreichbar.",
        _ => "Alles in Ordnung.",
    };

    public static string SensorText(WaechterLage l)
        => l.Sensor is { } w ? $"Sensor {w:0.#} %" : "Sensor ohne Wert";

    public static string Soll(WaechterLage l, string seit)
        => $"Soll {(l.SollHell ? "hell" : "dunkel")} seit {seit}";
}
