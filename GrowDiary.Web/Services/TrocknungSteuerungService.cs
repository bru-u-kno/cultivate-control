using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Geräte am Trockenzelt folgen dem Plan (A-029, Etappe 3, Teil B): schreibt die Grenzen der Auslöser an den Ports des
/// Controllers auf den oberen Rand des Bands der Etappe — nur für die Rollen, die der Nutzer eingeschaltet hat, und nur
/// in der Betriebsart „Regeln“.
/// </summary>
/// <remarks>
/// <para><b>Was nie geschrieben wird:</b> der Modus des Ports und die Ein-Schalter der Auslöser. Der Controller regelt
/// weiter selbst; der Fork verschiebt nur die Grenze. Steht ein Port auf „Aus“, wirkt die Grenze erst, wenn der Nutzer ihn
/// auf Auto stellt.</para>
/// <para><b>Gleichmäßig und wiederholbar.</b> Jeder Durchlauf vergleicht Ist und Plan und schreibt nur bei Abweichung —
/// so zieht der Fork beim Start und bei jedem Etappenwechsel nach, ohne zu zählen, und ein zweiter Durchlauf ist ein
/// Nichtstun.</para>
/// </remarks>
public sealed class TrocknungSteuerungService
{
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPlanQuelle _plan;
    private readonly TrocknungPortRepository _ports;
    private readonly TrocknungSteuerungRepository _steuerung;
    private readonly TrocknungZielService _ziel;
    private readonly ILogger<TrocknungSteuerungService> _logger;
    // Der Push-Dienst lebt je Anfrage (scoped), dieser Dienst als Einzelstück: er holt sich den Push-Dienst erst beim Senden.
    private readonly IServiceScopeFactory? _bereiche;

    public TrocknungSteuerungService(
        GrowRepository grows, HarvestRepository ernten, TrocknungPlanQuelle plan, TrocknungPortRepository ports,
        TrocknungSteuerungRepository steuerung, TrocknungZielService ziel, ILogger<TrocknungSteuerungService> logger,
        IServiceScopeFactory? bereiche = null)
    {
        _bereiche = bereiche;
        _grows = grows;
        _ernten = ernten;
        _plan = plan;
        _ports = ports;
        _steuerung = steuerung;
        _ziel = ziel;
        _logger = logger;
    }

    public sealed record Geraet(
        string Rolle, string? PortName, bool Folgt, string? Modus, double? WertAmGeraet, string? Einheit, double? PlanWert,
        string Urteil, double? Vorher, DateTime? ZuletztGesetztUtc, SchaltStand? Schalten = null, StufeStand? Stufe = null);

    /// <summary>Die Abluft mit Mindeststufe (nur bei der Abluft, solange sie eingebunden ist und eine Mindeststufe gilt).</summary>
    public sealed record StufeStand(int Mindest, double? Jetzt, string Zustand, string? Modus, bool Gescheitert);

    /// <summary>Der Zustand des Entfeuchters, den der Fork schaltet (nur Rolle Entfeuchter): Schwellen, Luftfeuchte, letzter Schaltvorgang.</summary>
    public sealed record SchaltStand(
        string Zustand, double? Feuchte, double? EinAb, double? AusUnter, string? Modus, DateTime? ZuletztUtc, string? ZuletztAktion, bool Gescheitert);

    /// <summary>Ein Vorgang läuft gerade (Speichern der Haken, Abgleich mit dem Controller): damit sehen auch andere Benutzer, dass die Seite noch arbeitet.</summary>
    public sealed record Arbeit(string Phase, DateTime SeitUtc, string? ClientId);

    public const string PhaseSpeichern = "speichern";
    public const string PhaseAbgleichen = "abgleichen";
    public const string PhasePruefen = "pruefen";

    // Eine Sicherung gegen einen hängenden Vorgang: nach so langer Zeit zeigt der Stand ihn nicht mehr an.
    private static readonly TimeSpan ArbeitHoechstens = TimeSpan.FromMinutes(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Arbeit> _arbeit = new();

    /// <summary>Meldet, dass für das Zelt ein Vorgang läuft. Das Ende (Dispose) nimmt die Meldung zurück.</summary>
    public ArbeitsVorgang ArbeitBeginnen(int tentId, string? clientId)
    {
        var meine = new Arbeit(PhaseSpeichern, DateTime.UtcNow, ClientIdPruefen(clientId));
        _arbeit[tentId] = meine;
        return new ArbeitsVorgang(this, tentId, meine);
    }

    /// <summary>Die Kennung der Seite ist frei gewählt und nur zum Wiedererkennen: kurz und aus einfachen Zeichen, sonst wird sie verworfen.</summary>
    public static string? ClientIdPruefen(string? clientId)
        => clientId is { Length: > 0 and <= 40 } && clientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? clientId : null;

    private Arbeit? AktuelleArbeit(int tentId)
        => _arbeit.TryGetValue(tentId, out var a) && DateTime.UtcNow - a.SeitUtc < ArbeitHoechstens ? a : null;

    /// <summary>Ein laufender Vorgang. Phase und Ende betreffen nur diesen Vorgang, nie den eines anderen Benutzers im selben Zelt.</summary>
    public sealed class ArbeitsVorgang : IDisposable
    {
        private readonly TrocknungSteuerungService _dienst;
        private readonly int _tentId;
        private Arbeit _aktuell;

        internal ArbeitsVorgang(TrocknungSteuerungService dienst, int tentId, Arbeit anfang)
        {
            _dienst = dienst;
            _tentId = tentId;
            _aktuell = anfang;
        }

        public void Phase(string phase)
        {
            var neu = _aktuell with { Phase = phase };
            // Nur ersetzen, wenn der Eintrag noch dieser ist: ein anderer Vorgang oder das Aufräumen dazwischen bleibt unberührt.
            if (_dienst._arbeit.TryUpdate(_tentId, neu, _aktuell)) _aktuell = neu;
        }

        public void Dispose() => _dienst._arbeit.TryRemove(new KeyValuePair<int, Arbeit>(_tentId, _aktuell));
    }

    public sealed record Stand(
        bool Trocknet, string Betrieb, bool PortsErreichbar, int? Tag, int? TageGeplant, string? Etappe,
        double? TempMin, double? TempMax, double? FeuchteMin, double? FeuchteMax, IReadOnlyList<Geraet> Geraete, Arbeit? InArbeit = null,
        int AbluftMinStufe = 0);

    // Die Abluft hat einen Port, aber zwei Grenzen: jede ist hier eine eigene Rolle (eigener Wert, eigener Merker).
    private static readonly string[] Rollen =
        [TrocknungZiel.RolleEntfeuchter, TrocknungZiel.RolleKuehlung, TrocknungZiel.RolleAbluftFeuchte, TrocknungZiel.RolleAbluftTemp];

    /// <summary>Der Stand, wie die Seite ihn zeigt. Schreibt nichts.</summary>
    public async Task<Stand> StandAsync(int tentId, CancellationToken ct)
    {
        var einstellung = _steuerung.Get(tentId);
        var merker = _steuerung.Merkers(tentId);
        var zuordnung = _ports.Get(tentId);
        DryingWindow.Stand? trocknung = DryingWindow.StandFor(_grows, _ernten, tentId, DateTime.Today);
        int? geplant = null; string? etappeName = null; TrocknungsPlan.Band? band = null;
        if (trocknung is { } t)
        {
            var plan = _plan.Fuer(tentId, t);
            geplant = plan.Geplant;
            etappeName = plan.Etappe?.Etappe.Name;
            band = plan.Band;
        }

        var ports = zuordnung.Count == 0 ? [] : await _ziel.PortsAsync(frisch: false, ct);
        var erreichbar = zuordnung.Count == 0 || ports is not null;
        var geraete = new List<Geraet>();
        foreach (var rolle in Rollen)
        {
            merker.TryGetValue(rolle, out var m);
            var folgt = einstellung.Folgt(rolle);
            var planGrenze = TrocknungZiel.IstFeuchte(rolle) ? band?.FeuchteMax : band?.TempMax;
            if (!zuordnung.TryGetValue(TrocknungZiel.PortRolle(rolle), out var deviceId))
            {
                geraete.Add(new Geraet(rolle, null, folgt, null, null, null, planGrenze, "nichtZugeordnet", m?.Vorher, m?.GesetztAtUtc));
                continue;
            }

            var port = ports?.FirstOrDefault(p => p.DeviceId == deviceId);
            if (port is null)
            {
                geraete.Add(new Geraet(rolle, null, folgt, null, null, null, planGrenze, "unbekannt", m?.Vorher, m?.GesetztAtUtc));
                continue;
            }

            if (rolle == TrocknungZiel.RolleEntfeuchter && folgt && trocknung is { } laufend)
            {
                // Der Entfeuchter wird geschaltet, nicht über einen Grenzwert geführt: Zustand und Schwellen statt „Wert am Gerät“.
                var lage = await SchaltLageAsync(tentId, port, laufend, ct);
                var schalt = SchaltStandAus(tentId, lage);
                geraete.Add(new Geraet(rolle, port.Name, folgt, lage.Modus, null, null, planGrenze, SchaltUrteil(schalt), m?.Vorher, m?.GesetztAtUtc, schalt));
                continue;
            }

            var wunsch = await WunschAsync(rolle, port, planGrenze, ct);
            StufeStand? stufeStand = null;
            var urteil = wunsch.Urteil;
            if (TrocknungZiel.PortRolle(rolle) == TrocknungZiel.RolleAbluft && folgt && einstellung.AbluftMinStufe > 0)
            {
                var lage = await StufeLageAsync(port, ct);
                stufeStand = StufeStandAus(tentId, einstellung.AbluftMinStufe, lage);
                // Im Modus On und Off gibt es keine Auslöser: dort gilt die Mindeststufe. Im Modus Auto bleibt das Urteil der Grenzen.
                if (!string.Equals(lage.Modus, "Auto", StringComparison.OrdinalIgnoreCase)) urteil = StufeUrteil(stufeStand);
            }

            geraete.Add(new Geraet(rolle, port.Name, folgt, wunsch.Modus, wunsch.Jetzt, wunsch.Einheit, wunsch.Plan ?? planGrenze, urteil, m?.Vorher, m?.GesetztAtUtc, null, stufeStand));
        }

        return new Stand(
            trocknung is not null, einstellung.Betrieb, erreichbar, trocknung?.Tag, geplant, etappeName,
            band?.TempMin, band?.TempMax, band?.FeuchteMin, band?.FeuchteMax, geraete, AktuelleArbeit(tentId), einstellung.AbluftMinStufe);
    }

    private async Task<TrocknungZiel.Wunsch> WunschAsync(string rolle, TrocknungZiel.Port port, double? planGrenze, CancellationToken ct)
    {
        var entitaeten = new[] { port.ModusEntity, port.FeuchteWertEntity, port.TempWertEntity }
            .Where(e => e is not null).Select(e => e!).Distinct().ToList();
        var zustaende = await Task.WhenAll(entitaeten.Select(async e => (e, Zustand: await _ziel.ZustandAsync(e, ct))));
        var nachId = zustaende.ToDictionary(z => z.e, z => z.Zustand, StringComparer.OrdinalIgnoreCase);
        return TrocknungZiel.Entscheide(rolle, port, e => nachId.GetValueOrDefault(e), planGrenze);
    }

    // ------------------------------------------------------------------ Entfeuchter schalten

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTime Utc, string Aktion)> _zuletztGeschaltet = new();
    // Schlüssel: Zelt * 10 + Art (1 = Entfeuchter, 2 = Abluft), damit sich beide Störungen nicht gegenseitig zurücksetzen.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _problemZyklen = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _letzterSchaltFehler = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _problemGemeldet = new();
    private static int Schluessel(int tentId, int art) => tentId * 10 + art;

    /// <summary>Wie viele Läufe (je eine Minute) ein Problem dauern muss, bis der Fork es per Push meldet.</summary>
    public const int ProblemBisPush = 10;

    private sealed record SchaltLage(double? Feuchte, string? Modus, double? EinAb, double? AusUnter);

    private async Task<SchaltLage> SchaltLageAsync(int tentId, TrocknungZiel.Port port, DryingWindow.Stand trocknung, CancellationToken ct)
    {
        var etappe = _plan.Fuer(tentId, trocknung);
        double? einAb = null, ausUnter = null;
        if (etappe.Etappe is { } e && etappe.Band is { } b)
        {
            var (ein, aus) = TrocknungSchalten.Schwellen(e.Etappe.LuftfeuchteZiel, b.FeuchteMin);
            einAb = ein;
            ausUnter = aus;
        }

        var modus = (await _ziel.ZustandAsync(port.ModusEntity, ct))?.State;
        var sensor = _grows.GetTent(tentId)?.Sensors.FirstOrDefault(x => x.MetricType == SensorMetricType.Humidity && x.IsActive && x.HaEntityId.Length > 0);
        double? feuchte = sensor is null ? null : Zahlenlesen.Maschine((await _ziel.ZustandAsync(sensor.HaEntityId, ct))?.State);
        return new SchaltLage(feuchte, modus, einAb, ausUnter);
    }

    private SchaltStand SchaltStandAus(int tentId, SchaltLage lage)
    {
        _zuletztGeschaltet.TryGetValue(tentId, out var z);
        var entscheidung = lage.EinAb is { } ein && lage.AusUnter is { } aus
            ? TrocknungSchalten.Entscheide(lage.Feuchte, lage.Modus, ein, aus, z == default ? null : z.Utc, DateTime.UtcNow)
            : new TrocknungSchalten.Entscheidung(TrocknungSchalten.ZustandKeinMesswert, TrocknungSchalten.Aktion.Nichts);
        return new SchaltStand(
            entscheidung.Zustand, lage.Feuchte, lage.EinAb, lage.AusUnter, lage.Modus,
            z == default ? null : z.Utc, z == default ? null : z.Aktion, _letzterSchaltFehler.GetValueOrDefault(tentId));
    }

    private static string SchaltUrteil(SchaltStand s) => s.Gescheitert ? "schaltetNicht" : s.Zustand switch
    {
        TrocknungSchalten.ZustandKeinMesswert => "keinMesswert",
        TrocknungSchalten.ZustandPortNichtSchaltbar => "portNichtSchaltbar",
        _ => "schaltet",
    };

    /// <summary>
    /// Ein Schaltlauf (jede Minute): schaltet den Entfeuchter an den Planwerten ein und aus, nur im Betrieb „Regeln“, nur mit „eingebunden“,
    /// nur während eine Charge trocknet. Gibt zurück, ob geschaltet wurde. Ein Problem, das länger als <see cref="ProblemBisPush"/> Läufe dauert, geht einmal als Push raus.
    /// </summary>
    public async Task<bool> SchaltenAsync(int tentId, CancellationToken ct)
    {
        var einstellung = _steuerung.Get(tentId);
        if (einstellung.Betrieb != TrocknungSteuerungRepository.BetriebRegeln || !einstellung.EntfeuchterFolgt) { Erholt(tentId); return false; }
        if (DryingWindow.StandFor(_grows, _ernten, tentId, DateTime.Today) is not { } trocknung) { Erholt(tentId); return false; }
        if (!_ports.Get(tentId).TryGetValue(TrocknungZiel.RolleEntfeuchter, out var deviceId)) return false;
        var ports = await _ziel.PortsAsync(frisch: false, ct);
        if (ports?.FirstOrDefault(p => p.DeviceId == deviceId) is not { } port) { await ProblemAsync(tentId, "Home Assistant liefert den Port des Entfeuchters nicht.", ct); return false; }

        var lage = await SchaltLageAsync(tentId, port, trocknung, ct);
        if (lage.EinAb is not { } ein || lage.AusUnter is not { } aus) return false;
        _zuletztGeschaltet.TryGetValue(tentId, out var z);
        var entscheidung = TrocknungSchalten.Entscheide(lage.Feuchte, lage.Modus, ein, aus, z == default ? null : z.Utc, DateTime.UtcNow);

        if (entscheidung.Aktion == TrocknungSchalten.Aktion.Nichts)
        {
            _letzterSchaltFehler[tentId] = false;
            if (entscheidung.Zustand is TrocknungSchalten.ZustandPortNichtSchaltbar or TrocknungSchalten.ZustandKeinMesswert)
                await ProblemAsync(tentId, entscheidung.Zustand == TrocknungSchalten.ZustandPortNichtSchaltbar
                    ? $"Der Port „{port.Name}“ steht auf {lage.Modus ?? "unbekannt"}: Der Fork kann den Entfeuchter nur schalten, wenn der Port auf On oder Off steht."
                    : "Die Luftfeuchte im Trockenzelt lässt sich nicht lesen: Der Entfeuchter bleibt, wie er ist.", ct);
            else Erholt(tentId);
            return false;
        }

        var option = entscheidung.Aktion == TrocknungSchalten.Aktion.Einschalten ? "On" : "Off";
        if (await _ziel.SetzeModusAsync(port.ModusEntity, option, ct))
        {
            _zuletztGeschaltet[tentId] = (DateTime.UtcNow, option == "On" ? "ein" : "aus");
            _letzterSchaltFehler[tentId] = false;
            Erholt(tentId);
            _logger.LogInformation("Trocknung Zelt {Zelt}: Entfeuchter {Port} auf {Option} (Luftfeuchte {Feuchte} %, ein ab {Ein}, aus unter {Aus}).", tentId, port.Name, option, lage.Feuchte, ein, aus);
            return true;
        }

        _letzterSchaltFehler[tentId] = true;
        _logger.LogWarning("Trocknung Zelt {Zelt}: Entfeuchter {Port} ließ sich nicht auf {Option} schalten.", tentId, port.Name, option);
        await ProblemAsync(tentId, $"Der Entfeuchter „{port.Name}“ ließ sich nicht auf {option} schalten. Die Cloud des Controllers hat den Befehl nicht angenommen.", ct);
        return false;
    }

    private void Erholt(int tentId, int art = 1)
    {
        _problemZyklen.TryRemove(Schluessel(tentId, art), out _);
        _problemGemeldet.TryRemove(Schluessel(tentId, art), out _);
    }

    private async Task ProblemAsync(int tentId, string text, CancellationToken ct, int art = 1)
    {
        var schluessel = Schluessel(tentId, art);
        var zyklen = _problemZyklen.AddOrUpdate(schluessel, 1, (_, n) => n + 1);
        if (zyklen < ProblemBisPush || _problemGemeldet.GetValueOrDefault(schluessel) || _bereiche is null) return;
        _problemGemeldet[schluessel] = true;
        try
        {
            using var bereich = _bereiche.CreateScope();
            await bereich.ServiceProvider.GetRequiredService<NotificationService>().SendAsync(NotificationCategory.System, art == 2 ? "Trockenzelt: Abluft unter der Mindeststufe" : "Trockenzelt: Entfeuchter wird nicht geschaltet", text, ct, trotzRuhezeit: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Trocknung Zelt {Zelt}: Push zur Störung des Entfeuchters nicht gesendet.", tentId);
        }
    }

    // ------------------------------------------------------------------ Mindeststufe der Abluft

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _letzterStufeFehler = new();

    private sealed record StufeLage(string? Modus, double? Stufe);

    private async Task<StufeLage> StufeLageAsync(TrocknungZiel.Port port, CancellationToken ct)
    {
        var modus = (await _ziel.ZustandAsync(port.ModusEntity, ct))?.State;
        double? stufe = port.StufeEntity is null ? null : Zahlenlesen.Maschine((await _ziel.ZustandAsync(port.StufeEntity, ct))?.State);
        return new StufeLage(modus, stufe);
    }

    private StufeStand StufeStandAus(int tentId, int mindest, StufeLage lage)
    {
        var e = TrocknungAbluftStufe.Entscheide(mindest, lage.Modus, lage.Stufe);
        return new StufeStand(mindest, lage.Stufe, e.Zustand, lage.Modus, _letzterStufeFehler.GetValueOrDefault(tentId));
    }

    private static string StufeUrteil(StufeStand s) => s.Gescheitert ? "stufeNichtAnhebbar" : s.Zustand switch
    {
        TrocknungAbluftStufe.ZustandHaelt => "haeltStufe",
        TrocknungAbluftStufe.ZustandHebtAn => "hebtStufeAn",
        TrocknungAbluftStufe.ZustandKeinWert => "stufeKeinWert",
        _ => "stufePortNichtOn",
    };

    /// <summary>
    /// Ein Lauf (jede Minute): Liegt die Abluft unter der Mindeststufe, hebt der Fork sie auf die Mindeststufe an. Nur in „Regeln“, nur eingebunden,
    /// nur im Modus On, nur während eine Charge trocknet. Nach oben und am Modus ändert er nichts. Gibt zurück, ob geschrieben wurde.
    /// </summary>
    public async Task<bool> AbluftStufeAsync(int tentId, CancellationToken ct)
    {
        const int art = 2;
        var einstellung = _steuerung.Get(tentId);
        if (einstellung.Betrieb != TrocknungSteuerungRepository.BetriebRegeln || !einstellung.AbluftFolgt || einstellung.AbluftMinStufe <= 0) { Erholt(tentId, art); return false; }
        if (DryingWindow.StandFor(_grows, _ernten, tentId, DateTime.Today) is null) { Erholt(tentId, art); return false; }
        if (!_ports.Get(tentId).TryGetValue(TrocknungZiel.RolleAbluft, out var deviceId)) return false;
        var ports = await _ziel.PortsAsync(frisch: false, ct);
        if (ports?.FirstOrDefault(p => p.DeviceId == deviceId) is not { } port) { await ProblemAsync(tentId, "Home Assistant liefert den Port der Abluft nicht.", ct, art); return false; }

        var lage = await StufeLageAsync(port, ct);
        var e = TrocknungAbluftStufe.Entscheide(einstellung.AbluftMinStufe, lage.Modus, lage.Stufe);
        // Im Modus Auto regeln die Grenzen der Abluft, nicht die Stufe: dort gibt es nichts zu sichern und nichts zu melden.
        if (string.Equals(lage.Modus, "Auto", StringComparison.OrdinalIgnoreCase)) { _letzterStufeFehler[tentId] = false; Erholt(tentId, art); return false; }
        if (e.Aktion == TrocknungAbluftStufe.Aktion.Nichts)
        {
            _letzterStufeFehler[tentId] = false;
            if (e.Zustand is TrocknungAbluftStufe.ZustandPortNichtOn or TrocknungAbluftStufe.ZustandKeinWert)
                await ProblemAsync(tentId, e.Zustand == TrocknungAbluftStufe.ZustandPortNichtOn
                    ? $"Der Port „{port.Name}“ steht auf {lage.Modus ?? "unbekannt"}: Die Abluft läuft nicht und die Mindeststufe {einstellung.AbluftMinStufe} wirkt nicht. Stelle den Port am Controller auf On."
                    : "Die Stufe der Abluft lässt sich nicht lesen: Die Mindeststufe kann nicht geprüft werden.", ct, art);
            else Erholt(tentId, art);
            return false;
        }

        if (port.StufeEntity is { } entity && await _ziel.SetzeZahlAsync(entity, einstellung.AbluftMinStufe, ct))
        {
            _letzterStufeFehler[tentId] = false;
            Erholt(tentId, art);
            _logger.LogInformation("Trocknung Zelt {Zelt}: Abluft {Port} auf Stufe {Neu} angehoben (war {Alt}, Mindeststufe {Min}).", tentId, port.Name, einstellung.AbluftMinStufe, lage.Stufe, einstellung.AbluftMinStufe);
            return true;
        }

        _letzterStufeFehler[tentId] = true;
        _logger.LogWarning("Trocknung Zelt {Zelt}: Abluft {Port} ließ sich nicht auf Stufe {Neu} anheben.", tentId, port.Name, einstellung.AbluftMinStufe);
        await ProblemAsync(tentId, $"Die Abluft „{port.Name}“ ließ sich nicht auf die Mindeststufe {einstellung.AbluftMinStufe} anheben. Die Cloud des Controllers hat den Befehl nicht angenommen.", ct, art);
        return false;
    }

    /// <summary>Anzahl der Grenzen, die dieser Durchlauf an Home Assistant geschrieben hat.</summary>
    public sealed record Abgleich(int Geschrieben, int Gescheitert);

    /// <summary>
    /// Ein Durchlauf für ein Zelt. Nur in der Betriebsart „Regeln“, nur während eine Charge trocknet, nur für Rollen mit
    /// „Folgt dem Plan“ und Port. Merkt sich beim ersten Mal den Wert davor.
    /// </summary>
    public async Task<Abgleich> AbgleichenAsync(int tentId, CancellationToken ct)
    {
        var einstellung = _steuerung.Get(tentId);
        if (einstellung.Betrieb != TrocknungSteuerungRepository.BetriebRegeln) return new Abgleich(0, 0);
        if (DryingWindow.StandFor(_grows, _ernten, tentId, DateTime.Today) is not { } trocknung) return new Abgleich(0, 0);
        var zuordnung = _ports.Get(tentId);
        if (zuordnung.Count == 0) return new Abgleich(0, 0);
        var ports = await _ziel.PortsAsync(frisch: false, ct);
        if (ports is null) return new Abgleich(0, 0);

        var band = _plan.Fuer(tentId, trocknung).Band;
        var merker = _steuerung.Merkers(tentId);
        int geschrieben = 0, gescheitert = 0;
        foreach (var rolle in Rollen)
        {
            // Der Entfeuchter wird nicht über einen Grenzwert geführt, sondern vom Schaltlauf ein- und ausgeschaltet.
            if (rolle == TrocknungZiel.RolleEntfeuchter) continue;
            if (!einstellung.Folgt(rolle) || !zuordnung.TryGetValue(TrocknungZiel.PortRolle(rolle), out var deviceId)) continue;
            var port = ports.FirstOrDefault(p => p.DeviceId == deviceId);
            if (port is null) continue;

            var planGrenze = TrocknungZiel.IstFeuchte(rolle) ? band?.FeuchteMax : band?.TempMax;
            var wunsch = await WunschAsync(rolle, port, planGrenze, ct);
            if (!wunsch.Setzen) continue;

            if (await _ziel.SetzeZahlAsync(wunsch.Entity!, wunsch.Plan!.Value, ct))
            {
                // Nur beim ersten Mal den Wert davor festhalten (siehe Repository).
                _steuerung.Gesetzt(tentId, rolle, merker.TryGetValue(rolle, out var m) ? m.Vorher : wunsch.Jetzt!.Value, wunsch.Plan.Value);
                geschrieben++;
                _logger.LogInformation("Trocknung Zelt {Zelt}: {Rolle} {Entity} {Vorher} → {Neu}.", tentId, rolle, wunsch.Entity, wunsch.Jetzt, wunsch.Plan);
            }
            else
            {
                gescheitert++;
                _logger.LogWarning("Trocknung Zelt {Zelt}: {Entity} ließ sich nicht auf {Neu} setzen.", tentId, wunsch.Entity, wunsch.Plan);
            }
        }

        return new Abgleich(geschrieben, gescheitert);
    }

    /// <summary>
    /// „Alte Werte zurück“: schreibt je Rolle mit Merker den Wert davor wieder an den Port und stellt auf „Nur melden“.
    /// Eine Rolle, die sich nicht zurücksetzen lässt, behält ihren Merker (zweiter Versuch möglich).
    /// </summary>
    public async Task<Abgleich> ZuruecksetzenAsync(int tentId, CancellationToken ct)
    {
        var merker = _steuerung.Merkers(tentId);
        var zuordnung = _ports.Get(tentId);
        var ports = zuordnung.Count == 0 ? null : await _ziel.PortsAsync(frisch: true, ct);
        var einstellung = _steuerung.Get(tentId);
        int geschrieben = 0, gescheitert = 0;
        foreach (var rolle in Rollen)
        {
            if (!merker.TryGetValue(rolle, out var m)) continue;
            var port = zuordnung.TryGetValue(TrocknungZiel.PortRolle(rolle), out var deviceId) ? ports?.FirstOrDefault(p => p.DeviceId == deviceId) : null;
            var entity = port is null ? null : TrocknungZiel.IstFeuchte(rolle) ? port.FeuchteWertEntity : port.TempWertEntity;
            if (entity is not null && await _ziel.SetzeZahlAsync(entity, m.Vorher, ct))
            {
                _steuerung.MerkerLoeschen(tentId, rolle);
                einstellung = rolle switch
                {
                    TrocknungZiel.RolleEntfeuchter => einstellung with { EntfeuchterFolgt = false },
                    TrocknungZiel.RolleKuehlung => einstellung with { KuehlungFolgt = false },
                    _ => einstellung with { AbluftFolgt = false },
                };
                geschrieben++;
                _logger.LogInformation("Trocknung Zelt {Zelt}: {Rolle} {Entity} zurück auf {Vorher}.", tentId, rolle, entity, m.Vorher);
            }
            else
            {
                gescheitert++;
                _logger.LogWarning("Trocknung Zelt {Zelt}: {Rolle} ließ sich nicht zurücksetzen.", tentId, rolle);
            }
        }

        // „Zurück“ heißt: der Fork fasst nichts mehr an, auch wenn eine Rolle nicht zurückging (dann bleibt ihr Merker stehen).
        // Den Entfeuchter hält kein Merker (er wird geschaltet, nicht über eine Grenze geführt): er hört einfach auf zu schalten und bleibt, wie er steht.
        _steuerung.Set(tentId, einstellung with { Betrieb = TrocknungSteuerungRepository.BetriebMelden, EntfeuchterFolgt = false });
        return new Abgleich(geschrieben, gescheitert);
    }
}
