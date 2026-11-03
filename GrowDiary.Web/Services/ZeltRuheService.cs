using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Fork AI (cc.277, „Zelt ruhend"): Zwischen zwei Durchgängen alle nicht benötigten Geräte ausschalten — und später genau
/// das wieder einschalten, was ausgeschaltet wurde.
/// </summary>
/// <remarks>
/// <para><b>Je Gerät zwei Schritte, in dieser Reihenfolge.</b> Erst die Automatik des Geräts aus (über den Speichern-Weg der
/// jeweiligen Steuerung, damit der gespeicherte Stand und Home Assistant übereinstimmen und der Stundenabgleich sie nicht
/// zurückdreht), dann das Gerät selbst. Umgekehrt schaltete die Automatik es im nächsten Takt wieder ein. Beim Wecken
/// läuft es umgekehrt: erst das Gerät in den Zustand von vorher, dann die Automatik wieder an.</para>
/// <para><b>Nur, was war.</b> Der Fork merkt sich je Gerät die Zustände davor (<see cref="ZeltRuheMerker"/>). Ein Gerät, das
/// schon aus war, bleibt nach dem Wecken aus; eine Automatik, die schon aus war, bleibt aus.</para>
/// <para><b>Ein Gerät, das nicht antwortet, wird übersprungen</b> und nicht „irgendwie" ausgeschaltet — im Ergebnis steht, was
/// geklappt hat und was nicht. Gemerkt wird nur, was wirklich geschaltet wurde.</para>
/// <para><b>Geräte kommen aus den Rollen.</b> Die Entitäten stehen in der Geräte-Zuordnung der Steuerungen (eine Wahrheit
/// je Gerät); der Fork kennt hier keine feste Entity-ID. Ohne zugeordnete Rolle ist ein Gerät „nicht vorhanden".</para>
/// </remarks>
public sealed class ZeltRuheService
{
    /// <summary>Unter diesem Modul steht der Zustand in <c>ForkSteuerungEinstellungen</c>.</summary>
    public const string Modul = "zeltruhe";

    /// <summary>So lange hält der Fork den Licht-Wächter an, solange das Zelt ruht. „Zelt wieder aktiv" hebt es früher auf.</summary>
    internal static readonly TimeSpan WaechterPause = TimeSpan.FromDays(60);

    /// <summary>Ruhend, Wieder aktiv und die Auswahl sind nie gleichzeitig unterwegs: zwei Aufrufe würden sonst den Merker des anderen überschreiben.</summary>
    private static readonly SemaphoreSlim Sperre = new(1, 1);

    private readonly SteuerungRepository _repo;
    private readonly SteuerungGeraeteService _geraete;
    private readonly HomeAssistantService _ha;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly GrowRepository _grows;
    private readonly IZeltRuheWerkzeug _werkzeug;
    private readonly ILogger<ZeltRuheService> _logger;
    private readonly SteuerungZeltService? _zelte;

    public ZeltRuheService(
        SteuerungRepository repo,
        SteuerungGeraeteService geraete,
        HomeAssistantService ha,
        HomeAssistantSettingsRepository haSettings,
        GrowRepository grows,
        IZeltRuheWerkzeug werkzeug,
        ILogger<ZeltRuheService> logger,
        SteuerungZeltService? zelte = null)
    {
        _zelte = zelte;
        _repo = repo;
        _geraete = geraete;
        _ha = ha;
        _haSettings = haSettings;
        _grows = grows;
        _werkzeug = werkzeug;
        _logger = logger;
    }

    /// <summary>Der Zustand des Zeltes der Steuerung (wie bisher unter dem Modul <see cref="Modul"/>).</summary>
    public ZeltRuheEinstellungen Einstellungen => _repo.GetEinstellungen<ZeltRuheEinstellungen>(Modul) ?? new ZeltRuheEinstellungen();

    /// <summary>
    /// Unter welchem Modul der Zustand eines Zeltes liegt: das Zelt der Steuerung behält <see cref="Modul"/> (nichts wird umgezogen),
    /// jedes andere Zelt bekommt <c>zeltruhe-z{id}</c>.
    /// </summary>
    public static string ModulFuer(int? zeltId, int? steuerungsZeltId)
        => zeltId is { } z && z != steuerungsZeltId ? $"{Modul}-z{z}" : Modul;

    private ZeltRuheEinstellungen EinstellungenFuer(int? zeltId)
        => _repo.GetEinstellungen<ZeltRuheEinstellungen>(ModulFuer(zeltId, SteuerungsZeltId())) ?? new ZeltRuheEinstellungen();

    private void Speichern(int? zeltId, ZeltRuheEinstellungen e)
        => _repo.SetEinstellungen(ModulFuer(zeltId, SteuerungsZeltId()), e);

    /// <summary>Das Zelt, um das es geht: das genannte, sonst das Zelt der Steuerung.</summary>
    private int? Zelt(int? zeltId) => zeltId ?? SteuerungsZeltId();

    /// <summary>Welche Arten Gerät zu einem Zelt gehören: die Steuerungen, die ihm zugewiesen sind, und das CO₂ der Zelte mit CO₂.</summary>
    public IReadOnlyList<ZeltRuheArt> ArtenFuer(int? zeltId)
    {
        // Ohne Zeltangabe (alte Aufrufer, MCP): alles wie bisher — alle Geräte des Zeltes der Steuerung.
        if (zeltId is not { } zelt) return ZeltRuheArten.Alle;
        // Ohne Zuweisungsdienst (und mit nur einem Zelt) gehört alles dem Zelt der Steuerung — wie bisher.
        var bestand = SteuerungsZeltId();
        var co2Zelte = Co2Zelte.Aktuell.ZeltIds;
        bool Gehoert(ZeltRuheArt a) => a.Kennung switch
        {
            ZeltRuheArten.Co2 or ZeltRuheArten.Abluft => co2Zelte.Count > 0 ? co2Zelte.Contains(zelt) : zelt == bestand,
            ZeltRuheArten.Zusatz => _zelte is null ? zelt == bestand : _zelte.ZeltFuer("entfeuchter-zusatz") == zelt,
            _ => _zelte is null ? zelt == bestand : _zelte.ZeltFuer(a.Kennung) == zelt,
        };
        return ZeltRuheArten.Alle.Where(Gehoert).ToList();
    }

    // ------------------------------------------------------------------ Reine Regeln (ohne Home Assistant)

    /// <summary>Wie ein Zustand aus Home Assistant in der Oberfläche heißt.</summary>
    public static (string Text, bool? Laeuft) Beschreiben(string? domain, string? zustand)
    {
        if (string.IsNullOrWhiteSpace(zustand)) return ("nicht erreichbar", null);
        var z = zustand.Trim();
        if (z.Equals("unavailable", StringComparison.OrdinalIgnoreCase) || z.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return ("nicht erreichbar", null);

        if (string.Equals(domain, "select", StringComparison.OrdinalIgnoreCase))
        {
            var aus = z.Equals(AcModi.Aus, StringComparison.OrdinalIgnoreCase);
            return (aus ? "aus" : AcModi.Name(z), !aus);
        }

        var an = z.Equals("on", StringComparison.OrdinalIgnoreCase);
        var ausSchalter = z.Equals("off", StringComparison.OrdinalIgnoreCase);
        return an ? ("an", true) : ausSchalter ? ("aus", false) : (z, null);
    }

    /// <summary>Der Dienstaufruf, der ein Gerät ausschaltet — je nach Art der Entität.</summary>
    public static (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten)? Ausschalten(string entityId)
    {
        var domain = SteuerungGeraeteService.Domain(entityId);
        return domain switch
        {
            "select" => ("select", "select_option", new Dictionary<string, object> { ["option"] = AcModi.Aus }),
            "switch" or "input_boolean" or "light" or "fan" => (domain, "turn_off", null),
            _ => null,
        };
    }

    /// <summary>Der Dienstaufruf, der ein Gerät in den Zustand von vorher zurückstellt.</summary>
    public static (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten)? Wiederherstellen(string entityId, string? vorher)
    {
        if (string.IsNullOrWhiteSpace(vorher)
            || vorher.Equals("unavailable", StringComparison.OrdinalIgnoreCase)
            || vorher.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;

        var domain = SteuerungGeraeteService.Domain(entityId);
        switch (domain)
        {
            case "select":
                return ("select", "select_option", new Dictionary<string, object> { ["option"] = vorher });
            case "switch" or "input_boolean" or "light" or "fan":
                return (domain, vorher.Equals("on", StringComparison.OrdinalIgnoreCase) ? "turn_on" : "turn_off", null);
            default:
                return null;
        }
    }

    /// <summary>Welche Kennungen gelten: die gespeicherte Auswahl, sonst die Vorgabe — jeweils nur für vorhandene Geräte.</summary>
    public static IReadOnlyList<string> Wirksam(IReadOnlyList<string>? auswahl, IReadOnlyCollection<string> vorhanden)
    {
        if (auswahl is null) return ZeltRuheArten.Standard(vorhanden);
        var da = vorhanden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ZeltRuheArten.Alle.Where(a => da.Contains(a.Kennung) && auswahl.Contains(a.Kennung, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Kennung).ToList();
    }

    // ------------------------------------------------------------------ Stand

    /// <summary>Das Zelt, zu dem die Steuerung gehört: das Zelt des laufenden, sonst des zuletzt angelegten Grows.</summary>
    public int? SteuerungsZeltId() => SteuerungsZeltId(_grows);

    public static int? SteuerungsZeltId(GrowRepository grows)
    {
        var alle = grows.GetAllGrows().Where(g => g.TentId is not null).ToList();
        var laufend = alle.Where(g => !g.IsArchived).OrderByDescending(g => g.Id).FirstOrDefault();
        return (laufend ?? alle.OrderByDescending(g => g.Id).FirstOrDefault())?.TentId;
    }

    /// <summary>
    /// Ist dieses Zelt von Hand auf „ruhend" gestellt? Dann melden seine Alarme nichts: Wer das Zelt ruhen lässt, hat es
    /// leer gemeldet. Gilt nur für das Zelt der Steuerung (die Geräte-Zuordnung kennt nur eins).
    /// </summary>
    public static bool IstRuhend(SteuerungRepository repo, GrowRepository grows, int tentId)
        => repo.GetEinstellungen<ZeltRuheEinstellungen>(ModulFuer(tentId, SteuerungsZeltId(grows))) is { Ruhend: true };

    public async Task<ZeltRuheStand> StandAsync(CancellationToken ct) => await StandAsync(null, ct);

    /// <param name="zeltId">Das Zelt; <c>null</c>: das Zelt der Steuerung.</param>
    public async Task<ZeltRuheStand> StandAsync(int? zeltId, CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var e = EinstellungenFuer(zeltId);
        var arten = ArtenFuer(zeltId);
        var rollen = Rollen(zeltId);
        var wirksam = Wirksam(e.Auswahl, rollen.Where(r => r.Value is not null).Select(r => r.Key).ToList());

        var geraete = new List<ZeltRuheGeraet>();
        var erreichbar = settings.IsConfigured;
        foreach (var art in arten)
        {
            var entity = rollen[art.Kennung];
            if (entity is null)
            {
                geraete.Add(new ZeltRuheGeraet(art.Kennung, art.Titel, false, "nicht zugeordnet", null, false, false));
                continue;
            }

            var (text, laeuft) = ("nicht erreichbar", (bool?)null);
            if (settings.IsConfigured)
            {
                var stand = await _ha.GetEntityStateAsync(settings, entity, ct);
                (text, laeuft) = Beschreiben(SteuerungGeraeteService.Domain(entity), stand?.State);
                if (stand is null) erreichbar = false;
            }
            geraete.Add(new ZeltRuheGeraet(
                art.Kennung, art.Titel, true, text, laeuft,
                wirksam.Contains(art.Kennung, StringComparer.OrdinalIgnoreCase),
                e.Gemerkt.Any(m => string.Equals(m.Kennung, art.Kennung, StringComparison.OrdinalIgnoreCase))));
        }
        var zelt = Zelt(zeltId);
        var zeltTyp = zelt is { } id ? _grows.GetTent(id)?.TentType.ToString() : null;
        return new ZeltRuheStand(e.Ruhend, e.SeitUtc, zelt, zeltTyp, erreichbar, geraete);
    }

    private Dictionary<string, string?> Rollen(int? zeltId)
    {
        var ergebnis = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var zelt = zeltId;
        foreach (var art in ArtenFuer(zeltId))
        {
            // CO₂ und Abluft liegen je Zelt unter ihrem eigenen Modul (co2 für das Bestand-Zelt, co2-z{id} für weitere).
            var modul = art.Modul == Co2Modul.Basis && zelt is { } z && Co2Zelte.Aktuell.BestandZeltId is not null
                ? Co2Zelte.ModulFuer(z)
                : art.Modul;
            ergebnis[art.Kennung] = _geraete.Entity(modul, art.Rolle);
        }
        return ergebnis;
    }

    /// <summary>Speichert, was im Ruhemodus ausgehen soll. Unbekannte Kennungen werden abgelehnt.</summary>
    public (bool Ok, string? Fehler) AuswahlSpeichern(IReadOnlyList<string> kennungen, int? zeltId = null)
    {
        Sperre.Wait();
        try { return AuswahlSpeichernOhneSperre(kennungen, zeltId); }
        finally { Sperre.Release(); }
    }

    private (bool Ok, string? Fehler) AuswahlSpeichernOhneSperre(IReadOnlyList<string> kennungen, int? zeltId)
    {
        var unbekannt = kennungen.FirstOrDefault(k => ZeltRuheArten.Finden(k) is null);
        if (unbekannt is not null) return (false, $"„{unbekannt}“ ist kein Gerät, das „Zelt ruhend“ kennt.");
        var e = EinstellungenFuer(zeltId);
        e.Auswahl = ZeltRuheArten.Alle.Where(a => kennungen.Contains(a.Kennung, StringComparer.OrdinalIgnoreCase)).Select(a => a.Kennung).ToList();
        Speichern(zeltId, e);
        return (true, null);
    }

    // ------------------------------------------------------------------ Ruhend schalten

    /// <param name="kennungen">Die Geräte dieses Mal; <c>null</c>: die gespeicherte Auswahl. Wird sie so mitgeschickt, merkt sich der Fork sie als neue Auswahl.</param>
    public async Task<ZeltRuheErgebnis> RuhendAsync(IReadOnlyList<string>? kennungen, CancellationToken ct) => await RuhendAsync(kennungen, null, ct);

    /// <param name="zeltId">Das Zelt; <c>null</c>: das Zelt der Steuerung.</param>
    public async Task<ZeltRuheErgebnis> RuhendAsync(IReadOnlyList<string>? kennungen, int? zeltId, CancellationToken ct)
    {
        await Sperre.WaitAsync(ct);
        try
        {
            var settings = _haSettings.GetEffectiveHomeAssistantSettings();
            if (!settings.IsConfigured) return await ErgebnisAsync(zeltId, false, "Home Assistant ist nicht verbunden — es wurde nichts geschaltet.", [], ct);

            if (kennungen is not null)
            {
                var (ok, fehler) = AuswahlSpeichernOhneSperre(kennungen, zeltId);
                if (!ok) return await ErgebnisAsync(zeltId, false, fehler!, [], ct);
            }

            var e = EinstellungenFuer(zeltId);
            var rollen = Rollen(zeltId);
            var wirksam = Wirksam(e.Auswahl, rollen.Where(r => r.Value is not null).Select(r => r.Key).ToList());

            // Ist das Zelt schon ruhend, werden nur die Geräte nachgeholt, die noch nicht ausgeschaltet sind.
            var offen = wirksam.Where(k => !e.Gemerkt.Any(m => string.Equals(m.Kennung, k, StringComparison.OrdinalIgnoreCase))).ToList();
            if (offen.Count == 0)
            {
                return await ErgebnisAsync(zeltId, false, e.Ruhend
                    ? "Das Zelt ist schon ruhend — es gibt nichts Weiteres auszuschalten."
                    : "Es ist kein Gerät zum Ausschalten ausgewählt.", [], ct);
            }

            var geraete = offen.Select(k => (ZeltRuheArten.Finden(k)!, rollen[k]!)).ToList();
            // Ohne das Token der Anfrage: bricht der Aufrufer mitten drin ab, soll der Fork den Weg zu Ende gehen
            // (und der Merker steht schon vor jedem Eingriff im Speicher).
            var ablauf = new ZeltRuheAblauf(_werkzeug, () => EinstellungenFuer(zeltId), e2 => Speichern(zeltId, e2), _logger, zeltId: zeltId);
            var schritte = await ablauf.RuhendAsync(geraete, CancellationToken.None);

            var geklappt = schritte.Count(s => s.Ok);
            var ruhend = EinstellungenFuer(zeltId).Ruhend;
            var text = geklappt == schritte.Count
                ? $"Zelt ruhend: {geklappt} {(geklappt == 1 ? "Gerät" : "Geräte")} ausgeschaltet."
                : geklappt == 0
                    ? (ruhend ? "Nichts bestätigt ausgeschaltet — die Regelung einiger Geräte ist aber schon aus, Details unten." : "Nichts ausgeschaltet, Details unten.")
                    : $"Zelt ruhend: {geklappt} von {schritte.Count} Geräten ausgeschaltet, der Rest steht unten.";
            return await ErgebnisAsync(zeltId, geklappt > 0, text, schritte, ct);
        }
        finally { Sperre.Release(); }
    }

    // ------------------------------------------------------------------ Wieder aktiv

    public async Task<ZeltRuheErgebnis> AktivAsync(CancellationToken ct) => await AktivAsync(null, ct);

    /// <param name="zeltId">Das Zelt; <c>null</c>: das Zelt der Steuerung.</param>
    public async Task<ZeltRuheErgebnis> AktivAsync(int? zeltId, CancellationToken ct)
    {
        await Sperre.WaitAsync(ct);
        try
        {
            var settings = _haSettings.GetEffectiveHomeAssistantSettings();
            if (!settings.IsConfigured) return await ErgebnisAsync(zeltId, false, "Home Assistant ist nicht verbunden — es wurde nichts geschaltet.", [], ct);

            var e = EinstellungenFuer(zeltId);
            if (!e.Ruhend) return await ErgebnisAsync(zeltId, false, "Das Zelt ist nicht ruhend.", [], ct);

            var ablauf = new ZeltRuheAblauf(_werkzeug, () => EinstellungenFuer(zeltId), e2 => Speichern(zeltId, e2), _logger, zeltId: zeltId);
            var schritte = await ablauf.AktivAsync(CancellationToken.None);

            var offen = schritte.Count(s => !s.Ok);
            var text = offen == 0
                ? "Zelt wieder aktiv: alles steht wieder wie vorher."
                : $"{schritte.Count - offen} von {schritte.Count} Geräten wieder aktiv — der Rest bleibt gemerkt, ein zweiter Versuch holt ihn nach.";
            return await ErgebnisAsync(zeltId, offen == 0, text, schritte, ct);
        }
        finally { Sperre.Release(); }
    }

    private async Task<ZeltRuheErgebnis> ErgebnisAsync(int? zeltId, bool ok, string zusammenfassung, IReadOnlyList<ZeltRuheSchritt> schritte, CancellationToken ct)
        => new(ok, zusammenfassung, schritte, await StandAsync(zeltId, ct));
}
