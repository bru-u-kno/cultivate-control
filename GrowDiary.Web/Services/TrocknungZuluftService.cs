using System.Collections.Concurrent;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Merkt, dass der Fork die Zuluft gerade selbst führt (Trocknungsbetrieb). Die Zuluft-Seite schaltet beim Speichern die HA-Automatik
/// dann nicht wieder ein — sonst arbeiteten beide eine Minute lang gegeneinander.
/// </summary>
public sealed class ZuluftFuehrung
{
    public volatile bool Gefuehrt;
}

/// <summary>
/// Die Zuluft folgt dem Trocknungsplan (cc.337): Im Trocknungsbetrieb entscheidet der Fork jede Minute selbst, ob sie ansaugt und auf
/// welcher Stufe (<see cref="TrocknungZuluft"/>). Die HA-Automatik der Zuluft hält er dafür an und stellt sie wieder her, sobald der
/// Trocknungsbetrieb endet. Am Controller ändert er nur Ein/Aus und die Stufe — nie etwas anderes.
/// </summary>
public sealed class TrocknungZuluftService
{
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPlanQuelle _plan;
    private readonly TrocknungSteuerungRepository _steuerung;
    private readonly HomeAssistantService _ha;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly IServiceScopeFactory _bereiche;
    private readonly ZuluftFuehrung _fuehrung;
    private readonly ILogger<TrocknungZuluftService> _logger;

    private readonly ConcurrentDictionary<int, bool> _saugte = new();
    private readonly ConcurrentDictionary<int, DateTime> _zuletztGeschaltetUtc = new();
    private readonly ConcurrentDictionary<int, int> _problemLaeufe = new();
    private readonly ConcurrentDictionary<int, bool> _problemGemeldet = new();
    private readonly ConcurrentDictionary<int, bool> _gefuehrt = new();
    // Wie die Automatik stand, bevor der Fork sie anhielt — gilt, wenn die Seite nie etwas für sie gespeichert hat.
    private readonly ConcurrentDictionary<int, bool> _automatikVorher = new();

    /// <summary>Wie viele Läufe (je eine Minute) eine Störung dauern muss, bis der Fork sie per Push meldet.</summary>
    public const int ProblemBisPush = 10;

    public TrocknungZuluftService(
        GrowRepository grows, HarvestRepository ernten, TrocknungPlanQuelle plan, TrocknungSteuerungRepository steuerung,
        HomeAssistantService ha, HomeAssistantSettingsRepository haSettings, IServiceScopeFactory bereiche, ZuluftFuehrung fuehrung,
        ILogger<TrocknungZuluftService> logger)
    {
        _grows = grows;
        _ernten = ernten;
        _plan = plan;
        _steuerung = steuerung;
        _ha = ha;
        _haSettings = haSettings;
        _bereiche = bereiche;
        _fuehrung = fuehrung;
        _logger = logger;
    }

    /// <summary>Der Stand für die Seite: die Einstellung, die Messwerte und was der Fork gerade entscheiden würde. Nur lesend.</summary>
    public sealed record Stand(
        bool Trockenzelt, int? TentId, string? ZeltName, bool Trocknet, bool Folgt, bool Regelt, int Hoechststufe, int AbstandProzent,
        double? ZeltTempC, double? ZeltRhProzent, double? ZeltAbsolutGm3, double? AussenAbsolutGm3, double? PlanRhProzent, double? PlanTempC,
        int? Tag, int? TageGeplant, string Zustand, int Stufe, double? Differenz, double? LaeuftStufe, bool Gescheitert);

    /// <summary>Die Zelte, deren Zuluft der Fork gerade führt.</summary>
    public IReadOnlyList<int> GefuehrteZelte() => _gefuehrt.Keys.ToList();

    /// <summary>Das Trockenzelt, dem die Zuluft folgt: das erste Zelt mit dem Zweck „Trocknung“; <c>null</c>, wenn es keines gibt.</summary>
    public Tent? Trockenzelt() => _grows.GetTents().FirstOrDefault(t => t.TentType == TentType.Drying);

    private sealed record Messwerte(double? ZeltTemp, double? ZeltRh, double? AussenAbs, double? AussenTemp, string? SchalterId, string? StufeId, string? ZustandId, string? IstStufeId);

    /// <param name="GespeicherteAutomatik">Was die Zuluft-Seite zuletzt für die Automatik gespeichert hat; <c>null</c>, wenn nie gespeichert wurde.</param>
    private sealed record Umfeld(ZuluftEinstellungen Einstellung, IReadOnlyDictionary<string, string?> Rollen, bool? GespeicherteAutomatik);

    private async Task<Umfeld> UmfeldAsync(CancellationToken ct)
    {
        using var bereich = _bereiche.CreateScope();
        var zuluft = bereich.ServiceProvider.GetRequiredService<ZuluftSteuerungService>();
        var geraete = bereich.ServiceProvider.GetRequiredService<SteuerungGeraeteService>();
        var gespeichert = zuluft.Gespeichert?.AutomatikAktiv;
        return new Umfeld(await zuluft.EinstellungenAsync(ct), geraete.EntitiesFuerModul(ZuluftSteuerungService.Modul), gespeichert);
    }

    private async Task<double?> ZahlAsync(HomeAssistantSettings s, string? entityId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(entityId) ? null : Zahlenlesen.Maschine((await _ha.GetEntityStateAsync(s, entityId, ct))?.State);

    private async Task<Messwerte> MessenAsync(int tentId, Umfeld u, CancellationToken ct)
    {
        var s = _haSettings.GetEffectiveHomeAssistantSettings();
        string? Rolle(string rolle) => u.Rollen.TryGetValue(rolle, out var id) ? id : null;
        var zelt = _grows.GetTent(tentId);
        string? Sensor(SensorMetricType art) => zelt?.Sensors.FirstOrDefault(x => x.MetricType == art && x.IsActive && x.HaEntityId.Length > 0)?.HaEntityId;

        var aussenTemp = await ZahlAsync(s, Rolle(ZuluftSteuerungService.Rollen.AussenTemp), ct);
        var aussenRh = await ZahlAsync(s, Rolle(ZuluftSteuerungService.Rollen.AussenRh), ct);
        return new Messwerte(
            await ZahlAsync(s, Sensor(SensorMetricType.AirTemperature), ct),
            await ZahlAsync(s, Sensor(SensorMetricType.Humidity), ct),
            TrocknungZuluft.Absolut(aussenTemp, aussenRh), aussenTemp,
            Rolle(ZuluftSteuerungService.Rollen.PortSchalter), Rolle(ZuluftSteuerungService.Rollen.PortStufe),
            Rolle(ZuluftSteuerungService.Rollen.PortZustand), Rolle(ZuluftSteuerungService.Rollen.PortIstStufe));
    }

    private static TrocknungZuluft.Regel RegelAus(ZuluftEinstellungen e, TrocknungSteuerungRepository.Einstellung t) => new(
        e.MindestDifferenzGm3, e.AbschaltAbstandGm3, t.ZuluftAbstandProzent,
        e.ZeltTemperaturMinC ?? ZuluftSteuerungService.ZeltTemperaturMinVorgabe, e.AussentemperaturMinC,
        e.StufeMin, t.ZuluftHoechststufe);

    /// <summary>Planwert der Luftfeuchte und der Temperatur für heute; <c>null</c> ohne laufende Trocknung.</summary>
    private (DryingWindow.Stand? Trocknung, double? Rh, double? Temp, int? Geplant) PlanFuer(int tentId)
    {
        if (DryingWindow.StandFor(_grows, _ernten, tentId, DateTime.Today) is not { } trocknung) return (null, null, null, null);
        var etappe = _plan.Fuer(tentId, trocknung);
        return (trocknung, etappe.Etappe?.Etappe.LuftfeuchteZiel, etappe.Etappe?.Etappe.LufttemperaturC, etappe.Geplant);
    }

    public async Task<Stand> StandAsync(int tentId, CancellationToken ct)
    {
        var zelt = _grows.GetTent(tentId);
        var t = _steuerung.Get(tentId);
        var (trocknung, planRh, planTemp, geplant) = PlanFuer(tentId);
        var regelt = t.Betrieb == TrocknungSteuerungRepository.BetriebRegeln && t.ZuluftFolgt && trocknung is not null;
        var u = await UmfeldAsync(ct);
        var m = await MessenAsync(tentId, u, ct);
        var e = TrocknungZuluft.Entscheide(
            new TrocknungZuluft.Lage(m.ZeltTemp, m.ZeltRh, m.AussenAbs, m.AussenTemp, planRh), RegelAus(u.Einstellung, t),
            _saugte.GetValueOrDefault(tentId));
        var s = _haSettings.GetEffectiveHomeAssistantSettings();
        return new Stand(
            zelt is not null, tentId, zelt?.Name, trocknung is not null, t.ZuluftFolgt, regelt, t.ZuluftHoechststufe, t.ZuluftAbstandProzent,
            m.ZeltTemp, m.ZeltRh, e.ZeltAbsolutGm3, m.AussenAbs, planRh, planTemp, trocknung?.Tag, geplant,
            e.Zustand, e.Stufe, e.Differenz, await ZahlAsync(s, m.IstStufeId, ct), _problemLaeufe.GetValueOrDefault(tentId) > 0);
    }

    /// <summary>
    /// Ein Lauf (jede Minute). Im Trocknungsbetrieb entscheidet er und schaltet; sonst stellt er die HA-Automatik der Zuluft her, wenn
    /// der Fork sie vorher angehalten hatte. Gibt zurück, ob am Port geschaltet wurde.
    /// </summary>
    public async Task<bool> LaufAsync(int tentId, CancellationToken ct)
    {
        var t = _steuerung.Get(tentId);
        var (trocknung, planRh, _, _) = PlanFuer(tentId);
        var aktiv = t.Betrieb == TrocknungSteuerungRepository.BetriebRegeln && t.ZuluftFolgt && trocknung is not null;
        if (!aktiv)
        {
            await UebergebenAsync(tentId, ct);
            return false;
        }

        var u = await UmfeldAsync(ct);
        var m = await MessenAsync(tentId, u, ct);
        if (m.SchalterId is null)
        {
            await ProblemAsync(tentId, "Der Zuluft ist noch kein Schalter zugeordnet (Geräte › Zuordnung): Der Fork kann sie nicht schalten.", ct);
            return false;
        }

        var s = _haSettings.GetEffectiveHomeAssistantSettings();
        var laeuft = Zustand(await _ha.GetEntityStateAsync(s, m.ZustandId ?? "", ct));
        // Die Automatik der Zuluft hält an, solange der Fork führt. Jede Minute geprüft: ein Speichern oder ein Neustart von Home Assistant
        // schaltet sie sonst wieder ein.
        _fuehrung.Gefuehrt = true;
        if (!_gefuehrt.ContainsKey(tentId) && Zustand(await _ha.GetEntityStateAsync(s, ZuluftSteuerungService.Entitaeten.Automatik, ct)) is { } vorher)
            _automatikVorher[tentId] = vorher;
        _gefuehrt[tentId] = true;
        await _ha.CallEntityServiceAsync(s, "automation", "turn_off", ZuluftSteuerungService.Entitaeten.Automatik, ct);

        var e = TrocknungZuluft.Entscheide(
            new TrocknungZuluft.Lage(m.ZeltTemp, m.ZeltRh, m.AussenAbs, m.AussenTemp, planRh), RegelAus(u.Einstellung, t),
            laeuft ?? _saugte.GetValueOrDefault(tentId));
        if (e.Zustand == TrocknungZuluft.ZustandKeinMesswert)
            await ProblemAsync(tentId, "Die Messwerte für die Zuluft im Trockenzelt (Zelt, Außenluft oder Plan) lassen sich nicht lesen: Die Zuluft bleibt aus.", ct);

        var soll = e.Saugt;
        _saugte[tentId] = soll;
        var istAn = laeuft ?? false;

        // Stufe nachführen, solange sie läuft und weiterlaufen soll.
        if (soll && istAn)
        {
            if (m.StufeId is not null && await ZahlAsync(s, m.IstStufeId ?? m.StufeId, ct) is { } ist && (int)Math.Round(ist) != e.Stufe)
                await _ha.CallEntityServiceAsync(s, "number", "set_value", m.StufeId, ct, new Dictionary<string, object> { ["value"] = e.Stufe });
            Erholt(tentId);
            return false;
        }

        if (soll == istAn) { Erholt(tentId); return false; }

        // Schalten nur nach Mindestlaufzeit / Mindestpause (schont den Lüfter und das Relais am Controller).
        var seit = _zuletztGeschaltetUtc.TryGetValue(tentId, out var z) ? DateTime.UtcNow - z : (TimeSpan?)null;
        var sperre = TimeSpan.FromMinutes(istAn ? u.Einstellung.MindestlaufzeitMin : u.Einstellung.MindestpauseMin);
        if (seit is { } vergangen && vergangen < sperre) return false;

        var ok = true;
        if (soll)
        {
            if (m.StufeId is not null)
            {
                ok &= await _ha.CallEntityServiceAsync(s, "number", "set_value", m.StufeId, ct, new Dictionary<string, object> { ["value"] = e.Stufe });
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            ok &= await SchaltenAsync(s, m.SchalterId, ein: true, ct);
        }
        else ok &= await SchaltenAsync(s, m.SchalterId, ein: false, ct);

        if (!ok)
        {
            _logger.LogWarning("Trocknung Zelt {Zelt}: Zuluft ließ sich nicht {Aktion}.", tentId, soll ? "einschalten" : "ausschalten");
            await ProblemAsync(tentId, $"Die Zuluft ließ sich nicht {(soll ? "einschalten" : "ausschalten")}. Die Cloud des Controllers hat den Befehl nicht angenommen.", ct);
            return false;
        }

        _zuletztGeschaltetUtc[tentId] = DateTime.UtcNow;
        Erholt(tentId);
        await _ha.CallEntityServiceAsync(s, "input_datetime", "set_datetime", ZuluftSteuerungService.Entitaeten.LetzterSchaltvorgang, ct,
            new Dictionary<string, object> { ["datetime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) });
        _logger.LogInformation("Trocknung Zelt {Zelt}: Zuluft {Aktion} (Stufe {Stufe}, Zelt {Rh} %, Plan {Plan} %, Differenz {Diff} g/m³).",
            tentId, soll ? "ein" : "aus", e.Stufe, m.ZeltRh, planRh, e.Differenz);
        return true;
    }

    private static bool? Zustand(HomeAssistantState? s) => s?.State is { } t && t is not ("unknown" or "unavailable") ? t is "on" or "On" : null;

    private Task<bool> SchaltenAsync(HomeAssistantSettings s, string entityId, bool ein, CancellationToken ct)
    {
        var domain = entityId.Split('.')[0];
        return domain switch
        {
            "select" => _ha.CallEntityServiceAsync(s, "select", "select_option", entityId, ct, new Dictionary<string, object> { ["option"] = ein ? "On" : "Off" }),
            "switch" or "input_boolean" => _ha.CallEntityServiceAsync(s, domain, ein ? "turn_on" : "turn_off", entityId, ct),
            _ => Task.FromResult(false),
        };
    }

    /// <summary>Gibt die Zuluft an die HA-Automatik zurück: Sie läuft wieder, wie die Zuluft-Seite es eingestellt hat.</summary>
    private async Task UebergebenAsync(int tentId, CancellationToken ct)
    {
        _saugte.TryRemove(tentId, out _);
        Erholt(tentId);
        if (!_gefuehrt.TryRemove(tentId, out _)) { _fuehrung.Gefuehrt = _gefuehrt.Count > 0; return; }

        _fuehrung.Gefuehrt = _gefuehrt.Count > 0;
        var u = await UmfeldAsync(ct);
        var s = _haSettings.GetEffectiveHomeAssistantSettings();
        // Gilt der Wunsch der Seite; hat die Seite nie gespeichert, der Stand von vorher; sonst an (die Regelung ist der Normalfall).
        var vorher = _automatikVorher.TryRemove(tentId, out var v) ? v : (bool?)null;
        var an = u.GespeicherteAutomatik ?? vorher ?? true;
        await _ha.CallEntityServiceAsync(s, "automation", an ? "turn_on" : "turn_off", ZuluftSteuerungService.Entitaeten.Automatik, ct);
        _logger.LogInformation("Trocknung Zelt {Zelt}: Zuluft zurück an die Regelung (Automatik {Automatik}).", tentId, an ? "an" : "aus");
    }

    private void Erholt(int tentId)
    {
        _problemLaeufe.TryRemove(tentId, out _);
        _problemGemeldet.TryRemove(tentId, out _);
    }

    private async Task ProblemAsync(int tentId, string text, CancellationToken ct)
    {
        var laeufe = _problemLaeufe.AddOrUpdate(tentId, 1, (_, n) => n + 1);
        if (laeufe < ProblemBisPush || _problemGemeldet.GetValueOrDefault(tentId)) return;
        _problemGemeldet[tentId] = true;
        try
        {
            using var bereich = _bereiche.CreateScope();
            await bereich.ServiceProvider.GetRequiredService<NotificationService>()
                .SendAsync(NotificationCategory.System, "Trockenzelt: Zuluft wird nicht geschaltet", text, ct, trotzRuhezeit: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Trocknung Zelt {Zelt}: Push zur Störung der Zuluft nicht gesendet.", tentId);
        }
    }
}
