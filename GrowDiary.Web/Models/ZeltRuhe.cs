namespace GrowDiary.Web.Models;

/// <summary>
/// Fork AI (cc.277, „Zelt ruhend"): Was der Fork über den Ruhezustand des Zelts festhält.
/// </summary>
/// <remarks>
/// Gespeichert unter dem Modul <c>zeltruhe</c> in <c>ForkSteuerungEinstellungen</c>. Der Fork merkt sich je Gerät, was er
/// ausgeschaltet hat und wie es vorher stand — „Zelt wieder aktiv" stellt genau das wieder her und nichts anderes.
/// </remarks>
public sealed class ZeltRuheEinstellungen
{
    /// <summary>
    /// Die Geräte, die im Ruhemodus ausgehen (Kennungen aus <see cref="ZeltRuheArten"/>). <c>null</c>: nie gewählt, es gilt
    /// <see cref="ZeltRuheArten.Standard"/>.
    /// </summary>
    public List<string>? Auswahl { get; set; }

    public bool Ruhend { get; set; }

    public DateTime? SeitUtc { get; set; }

    /// <summary>Was beim Ruhendschalten ausgeschaltet wurde — mit dem Zustand davor.</summary>
    public List<ZeltRuheMerker> Gemerkt { get; set; } = new();

    /// <summary>Hat der Fork den Licht-Wächter für die Ruhezeit angehalten? Nur dann hebt er die Pause beim Wecken auf.</summary>
    public bool WaechterAngehalten { get; set; }
}

/// <summary>Ein ausgeschaltetes Gerät mit dem Zustand davor.</summary>
public sealed class ZeltRuheMerker
{
    public string Kennung { get; set; } = string.Empty;

    /// <summary>War die Automatik des Geräts an? <c>null</c>: das Gerät hat keine (Licht, Abluft).</summary>
    public bool? AutomatikWarAn { get; set; }

    public string? Entity { get; set; }

    /// <summary>Der Zustand des Geräts vor dem Ausschalten, so wie Home Assistant ihn meldete („Schedule", „on" …).</summary>
    public string? Vorher { get; set; }

    /// <summary>
    /// Die Zelte, deren Fork-eigene Kühler-Regelung (<c>Tent.ChillerControlEnabled</c>, der <c>KuehlerWorker</c>) an war und
    /// ausgeschaltet wurde. Ohne das schaltete der Worker die Steckdose binnen einer Minute wieder ein.
    /// </summary>
    public List<int> ChillerZelte { get; set; } = new();

    /// <summary>Wird dieses Gerät beim Wecken von seiner Regelung geführt (Automatik oder Fork-Regler), statt direkt zurückgestellt?</summary>
    public bool Gefuehrt => AutomatikWarAn == true || ChillerZelte.Count > 0;
}

/// <summary>Eine Art Gerät, die „Zelt ruhend" ausschalten kann.</summary>
/// <param name="Kennung">Stabil, steht in der gespeicherten Auswahl.</param>
/// <param name="Titel">Wie das Gerät in der Oberfläche heißt.</param>
/// <param name="Modul">Das Steuerungs-Modul, in dem die Schalter-Rolle liegt.</param>
/// <param name="Rolle">Die Rolle, die das Gerät wirklich ein- und ausschaltet.</param>
/// <param name="Standard">Steht es ohne Auswahl auf „ausschalten"?</param>
public sealed record ZeltRuheArt(string Kennung, string Titel, string Modul, string Rolle, bool Standard);

public static class ZeltRuheArten
{
    public const string Licht = "licht";
    public const string Co2 = "co2";
    public const string Abluft = "abluft";
    public const string Zusatz = "zusatz";
    public const string Chiller = "chiller";
    public const string Entfeuchter = "entfeuchter";
    public const string Zuluft = "zuluft";

    /// <summary>
    /// In der Reihenfolge der Oberfläche. Entfeuchter und Zuluft stehen ohne Auswahl auf „läuft weiter": Sie sind die Geräte,
    /// die auch ohne Pflanzen gebraucht werden können (Raumluft, Trocknen) — ausgeschaltet wird nur, was jemand ausdrücklich ankreuzt.
    /// </summary>
    public static IReadOnlyList<ZeltRuheArt> Alle { get; } = new ZeltRuheArt[]
    {
        new(Licht, "Licht", "licht", "licht_modus", true),
        new(Co2, "CO₂-Begasung", "co2", "port_schalter", true),
        new(Abluft, "Abluft", "co2", "abluft_schalter", true),
        new(Zusatz, "Zusatz-Entfeuchter", SteuerungGeraeteRollen.ZusatzModul, "zusatz_schalter", true),
        new(Chiller, "Wasserkühler", "chiller", "steckdose", true),
        new(Entfeuchter, "Entfeuchter", "entfeuchter", "port_schalter", false),
        new(Zuluft, "Zuluft", "zuluft", "port_schalter", false),
    };

    public static ZeltRuheArt? Finden(string? kennung)
        => Alle.FirstOrDefault(a => string.Equals(a.Kennung, kennung, StringComparison.OrdinalIgnoreCase));

    /// <summary>Die Kennungen, die ohne eigene Auswahl ausgehen — nur Geräte, die es gibt.</summary>
    public static IReadOnlyList<string> Standard(IEnumerable<string> vorhanden)
    {
        var da = vorhanden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Alle.Where(a => a.Standard && da.Contains(a.Kennung)).Select(a => a.Kennung).ToList();
    }
}

/// <summary>Ein Gerät in der Antwort — für die Zelt-Karte und das Blatt nach der Ernte.</summary>
/// <param name="Vorhanden">Hat die Rolle eine Entität? Ohne sie lässt sich das Gerät nicht ausschalten.</param>
/// <param name="Jetzt">Der Zustand in Worten („aus", „an", „Zeitplan", „nicht erreichbar").</param>
/// <param name="Laeuft">Läuft es gerade? <c>null</c>: unbekannt.</param>
/// <param name="Ausschalten">Steht es in der Auswahl?</param>
/// <param name="Gemerkt">Hat der Fork es beim Ruhendschalten ausgeschaltet?</param>
public sealed record ZeltRuheGeraet(string Kennung, string Titel, bool Vorhanden, string Jetzt, bool? Laeuft, bool Ausschalten, bool Gemerkt);

/// <param name="ZeltId">Das Zelt, zu dem die Steuerung gehört (das Zelt des neuesten Grows) — die Karte steht nur dort.</param>
/// <param name="ZeltTyp">Der Zweck dieses Zelts („Production", „Drying" …). Ein Trockenzelt hat weder Karte noch Frage: seine Geräte folgen dem Trocknungsplan.</param>
public sealed record ZeltRuheStand(bool Ruhend, DateTime? SeitUtc, int? ZeltId, string? ZeltTyp, bool HaErreichbar, IReadOnlyList<ZeltRuheGeraet> Geraete);

/// <summary>Was bei einem Schaltvorgang mit jedem Gerät passiert ist.</summary>
public sealed record ZeltRuheErgebnis(bool Ok, string Zusammenfassung, IReadOnlyList<ZeltRuheSchritt> Schritte, ZeltRuheStand Stand);

public sealed record ZeltRuheSchritt(string Kennung, string Titel, bool Ok, string Text);
