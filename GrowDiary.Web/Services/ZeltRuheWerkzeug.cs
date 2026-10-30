using GrowDiary.Web.Models;
using GrowDiary.Web.Infrastructure;

namespace GrowDiary.Web.Services;

/// <summary>
/// Fork AI (cc.277): <see cref="IZeltRuheWerkzeug"/> über Home Assistant und die vorhandenen Steuerungsdienste.
/// </summary>
/// <remarks>
/// Die Automatik jedes Geräts wird über den Speichern-Weg seiner Steuerung geschaltet (nicht an Home Assistant vorbei), damit der
/// gespeicherte Stand und die HA-Automation übereinstimmen und der Stundenabgleich sie nicht zurückdreht. Scheitert das
/// Schreiben, wird der alte Wert zurückgespeichert — es bleibt kein halber Zustand im Fork stehen.
/// </remarks>
public sealed class ZeltRuheWerkzeug : IZeltRuheWerkzeug
{
    private readonly HomeAssistantService _ha;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly AcSchreiber _schreiber;
    private readonly Co2SteuerungService _co2;
    private readonly ZuluftSteuerungService _zuluft;
    private readonly ChillerSteuerungService _chiller;
    private readonly EntfeuchterSteuerungService _entfeuchter;
    private readonly EntfeuchterZusatzSteuerungService _zusatz;
    private readonly IChillerRegler _kuehlerRegler;
    private readonly LichtWaechterService _waechter;
    private readonly Func<TimeSpan, CancellationToken, Task>? _warten;
    private bool _acGeschrieben;

    public ZeltRuheWerkzeug(
        HomeAssistantService ha, HomeAssistantSettingsRepository haSettings, AcSchreiber schreiber,
        Co2SteuerungService co2, ZuluftSteuerungService zuluft, ChillerSteuerungService chiller,
        EntfeuchterSteuerungService entfeuchter, EntfeuchterZusatzSteuerungService zusatz,
        IChillerRegler kuehlerRegler, LichtWaechterService waechter,
        Func<TimeSpan, CancellationToken, Task>? warten = null)
    {
        _ha = ha;
        _haSettings = haSettings;
        _schreiber = schreiber;
        _co2 = co2;
        _zuluft = zuluft;
        _chiller = chiller;
        _entfeuchter = entfeuchter;
        _zusatz = zusatz;
        _kuehlerRegler = kuehlerRegler;
        _waechter = waechter;
        _warten = warten;
    }

    public async Task<string?> ZustandAsync(string entity, CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        return settings.IsConfigured ? (await _ha.GetEntityStateAsync(settings, entity, ct))?.State : null;
    }

    /// <summary>
    /// Auswahlfelder (AC Infinity: Betriebsart des Ports) gehen durch den <see cref="AcSchreiber"/> — nacheinander, mit Pause und
    /// Nachlesen, weil die Wolke parallele Updates still verwirft. Steckdosen und Schalter (Shelly, Helfer) sind lokal.
    /// </summary>
    public async Task<Schaltausgang> SchaltenAsync(
        string entity, (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten) aktion, string? soll, CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        if (!settings.IsConfigured) return Schaltausgang.Abgelehnt;

        if (string.Equals(aktion.Domain, "select", StringComparison.OrdinalIgnoreCase) && soll is not null)
        {
            if (_acGeschrieben) await (_warten ?? Task.Delay)(AcSchreiber.Pause, ct);
            _acGeschrieben = true;
            var schritt = new AcSchreibschritt(entity, aktion.Domain, aktion.Dienst, aktion.Daten ?? new Dictionary<string, object>(), soll);
            var ergebnis = (await _schreiber.SchreibenAsync(settings, [schritt], _warten, ct)).FirstOrDefault();
            if (ergebnis is null) return Schaltausgang.Abgelehnt;
            return ergebnis.Bestaetigt ? Schaltausgang.Bestaetigt : ergebnis.Angenommen ? Schaltausgang.Unbestaetigt : Schaltausgang.Abgelehnt;
        }

        // Nicht zu bool falten: „Antwort blieb aus" heißt nicht „nicht geschaltet" (HomeAssistantService.RufeEntitaetsDienstAsync).
        return await _ha.RufeEntitaetsDienstAsync(settings, aktion.Domain, aktion.Dienst, entity, ct, aktion.Daten) switch
        {
            HaDienstAntwort.Angenommen => Schaltausgang.Bestaetigt,
            HaDienstAntwort.Unbestaetigt => Schaltausgang.Unbestaetigt,
            _ => Schaltausgang.Abgelehnt,
        };
    }

    // ------------------------------------------------------------------ Automatik je Gerät

    public Task<(bool Ok, bool? WarAn)> AutomatikAusAsync(string kennung, CancellationToken ct)
        => kennung switch
        {
            ZeltRuheArten.Co2 => Aus(() => Task.FromResult(_co2.Einstellungen.AutomatikAktiv), async an =>
            {
                var e = _co2.Einstellungen;
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _co2.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Zuluft => Aus(async () => (await _zuluft.EinstellungenAsync(ct)).AutomatikAktiv, async an =>
            {
                var e = await _zuluft.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _zuluft.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Chiller => Aus(async () => (await _chiller.EinstellungenAsync(ct)).AutomatikAktiv, async an =>
            {
                var e = await _chiller.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _chiller.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Entfeuchter => Aus(async () => (await _entfeuchter.EinstellungenAsync(ct)).AutomatikAktiv, async an =>
            {
                var e = await _entfeuchter.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _entfeuchter.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Zusatz => Aus(async () => (await _zusatz.EinstellungenAsync(ct)).AutomatikAktiv, async an =>
            {
                var ergebnis = await _zusatz.SpeichernAsync(new EntfeuchterZusatzAenderung { AutomatikAktiv = an }, ct);
                return ergebnis.Gespeichert is not null && ergebnis.HaErreicht == true;
            }),
            // Licht und Abluft haben keine eigene Automatik: das Gerät führt Zeitplan oder Stufe selbst.
            _ => Task.FromResult<(bool, bool?)>((true, null)),
        };

    public async Task<bool> AutomatikAnAsync(string kennung, CancellationToken ct)
    {
        var (ok, _) = await (kennung switch
        {
            ZeltRuheArten.Co2 => An(async an =>
            {
                var e = _co2.Einstellungen;
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _co2.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Zuluft => An(async an =>
            {
                var e = await _zuluft.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _zuluft.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Chiller => An(async an =>
            {
                var e = await _chiller.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _chiller.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Entfeuchter => An(async an =>
            {
                var e = await _entfeuchter.EinstellungenAsync(ct);
                e.AutomatikAktiv = an;
                var (g, _, erreicht) = await _entfeuchter.SpeichernAsync(e, ct);
                return g is not null && erreicht;
            }),
            ZeltRuheArten.Zusatz => An(async an =>
            {
                var ergebnis = await _zusatz.SpeichernAsync(new EntfeuchterZusatzAenderung { AutomatikAktiv = an }, ct);
                return ergebnis.Gespeichert is not null && ergebnis.HaErreicht == true;
            }),
            _ => Task.FromResult<(bool, bool?)>((true, null)),
        });
        return ok;
    }

    /// <summary>Automatik aus: nur, wenn sie an war. Scheitert das Schreiben, wird der alte Wert zurückgespeichert.</summary>
    private static async Task<(bool Ok, bool? WarAn)> Aus(Func<Task<bool>> lesen, Func<bool, Task<bool>> setzen)
    {
        var vorher = await lesen();
        if (!vorher) return (true, false);
        if (await setzen(false)) return (true, true);
        try { await setzen(true); } catch (Exception) { /* der Rückweg darf den Befund nicht überdecken */ }
        return (false, null);
    }

    private static async Task<(bool Ok, bool? WarAn)> An(Func<bool, Task<bool>> setzen) => (await setzen(true), true);

    // ------------------------------------------------------------------ Fork-Regler und Wächter

    public IReadOnlyList<int> KuehlerReglerAus() => _kuehlerRegler.Ausschalten();

    public void KuehlerReglerAn(IEnumerable<int> zeltIds) => _kuehlerRegler.Einschalten(zeltIds);

    public void WaechterPausierenBis(DateTime bisUtc) => _waechter.PausierenBis(bisUtc);

    public void WaechterFortsetzen() => _waechter.Pausieren(0);
}
