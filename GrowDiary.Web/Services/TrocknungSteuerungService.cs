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

    public TrocknungSteuerungService(
        GrowRepository grows, HarvestRepository ernten, TrocknungPlanQuelle plan, TrocknungPortRepository ports,
        TrocknungSteuerungRepository steuerung, TrocknungZielService ziel, ILogger<TrocknungSteuerungService> logger)
    {
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
        string Urteil, double? Vorher, DateTime? ZuletztGesetztUtc);

    public sealed record Stand(
        bool Trocknet, string Betrieb, bool PortsErreichbar, int? Tag, int? TageGeplant, string? Etappe,
        double? TempMin, double? TempMax, double? FeuchteMin, double? FeuchteMax, IReadOnlyList<Geraet> Geraete);

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

            var wunsch = await WunschAsync(rolle, port, planGrenze, ct);
            geraete.Add(new Geraet(rolle, port.Name, folgt, wunsch.Modus, wunsch.Jetzt, wunsch.Einheit, wunsch.Plan ?? planGrenze, wunsch.Urteil, m?.Vorher, m?.GesetztAtUtc));
        }

        return new Stand(
            trocknung is not null, einstellung.Betrieb, erreichbar, trocknung?.Tag, geplant, etappeName,
            band?.TempMin, band?.TempMax, band?.FeuchteMin, band?.FeuchteMax, geraete);
    }

    private async Task<TrocknungZiel.Wunsch> WunschAsync(string rolle, TrocknungZiel.Port port, double? planGrenze, CancellationToken ct)
    {
        var entitaeten = new[] { port.ModusEntity, port.FeuchteWertEntity, port.TempWertEntity }
            .Where(e => e is not null).Select(e => e!).Distinct().ToList();
        var zustaende = await Task.WhenAll(entitaeten.Select(async e => (e, Zustand: await _ziel.ZustandAsync(e, ct))));
        var nachId = zustaende.ToDictionary(z => z.e, z => z.Zustand, StringComparer.OrdinalIgnoreCase);
        return TrocknungZiel.Entscheide(rolle, port, e => nachId.GetValueOrDefault(e), planGrenze);
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
        _steuerung.Set(tentId, einstellung with { Betrieb = TrocknungSteuerungRepository.BetriebMelden });
        return new Abgleich(geschrieben, gescheitert);
    }
}
