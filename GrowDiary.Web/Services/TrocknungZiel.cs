using System.Text.RegularExpressions;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Trocknungs-Ziel je Zelt (A-029, Etappe 3, Teil A): Was verlangt der Plan von den Geräten — und was steht am Gerät?
/// Rein lesend: Dieser Teil schreibt nichts an Home Assistant und nichts an den Controller.
/// </summary>
/// <remarks>
/// <para><b>Warum Ports statt eigener Regelung.</b> Die Geräte am Trockenzelt hängen an Ports eines AC-Infinity-Controllers.
/// Jeder Port bringt eine eigene Automatik mit (Modus Auto mit Auslösern für Temperatur und Luftfeuchte), und der Controller
/// schaltet damit selbst — auch wenn Home Assistant oder der Fork gerade nicht laufen. Der Fork muss deshalb nichts
/// regeln; er vergleicht den Plan mit den Auslösern und schreibt sie später (Teil B) nur auf Wunsch.</para>
/// <para><b>Erkennung ohne Namensraten.</b> Die Entitäten tragen eine stabile, sprachunabhängige Kennung
/// <c>ac_infinity_&lt;Controller&gt;_port_&lt;Nr&gt;_&lt;Schlüssel&gt;</c>. Der Schlüssel <c>atType</c> ist der Modus, <c>devHh</c> die
/// Luftfeuchte-Obergrenze (<c>activeHh</c> ihr Ein-Schalter), <c>devHt</c> die Temperatur-Obergrenze (<c>activeHt</c>).
/// Der deutsche Anzeigename („Aktiver Modus“) ist kein Schlüssel — er hängt an der Sprache der Installation.</para>
/// </remarks>
public static partial class TrocknungZiel
{
    public const string RolleEntfeuchter = "entfeuchter";
    public const string RolleKuehlung = "kuehlung";

    /// <summary>
    /// Die Abluft hat EINEN Port, aber zwei Grenzen (Luftfeuchte und Temperatur). Die Zuordnung (Port) heißt <c>abluft</c>;
    /// beim Abgleich zählt jede Grenze als eigene Rolle mit eigenem Merker.
    /// </summary>
    public const string RolleAbluft = "abluft";
    public const string RolleAbluftFeuchte = "abluft-feuchte";
    public const string RolleAbluftTemp = "abluft-temp";

    /// <summary>Misst diese Rolle die Luftfeuchte (sonst die Temperatur)?</summary>
    public static bool IstFeuchte(string rolle) => rolle is RolleEntfeuchter or RolleAbluftFeuchte;

    /// <summary>Die Rolle, unter der der Port zugeordnet ist (beide Abluft-Grenzen hängen am Port „abluft“).</summary>
    public static string PortRolle(string rolle) => rolle is RolleAbluftFeuchte or RolleAbluftTemp ? RolleAbluft : rolle;

    /// <summary>Ein Port des Controllers mit den Entitäten, die der Vergleich braucht.</summary>
    public sealed record Port(
        string DeviceId,
        string Name,
        string Controller,
        int Nummer,
        string ModusEntity,
        string? FeuchteWertEntity,
        string? FeuchteAktivEntity,
        string? TempWertEntity,
        string? TempAktivEntity,
        string? StufeEntity = null);

    [GeneratedRegex(@"^ac_infinity_(?<controller>[0-9A-Za-z]+)_port_(?<nr>\d+)_(?<schluessel>[A-Za-z0-9]+)$")]
    private static partial Regex KennungMuster();

    /// <summary>Aus dem Register alle Ports mit eigenem Modus erkennen (stabile Kennung, kein Namensraten).</summary>
    public static IReadOnlyList<Port> Erkennen(IEnumerable<HerkunftEintrag> eintraege)
    {
        var ports = new List<Port>();
        foreach (var gruppe in eintraege.Where(e => e.DeviceId is not null && e.UniqueId is not null).GroupBy(e => e.DeviceId!))
        {
            var je = new Dictionary<string, string>(StringComparer.Ordinal);
            string? controller = null;
            var nummer = 0;
            foreach (var eintrag in gruppe)
            {
                var treffer = KennungMuster().Match(eintrag.UniqueId!);
                if (!treffer.Success) continue;
                controller = treffer.Groups["controller"].Value;
                nummer = int.Parse(treffer.Groups["nr"].Value);
                je[treffer.Groups["schluessel"].Value] = eintrag.EntityId;
            }

            // Ein Port ohne Modus ist keine Automatik, die sich vergleichen ließe.
            if (controller is null || !je.TryGetValue("atType", out var modus)) continue;
            var name = gruppe.Select(e => e.DeviceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? $"Port {nummer}";
            ports.Add(new Port(
                gruppe.Key, name, controller, nummer, modus,
                je.GetValueOrDefault("devHh"), je.GetValueOrDefault("activeHh"),
                je.GetValueOrDefault("devHt"), je.GetValueOrDefault("activeHt"),
                // „Einschaltleistung“: die Lüfterstufe, wenn der Port auf On steht.
                je.GetValueOrDefault("onSpead")));
        }

        return ports.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public enum Urteil
    {
        /// <summary>Für diese Rolle ist kein Port zugeordnet.</summary>
        NichtZugeordnet,
        /// <summary>Zugeordnet, aber nicht lesbar (Home Assistant oder das Gerät antwortet nicht).</summary>
        Unbekannt,
        /// <summary>Der Port steht auf Aus.</summary>
        GeraetAus,
        /// <summary>Der Port läuft in einem anderen Modus als Auto (z. B. An): Es gibt keine Auslöser, die dem Plan folgen könnten.</summary>
        OhneAutomatik,
        /// <summary>Modus Auto, aber der Auslöser dieser Rolle ist ausgeschaltet.</summary>
        AusloeserAus,
        /// <summary>Der Auslöser am Gerät stimmt (im Rahmen der Toleranz) mit dem Plan überein.</summary>
        Passt,
        /// <summary>Der Auslöser am Gerät weicht vom Plan ab.</summary>
        WeichtAb,
    }

    public sealed record Vergleich(
        string Rolle,
        string? PortName,
        string? Modus,
        double? WertAmGeraet,
        string? Einheit,
        double? PlanWert,
        Urteil Urteil);

    /// <summary>Für eine Rolle ohne Port.</summary>
    public static Vergleich OhnePort(string rolle, string? portName, double? plan)
        => new(rolle, portName, null, null, null, plan, Urteil.NichtZugeordnet);

    /// <summary>Toleranz, innerhalb derer ein Auslöser als „stimmt mit dem Plan überein“ gilt (Punkte bzw. Grad).</summary>
    public const double ToleranzFeuchte = 1.0;
    public const double ToleranzTemperatur = 0.5;

    /// <summary>
    /// Plan gegen Gerät für EINE Rolle. <paramref name="zustand"/> liefert den Zustand einer Entität (oder <c>null</c>);
    /// <paramref name="planOberGrenze"/> ist der obere Rand des Bands der Kachel (Feuchte in %, Temperatur in °C).
    /// </summary>
    public static Vergleich Vergleiche(string rolle, Port? port, Func<string, HomeAssistantState?> zustand, double? planOberGrenze)
    {
        if (port is null) return new Vergleich(rolle, null, null, null, null, planOberGrenze, Urteil.NichtZugeordnet);

        var modusState = zustand(port.ModusEntity);
        if (IstNichtLesbar(modusState))
            return new Vergleich(rolle, port.Name, null, null, null, planOberGrenze, Urteil.Unbekannt);

        var modus = modusState!.State;
        if (string.Equals(modus, "Off", StringComparison.OrdinalIgnoreCase))
            return new Vergleich(rolle, port.Name, modus, null, null, planOberGrenze, Urteil.GeraetAus);
        if (!string.Equals(modus, "Auto", StringComparison.OrdinalIgnoreCase))
            return new Vergleich(rolle, port.Name, modus, null, null, planOberGrenze, Urteil.OhneAutomatik);

        var feuchte = IstFeuchte(rolle);
        var wertEntity = feuchte ? port.FeuchteWertEntity : port.TempWertEntity;
        var aktivEntity = feuchte ? port.FeuchteAktivEntity : port.TempAktivEntity;
        var wertState = wertEntity is null ? null : zustand(wertEntity);
        var aktivState = aktivEntity is null ? null : zustand(aktivEntity);
        if (IstNichtLesbar(wertState) || IstNichtLesbar(aktivState))
            return new Vergleich(rolle, port.Name, modus, null, null, planOberGrenze, Urteil.Unbekannt);

        var einheit = wertState!.UnitOfMeasurement;
        // Fahrenheit und Co. würden einen falschen Unterschied ergeben — dann lieber „unbekannt“ als eine falsche Abweichung.
        if (!feuchte && einheit is not null && einheit != "°C")
            return new Vergleich(rolle, port.Name, modus, null, einheit, planOberGrenze, Urteil.Unbekannt);
        // Zahlen aus Home Assistant liest die App an EINER Stelle (Zahlenlesen.Maschine): „NaN“ und „Infinity“ sind dort keine Zahl.
        if (Zahlenlesen.Maschine(wertState.State) is not { } wert)
            return new Vergleich(rolle, port.Name, modus, null, einheit, planOberGrenze, Urteil.Unbekannt);

        if (!string.Equals(aktivState!.State, "on", StringComparison.OrdinalIgnoreCase))
            return new Vergleich(rolle, port.Name, modus, wert, einheit, planOberGrenze, Urteil.AusloeserAus);

        // Ohne Plan (kein Band für diesen Tag) gibt es nichts zu vergleichen — den Wert zeigen, aber kein Urteil fällen.
        if (planOberGrenze is not { } plan) return new Vergleich(rolle, port.Name, modus, wert, einheit, null, Urteil.Unbekannt);

        var urteil = Math.Abs(wert - plan) <= (feuchte ? ToleranzFeuchte : ToleranzTemperatur) ? Urteil.Passt : Urteil.WeichtAb;
        return new Vergleich(rolle, port.Name, modus, wert, einheit, planOberGrenze, urteil);
    }

    /// <summary>Was Teil B für EINE Rolle am Gerät tun würde — unabhängig vom Modus des Ports (die Grenze lässt sich auch bei „Aus“ setzen).</summary>
    public sealed record Wunsch(string Rolle, string? Entity, string? Modus, double? Jetzt, string? Einheit, double? Plan, string Urteil)
    {
        public bool Setzen => Urteil == "weichtAb" && Entity is not null && Plan is not null;
    }

    /// <summary>
    /// Die Grenze, auf die der Plan sie setzen würde: oberer Rand des Bands, auf eine ganze Zahl gerundet (die Auslöser des
    /// Controllers kennen nur ganze Werte) und in die Spanne der Entität gezwungen. Das Urteil: <c>passt</c>,
    /// <c>weichtAb</c>, <c>unbekannt</c> (nicht lesbar), <c>geraetAus</c> / <c>ohneAutomatik</c> (der Port stellt die Grenze in diesem Modus nicht bereit),
    /// <c>keinAusloeser</c> oder <c>keinPlan</c>.
    /// </summary>
    public static Wunsch Entscheide(string rolle, Port port, Func<string, HomeAssistantState?> zustand, double? planOberGrenze)
    {
        var modusState = zustand(port.ModusEntity);
        var modus = IstNichtLesbar(modusState) ? null : modusState!.State;
        var feuchte = IstFeuchte(rolle);
        var wertEntity = feuchte ? port.FeuchteWertEntity : port.TempWertEntity;
        if (wertEntity is null) return new Wunsch(rolle, null, modus, null, null, null, "keinAusloeser");

        var wertState = zustand(wertEntity);
        if (IstNichtLesbar(wertState))
        {
            // Der Controller stellt die Grenzen eines Ports nur im Modus Auto bereit; steht er auf Aus oder einem anderen Modus,
            // meldet die Entität „nicht verfügbar“ — das ist kein Fehler, sondern der Zustand des Geräts (und es lässt sich dann nichts schreiben).
            var urteil = modus is null || string.Equals(modus, "Auto", StringComparison.OrdinalIgnoreCase) ? "unbekannt"
                : string.Equals(modus, "Off", StringComparison.OrdinalIgnoreCase) ? "geraetAus" : "ohneAutomatik";
            return new Wunsch(rolle, wertEntity, modus, null, null, null, urteil);
        }
        var einheit = wertState!.UnitOfMeasurement;
        if (!feuchte && einheit is not null && einheit != "°C") return new Wunsch(rolle, wertEntity, modus, null, einheit, null, "unbekannt");
        if (Zahlenlesen.Maschine(wertState.State) is not { } jetzt) return new Wunsch(rolle, wertEntity, modus, null, einheit, null, "unbekannt");
        if (planOberGrenze is not { } plan || !double.IsFinite(plan)) return new Wunsch(rolle, wertEntity, modus, jetzt, einheit, null, "keinPlan");

        var ziel = Math.Round(plan, MidpointRounding.AwayFromZero);
        if (wertState.AttributMin is { } min) ziel = Math.Max(ziel, min);
        if (wertState.AttributMax is { } max) ziel = Math.Min(ziel, max);
        return new Wunsch(rolle, wertEntity, modus, jetzt, einheit, ziel, Math.Abs(jetzt - ziel) < 0.5 ? "passt" : "weichtAb");
    }

    private static bool IstNichtLesbar(HomeAssistantState? zustand)
        => zustand is null
            || string.Equals(zustand.State, "unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(zustand.State, "unknown", StringComparison.OrdinalIgnoreCase);
}
