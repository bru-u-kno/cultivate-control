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

    private readonly SteuerungRepository _repo;
    private readonly SteuerungGeraeteService _geraete;
    private readonly HomeAssistantService _ha;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly GrowRepository _grows;
    private readonly Co2SteuerungService _co2;
    private readonly ZuluftSteuerungService _zuluft;
    private readonly ChillerSteuerungService _chiller;
    private readonly EntfeuchterSteuerungService _entfeuchter;
    private readonly EntfeuchterZusatzSteuerungService _zusatz;
    private readonly LichtWaechterService _waechter;
    private readonly AcSchreiber _schreiber;
    private readonly ILogger<ZeltRuheService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task>? _warten;

    public ZeltRuheService(
        SteuerungRepository repo,
        SteuerungGeraeteService geraete,
        HomeAssistantService ha,
        HomeAssistantSettingsRepository haSettings,
        GrowRepository grows,
        Co2SteuerungService co2,
        ZuluftSteuerungService zuluft,
        ChillerSteuerungService chiller,
        EntfeuchterSteuerungService entfeuchter,
        EntfeuchterZusatzSteuerungService zusatz,
        LichtWaechterService waechter,
        AcSchreiber schreiber,
        ILogger<ZeltRuheService> logger,
        Func<TimeSpan, CancellationToken, Task>? warten = null)
    {
        _repo = repo;
        _geraete = geraete;
        _ha = ha;
        _haSettings = haSettings;
        _grows = grows;
        _co2 = co2;
        _zuluft = zuluft;
        _chiller = chiller;
        _entfeuchter = entfeuchter;
        _zusatz = zusatz;
        _waechter = waechter;
        _schreiber = schreiber;
        _logger = logger;
        _warten = warten;
    }

    public ZeltRuheEinstellungen Einstellungen => _repo.GetEinstellungen<ZeltRuheEinstellungen>(Modul) ?? new ZeltRuheEinstellungen();

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
    public int? SteuerungsZeltId()
    {
        var alle = _grows.GetAllGrows().Where(g => g.TentId is not null).ToList();
        var laufend = alle.Where(g => !g.IsArchived).OrderByDescending(g => g.Id).FirstOrDefault();
        return (laufend ?? alle.OrderByDescending(g => g.Id).FirstOrDefault())?.TentId;
    }

    public async Task<ZeltRuheStand> StandAsync(CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var e = Einstellungen;
        var rollen = Rollen();
        var wirksam = Wirksam(e.Auswahl, rollen.Where(r => r.Value is not null).Select(r => r.Key).ToList());

        var geraete = new List<ZeltRuheGeraet>();
        var erreichbar = settings.IsConfigured;
        foreach (var art in ZeltRuheArten.Alle)
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
        return new ZeltRuheStand(e.Ruhend, e.SeitUtc, SteuerungsZeltId(), erreichbar, geraete);
    }

    private Dictionary<string, string?> Rollen()
    {
        var ergebnis = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var art in ZeltRuheArten.Alle) ergebnis[art.Kennung] = _geraete.Entity(art.Modul, art.Rolle);
        return ergebnis;
    }

    /// <summary>Speichert, was im Ruhemodus ausgehen soll. Unbekannte Kennungen werden abgelehnt.</summary>
    public (bool Ok, string? Fehler) AuswahlSpeichern(IReadOnlyList<string> kennungen)
    {
        var unbekannt = kennungen.FirstOrDefault(k => ZeltRuheArten.Finden(k) is null);
        if (unbekannt is not null) return (false, $"„{unbekannt}“ ist kein Gerät, das „Zelt ruhend“ kennt.");
        var e = Einstellungen;
        e.Auswahl = ZeltRuheArten.Alle.Where(a => kennungen.Contains(a.Kennung, StringComparer.OrdinalIgnoreCase)).Select(a => a.Kennung).ToList();
        _repo.SetEinstellungen(Modul, e);
        return (true, null);
    }

    // ------------------------------------------------------------------ Ruhend schalten

    /// <param name="kennungen">Die Geräte dieses Mal; <c>null</c>: die gespeicherte Auswahl. Wird sie so mitgeschickt, merkt sich der Fork sie als neue Auswahl.</param>
    public async Task<ZeltRuheErgebnis> RuhendAsync(IReadOnlyList<string>? kennungen, CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        if (!settings.IsConfigured) return await ErgebnisAsync(false, "Home Assistant ist nicht verbunden — es wurde nichts geschaltet.", [], ct);

        var e = Einstellungen;
        if (e.Ruhend) return await ErgebnisAsync(false, "Das Zelt ist schon ruhend. Erst „Zelt wieder aktiv“, dann lässt sich die Auswahl ändern.", [], ct);

        if (kennungen is not null)
        {
            var (ok, fehler) = AuswahlSpeichern(kennungen);
            if (!ok) return await ErgebnisAsync(false, fehler!, [], ct);
            e = Einstellungen;
        }

        var rollen = Rollen();
        var wirksam = Wirksam(e.Auswahl, rollen.Where(r => r.Value is not null).Select(r => r.Key).ToList());
        if (wirksam.Count == 0) return await ErgebnisAsync(false, "Es ist kein Gerät zum Ausschalten ausgewählt.", [], ct);

        var schritte = new List<ZeltRuheSchritt>();
        var gemerkt = new List<ZeltRuheMerker>();
        var lauf = new SchaltLauf();
        foreach (var kennung in wirksam)
        {
            var art = ZeltRuheArten.Finden(kennung)!;
            var entity = rollen[kennung]!;
            var (schritt, merker) = await GeraetAusschaltenAsync(settings, art, entity, lauf, ct);
            schritte.Add(schritt);
            if (merker is not null) gemerkt.Add(merker);
        }

        // Ohne Licht-Wächter-Pause meldete der Wächter jede Plan-Hell-Zeit als Abweichung.
        var waechterAngehalten = false;
        if (gemerkt.Any(m => m.Kennung == ZeltRuheArten.Licht))
        {
            _waechter.PausierenBis(DateTime.UtcNow + WaechterPause);
            waechterAngehalten = true;
        }

        e.Gemerkt = gemerkt;
        e.Ruhend = gemerkt.Count > 0;
        e.SeitUtc = gemerkt.Count > 0 ? DateTime.UtcNow : null;
        e.WaechterAngehalten = waechterAngehalten;
        _repo.SetEinstellungen(Modul, e);

        var geklappt = schritte.Count(s => s.Ok);
        var text = geklappt == schritte.Count
            ? $"Zelt ruhend: {geklappt} {(geklappt == 1 ? "Gerät" : "Geräte")} ausgeschaltet."
            : geklappt == 0
                ? "Nichts ausgeschaltet — kein Gerät hat geantwortet."
                : $"Zelt ruhend: {geklappt} von {schritte.Count} Geräten ausgeschaltet, der Rest steht unten.";
        return await ErgebnisAsync(geklappt > 0, text, schritte, ct);
    }

    private async Task<(ZeltRuheSchritt Schritt, ZeltRuheMerker? Merker)> GeraetAusschaltenAsync(
        HomeAssistantSettings settings, ZeltRuheArt art, string entity, SchaltLauf lauf, CancellationToken ct)
    {
        var stand = await _ha.GetEntityStateAsync(settings, entity, ct);
        var (_, laeuft) = Beschreiben(SteuerungGeraeteService.Domain(entity), stand?.State);
        if (laeuft is null)
            return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "antwortet nicht — nicht ausgeschaltet"), null);

        var aktion = Ausschalten(entity);
        if (aktion is null)
            return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "diese Art Gerät kann „Zelt ruhend“ nicht ausschalten"), null);

        // 1. Automatik aus. Gelingt das nicht, bleibt das Gerät unberührt — sonst schaltete sie es wieder ein.
        bool? automatikWarAn;
        try
        {
            (var ok, automatikWarAn) = await AutomatikAusAsync(art.Kennung, ct);
            if (!ok) return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "Automatik ließ sich nicht ausschalten — Gerät unberührt"), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Zelt ruhend: Automatik von {Kennung} nicht ausgeschaltet.", art.Kennung);
            return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "Automatik ließ sich nicht ausschalten — Gerät unberührt"), null);
        }

        // 2. Das Gerät selbst — bei AC Infinity mit Abstand und Nachkontrolle (die Wolke verwirft schnelle Folgebefehle).
        var merker = new ZeltRuheMerker { Kennung = art.Kennung, AutomatikWarAn = automatikWarAn, Entity = entity, Vorher = stand!.State };
        var ausgang = await SchaltenAsync(settings, entity, aktion.Value, AcModi.Aus, lauf, ct);
        switch (ausgang)
        {
            case Schaltausgang.Bestaetigt:
                return (new ZeltRuheSchritt(art.Kennung, art.Titel, true, automatikWarAn == true ? "Automatik und Gerät aus" : "ausgeschaltet"), merker);
            case Schaltausgang.Unbestaetigt:
                // Gesendet, das Gerät hat es noch nicht zurückgemeldet: gemerkt, damit „wieder aktiv" es zurückstellt.
                return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "gesendet, aber vom Gerät noch nicht bestätigt — bitte nachsehen"), merker);
            default:
                // Die Automatik ist schon aus: merken, damit „wieder aktiv" sie zurückstellt.
                if (automatikWarAn == true) return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "Gerät hat den Befehl nicht angenommen — nur die Automatik ist aus"), merker);
                return (new ZeltRuheSchritt(art.Kennung, art.Titel, false, "Gerät hat den Befehl nicht angenommen"), null);
        }
    }

    /// <summary>Ein Zustand des Schaltlaufs: wurde schon an einen AC-Infinity-Controller geschrieben? Dann wartet der nächste Befehl.</summary>
    private sealed class SchaltLauf
    {
        public bool AcGeschrieben { get; set; }
    }

    private enum Schaltausgang { Bestaetigt, Unbestaetigt, Abgelehnt }

    /// <summary>
    /// Führt den Dienstaufruf aus. Auswahlfelder (AC Infinity: Betriebsart des Ports) gehen durch den <see cref="AcSchreiber"/> —
    /// nacheinander, mit Pause und Nachlesen, weil die Wolke parallele Updates still verwirft. Steckdosen und Schalter
    /// (Shelly, Helfer) sind lokal und gehen direkt.
    /// </summary>
    private async Task<Schaltausgang> SchaltenAsync(
        HomeAssistantSettings settings, string entity,
        (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten) aktion, string? soll, SchaltLauf lauf, CancellationToken ct)
    {
        if (string.Equals(aktion.Domain, "select", StringComparison.OrdinalIgnoreCase) && soll is not null)
        {
            if (lauf.AcGeschrieben) await (_warten ?? Task.Delay)(AcSchreiber.Pause, ct);
            lauf.AcGeschrieben = true;
            var schritt = new AcSchreibschritt(entity, aktion.Domain, aktion.Dienst, aktion.Daten ?? new Dictionary<string, object>(), soll);
            var ergebnis = (await _schreiber.SchreibenAsync(settings, [schritt], _warten, ct)).FirstOrDefault();
            if (ergebnis is null) return Schaltausgang.Abgelehnt;
            return ergebnis.Bestaetigt ? Schaltausgang.Bestaetigt : ergebnis.Angenommen ? Schaltausgang.Unbestaetigt : Schaltausgang.Abgelehnt;
        }

        var ok = await _ha.CallEntityServiceAsync(settings, aktion.Domain, aktion.Dienst, entity, ct, aktion.Daten);
        return ok ? Schaltausgang.Bestaetigt : Schaltausgang.Abgelehnt;
    }

    // ------------------------------------------------------------------ Wieder aktiv

    public async Task<ZeltRuheErgebnis> AktivAsync(CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        if (!settings.IsConfigured) return await ErgebnisAsync(false, "Home Assistant ist nicht verbunden — es wurde nichts geschaltet.", [], ct);

        var e = Einstellungen;
        if (!e.Ruhend) return await ErgebnisAsync(false, "Das Zelt ist nicht ruhend.", [], ct);

        var schritte = new List<ZeltRuheSchritt>();
        var offen = new List<ZeltRuheMerker>();
        var lauf = new SchaltLauf();
        foreach (var merker in e.Gemerkt)
        {
            var art = ZeltRuheArten.Finden(merker.Kennung);
            var titel = art?.Titel ?? merker.Kennung;

            // 1. Das Gerät in den Zustand von vorher.
            var geraetOk = true;
            var geraetText = "Gerät hat den Befehl nicht angenommen";
            if (merker.Entity is not null && Wiederherstellen(merker.Entity, merker.Vorher) is { } aktion)
            {
                var ausgang = await SchaltenAsync(settings, merker.Entity, aktion, merker.Vorher, lauf, ct);
                geraetOk = ausgang == Schaltausgang.Bestaetigt;
                if (ausgang == Schaltausgang.Unbestaetigt) geraetText = "gesendet, aber vom Gerät noch nicht bestätigt";
            }

            // 2. Die Automatik wieder an — nur, wenn sie vorher an war.
            var automatikOk = true;
            if (merker.AutomatikWarAn == true)
            {
                try { automatikOk = await AutomatikAnAsync(merker.Kennung, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Zelt wieder aktiv: Automatik von {Kennung} nicht eingeschaltet.", merker.Kennung);
                    automatikOk = false;
                }
            }

            if (geraetOk && automatikOk)
            {
                schritte.Add(new ZeltRuheSchritt(merker.Kennung, titel, true, "wieder wie vorher"));
            }
            else
            {
                schritte.Add(new ZeltRuheSchritt(merker.Kennung, titel, false, geraetOk ? "Automatik ließ sich nicht einschalten" : geraetText));
                offen.Add(merker);
            }
        }

        if (e.WaechterAngehalten) _waechter.Pausieren(0);

        // Was nicht geklappt hat, bleibt gemerkt: ein zweiter Versuch holt es nach, statt es zu vergessen.
        e.Gemerkt = offen;
        e.Ruhend = offen.Count > 0;
        e.SeitUtc = offen.Count > 0 ? e.SeitUtc : null;
        e.WaechterAngehalten = false;
        _repo.SetEinstellungen(Modul, e);

        var text = offen.Count == 0
            ? "Zelt wieder aktiv: alles steht wieder wie vorher."
            : $"{schritte.Count - offen.Count} von {schritte.Count} Geräten wieder eingeschaltet — der Rest bleibt gemerkt, ein zweiter Versuch holt ihn nach.";
        return await ErgebnisAsync(offen.Count == 0, text, schritte, ct);
    }

    // ------------------------------------------------------------------ Automatik je Gerät

    /// <summary>Schaltet die Automatik eines Geräts aus. Liefert, ob das geklappt hat, und ob sie vorher an war (<c>null</c>: das Gerät hat keine).</summary>
    private async Task<(bool Ok, bool? WarAn)> AutomatikAusAsync(string kennung, CancellationToken ct)
    {
        switch (kennung)
        {
            case ZeltRuheArten.Co2:
            {
                var e = _co2.Einstellungen;
                var vorher = e.AutomatikAktiv;
                if (!vorher) return (true, false);
                e.AutomatikAktiv = false;
                var (gespeichert, _, erreicht) = await _co2.SpeichernAsync(e, ct);
                return (gespeichert is not null && erreicht, vorher);
            }
            case ZeltRuheArten.Zuluft:
            {
                var e = await _zuluft.EinstellungenAsync(ct);
                var vorher = e.AutomatikAktiv;
                if (!vorher) return (true, false);
                e.AutomatikAktiv = false;
                var (gespeichert, _, erreicht) = await _zuluft.SpeichernAsync(e, ct);
                return (gespeichert is not null && erreicht, vorher);
            }
            case ZeltRuheArten.Chiller:
            {
                var e = await _chiller.EinstellungenAsync(ct);
                var vorher = e.AutomatikAktiv;
                if (!vorher) return (true, false);
                e.AutomatikAktiv = false;
                var (gespeichert, _, erreicht) = await _chiller.SpeichernAsync(e, ct);
                return (gespeichert is not null && erreicht, vorher);
            }
            case ZeltRuheArten.Entfeuchter:
            {
                var e = await _entfeuchter.EinstellungenAsync(ct);
                var vorher = e.AutomatikAktiv;
                if (!vorher) return (true, false);
                e.AutomatikAktiv = false;
                var (gespeichert, _, erreicht) = await _entfeuchter.SpeichernAsync(e, ct);
                return (gespeichert is not null && erreicht, vorher);
            }
            case ZeltRuheArten.Zusatz:
            {
                var e = await _zusatz.EinstellungenAsync(ct);
                var vorher = e.AutomatikAktiv;
                if (!vorher) return (true, false);
                var ergebnis = await _zusatz.SpeichernAsync(new EntfeuchterZusatzAenderung { AutomatikAktiv = false }, ct);
                return (ergebnis.Gespeichert is not null && ergebnis.HaErreicht == true, vorher);
            }
            default:
                // Licht und Abluft haben keine eigene Automatik: das Gerät führt Zeitplan oder Stufe selbst.
                return (true, null);
        }
    }

    private async Task<bool> AutomatikAnAsync(string kennung, CancellationToken ct)
    {
        switch (kennung)
        {
            case ZeltRuheArten.Co2:
            {
                var e = _co2.Einstellungen;
                e.AutomatikAktiv = true;
                var (gespeichert, _, erreicht) = await _co2.SpeichernAsync(e, ct);
                return gespeichert is not null && erreicht;
            }
            case ZeltRuheArten.Zuluft:
            {
                var e = await _zuluft.EinstellungenAsync(ct);
                e.AutomatikAktiv = true;
                var (gespeichert, _, erreicht) = await _zuluft.SpeichernAsync(e, ct);
                return gespeichert is not null && erreicht;
            }
            case ZeltRuheArten.Chiller:
            {
                var e = await _chiller.EinstellungenAsync(ct);
                e.AutomatikAktiv = true;
                var (gespeichert, _, erreicht) = await _chiller.SpeichernAsync(e, ct);
                return gespeichert is not null && erreicht;
            }
            case ZeltRuheArten.Entfeuchter:
            {
                var e = await _entfeuchter.EinstellungenAsync(ct);
                e.AutomatikAktiv = true;
                var (gespeichert, _, erreicht) = await _entfeuchter.SpeichernAsync(e, ct);
                return gespeichert is not null && erreicht;
            }
            case ZeltRuheArten.Zusatz:
            {
                var ergebnis = await _zusatz.SpeichernAsync(new EntfeuchterZusatzAenderung { AutomatikAktiv = true }, ct);
                return ergebnis.Gespeichert is not null && ergebnis.HaErreicht == true;
            }
            default:
                return true;
        }
    }

    private async Task<ZeltRuheErgebnis> ErgebnisAsync(bool ok, string zusammenfassung, IReadOnlyList<ZeltRuheSchritt> schritte, CancellationToken ct)
        => new(ok, zusammenfassung, schritte, await StandAsync(ct));
}
