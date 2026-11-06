namespace GrowDiary.Web.Services;

/// <summary>
/// Der Entfeuchter am Trockenzelt wird vom Fork geschaltet (Ein/Aus des Ports) statt über den Grenzwert im Gerät. Grund
/// (06.11.2026): Die Cloud des Controllers lehnt das Schreiben der Grenzwerte im Modus Auto ab (999999), das Schalten von
/// On/Off geht. Hier steht nur die Entscheidung — rein, ohne Home Assistant, damit sie ohne Gerät prüfbar bleibt.
/// </summary>
public static class TrocknungSchalten
{
    /// <summary>Kürzester Abstand zwischen zwei Schaltvorgängen — schont den Kompressor des Entfeuchters.</summary>
    public static readonly TimeSpan MindestPause = TimeSpan.FromMinutes(3);

    public const string ZustandKeinMesswert = "keinMesswert";
    public const string ZustandPortNichtSchaltbar = "portNichtSchaltbar";
    public const string ZustandWartet = "wartet";
    public const string ZustandLaeuft = "laeuft";
    public const string ZustandSteht = "steht";
    public const string ZustandSchaltetEin = "schaltetEin";
    public const string ZustandSchaltetAus = "schaltetAus";

    public enum Aktion { Nichts, Einschalten, Ausschalten }

    public sealed record Entscheidung(string Zustand, Aktion Aktion);

    /// <summary>Die Schwellen aus dem Plan: ein ab dem Ziel der Etappe, aus unter dem unteren Rand des Bands.</summary>
    public static (double EinAb, double AusUnter) Schwellen(double ziel, double bandUnten)
        => (ziel, bandUnten < ziel ? bandUnten : ziel - 1);

    /// <param name="feuchte">Luftfeuchte im Zelt; <c>null</c>, wenn nicht lesbar.</param>
    /// <param name="modus">Modus des Ports laut Home Assistant (<c>On</c>, <c>Off</c>, <c>Auto</c> …).</param>
    /// <param name="zuletztGeschaltetUtc">Wann der Fork zuletzt geschaltet hat; <c>null</c> seit dem Start noch nie.</param>
    public static Entscheidung Entscheide(double? feuchte, string? modus, double einAb, double ausUnter, DateTime? zuletztGeschaltetUtc, DateTime jetztUtc)
    {
        var an = string.Equals(modus, "On", StringComparison.OrdinalIgnoreCase);
        var aus = string.Equals(modus, "Off", StringComparison.OrdinalIgnoreCase);
        // Auto, unavailable und alle anderen Modi stellt der Fork nie ein: dann kann er nicht schalten.
        if (!an && !aus) return new Entscheidung(ZustandPortNichtSchaltbar, Aktion.Nichts);
        // Ohne Messwert bleibt der Entfeuchter, wie er ist: weder plötzlich aus noch dauerhaft an.
        if (feuchte is not { } f || !double.IsFinite(f)) return new Entscheidung(ZustandKeinMesswert, Aktion.Nichts);

        var wuerdeEin = aus && f >= einAb;
        var wuerdeAus = an && f < ausUnter;
        if (!wuerdeEin && !wuerdeAus) return new Entscheidung(an ? ZustandLaeuft : ZustandSteht, Aktion.Nichts);
        if (zuletztGeschaltetUtc is { } z && jetztUtc - z < MindestPause) return new Entscheidung(ZustandWartet, Aktion.Nichts);
        return wuerdeEin
            ? new Entscheidung(ZustandSchaltetEin, Aktion.Einschalten)
            : new Entscheidung(ZustandSchaltetAus, Aktion.Ausschalten);
    }
}
