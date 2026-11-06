namespace GrowDiary.Web.Services;

/// <summary>
/// Mindeststufe der Abluft am Trockenzelt (07.11.2026): Die Abluft soll nie unter eine vom Nutzer gewählte Lüfterstufe fallen.
/// Der Fork liest die Stufe (die „Einschaltleistung“ des Ports, wenn er auf On steht) und hebt sie bei Bedarf an; nach oben und am
/// Modus ändert er nichts. Hier steht nur die Entscheidung — rein, ohne Home Assistant.
/// </summary>
public static class TrocknungAbluftStufe
{
    public const int HoechstStufe = 10;

    public const string ZustandHaelt = "haelt";
    public const string ZustandHebtAn = "hebtAn";
    public const string ZustandPortNichtOn = "portNichtOn";
    public const string ZustandKeinWert = "keinWert";

    public enum Aktion { Nichts, Anheben }

    public sealed record Entscheidung(string Zustand, Aktion Aktion);

    /// <param name="mindest">Die gewählte Mindeststufe, 1 bis 10 (0 = keine; dann gibt es nichts zu entscheiden).</param>
    /// <param name="modus">Modus des Ports laut Home Assistant. Nur On hat eine feste Stufe; Off, Auto und alle anderen Modi stellt der Fork nie ein.</param>
    /// <param name="stufe">Die Stufe jetzt; <c>null</c>, wenn nicht lesbar.</param>
    public static Entscheidung Entscheide(int mindest, string? modus, double? stufe)
    {
        if (!string.Equals(modus, "On", StringComparison.OrdinalIgnoreCase)) return new Entscheidung(ZustandPortNichtOn, Aktion.Nichts);
        if (stufe is not { } s || !double.IsFinite(s)) return new Entscheidung(ZustandKeinWert, Aktion.Nichts);
        return s < mindest ? new Entscheidung(ZustandHebtAn, Aktion.Anheben) : new Entscheidung(ZustandHaelt, Aktion.Nichts);
    }

    /// <summary>Die Mindeststufe, wie sie gespeichert wird: ganze Zahl von 0 (keine) bis 10.</summary>
    public static bool Gueltig(int stufe) => stufe is >= 0 and <= HoechstStufe;
}
