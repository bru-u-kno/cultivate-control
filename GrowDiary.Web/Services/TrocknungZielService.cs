using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GrowDiary.Web.Services;

/// <summary>
/// Holt für das Trocknungs-Ziel die Ports des Controllers aus dem Register von Home Assistant und die Zustände ihrer
/// Entitäten — nur lesend (A-029, Etappe 3, Teil A).
/// </summary>
/// <remarks>
/// Das Register kommt über einen WebSocket und dauert; die Live-Seite fragt bei jedem Öffnen. Deshalb merkt sich der Dienst die
/// Portliste kurz (<see cref="Gedaechtnis"/>). Eine neue Zuordnung vergisst sie sofort.
/// </remarks>
public sealed class TrocknungZielService
{
    public static readonly TimeSpan Gedaechtnis = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<IReadOnlyList<TrocknungZiel.Port>?>> _ports;
    private readonly Func<string, CancellationToken, Task<HomeAssistantState?>> _zustand;
    private readonly Func<string, double, CancellationToken, Task<bool>> _setze;
    private readonly Func<string, string, CancellationToken, Task<bool>> _setzeModus;
    private readonly object _sperre = new();
    private (DateTime Zeit, IReadOnlyList<TrocknungZiel.Port>? Ports)? _gemerkt;

    [ActivatorUtilitiesConstructor]
    public TrocknungZielService(HomeAssistantRegistryService register, HomeAssistantService ha, HomeAssistantSettingsRepository einstellungen)
        : this(
            async ct =>
            {
                var herkunft = await register.HerkunftAsync(einstellungen.GetEffectiveHomeAssistantSettings(), ct);
                // Ein leeres Register heißt: Home Assistant ist nicht erreichbar oder nicht eingerichtet.
                return herkunft.Count == 0 ? null : TrocknungZiel.Erkennen(herkunft.Values);
            },
            (entityId, ct) => ha.GetEntityStateAsync(einstellungen.GetEffectiveHomeAssistantSettings(), entityId, ct),
            (entityId, wert, ct) => ha.CallEntityServiceAsync(
                einstellungen.GetEffectiveHomeAssistantSettings(), "number", "set_value", entityId, ct,
                new Dictionary<string, object> { ["value"] = wert }),
            (entityId, option, ct) => ha.CallEntityServiceAsync(
                einstellungen.GetEffectiveHomeAssistantSettings(), "select", "select_option", entityId, ct,
                new Dictionary<string, object> { ["option"] = option }))
    {
    }

    /// <summary>Für Tests: Ports, Zustände und das Setzen von außen. Ohne <paramref name="setze"/> gelingt das Setzen nie (Teil A schreibt nichts).</summary>
    public TrocknungZielService(
        Func<CancellationToken, Task<IReadOnlyList<TrocknungZiel.Port>?>> ports,
        Func<string, CancellationToken, Task<HomeAssistantState?>> zustand,
        Func<string, double, CancellationToken, Task<bool>>? setze = null,
        Func<string, string, CancellationToken, Task<bool>>? setzeModus = null)
    {
        _ports = ports;
        _zustand = zustand;
        _setze = setze ?? ((_, _, _) => Task.FromResult(false));
        _setzeModus = setzeModus ?? ((_, _, _) => Task.FromResult(false));
    }

    public Task<HomeAssistantState?> ZustandAsync(string entityId, CancellationToken ct) => _zustand(entityId, ct);

    /// <summary>
    /// Setzt die Zahl einer <c>number</c>-Entität (Teil B). Nur diese Art Entität: Der Fork schaltet über diesen Weg weder
    /// Modus noch Schalter des Controllers.
    /// </summary>
    public Task<bool> SetzeZahlAsync(string entityId, double wert, CancellationToken ct)
        => entityId.StartsWith("number.", StringComparison.OrdinalIgnoreCase) && double.IsFinite(wert)
            ? _setze(entityId, wert, ct)
            : Task.FromResult(false);

    /// <summary>
    /// Schaltet den Modus eines Ports zwischen <c>On</c> und <c>Off</c> (Entfeuchter am Trockenzelt). Nur diese beiden Werte an einer
    /// <c>select</c>-Entität: Auto und alle anderen Modi stellt der Fork nie ein.
    /// </summary>
    public Task<bool> SetzeModusAsync(string entityId, string option, CancellationToken ct)
        => entityId.StartsWith("select.", StringComparison.OrdinalIgnoreCase) && option is "On" or "Off"
            ? _setzeModus(entityId, option, ct)
            : Task.FromResult(false);

    /// <summary>Alle Ports mit eigener Automatik; <c>null</c>, wenn Home Assistant sich nicht erreichen lässt.</summary>
    public async Task<IReadOnlyList<TrocknungZiel.Port>?> PortsAsync(bool frisch, CancellationToken ct)
    {
        lock (_sperre)
        {
            if (!frisch && _gemerkt is { } m && DateTime.UtcNow - m.Zeit < Gedaechtnis) return m.Ports;
        }

        var ports = await _ports(ct);
        lock (_sperre) _gemerkt = (DateTime.UtcNow, ports);
        return ports;
    }

    public void Vergessen()
    {
        lock (_sperre) _gemerkt = null;
    }

    /// <summary>
    /// Plan gegen Gerät für beide Rollen. <paramref name="zuordnung"/> ist Rolle → Geräte-Id.
    /// </summary>
    public async Task<(bool Erreichbar, IReadOnlyList<TrocknungZiel.Vergleich> Vergleiche)> VergleicheAsync(
        IReadOnlyDictionary<string, string> zuordnung,
        double? planFeuchteOben,
        double? planTempOben,
        CancellationToken ct)
    {
        // Ohne Zuordnung gibt es nichts zu vergleichen — dann auch keine Rundfrage an Home Assistant.
        if (zuordnung.Count == 0)
            return (true, [TrocknungZiel.OhnePort(TrocknungZiel.RolleEntfeuchter, null, planFeuchteOben), TrocknungZiel.OhnePort(TrocknungZiel.RolleKuehlung, null, planTempOben)]);

        var ports = await PortsAsync(frisch: false, ct);
        var erreichbar = ports is not null;
        var rollen = new[]
        {
            (Rolle: TrocknungZiel.RolleEntfeuchter, Plan: planFeuchteOben),
            (Rolle: TrocknungZiel.RolleKuehlung, Plan: planTempOben),
        };

        var ergebnis = new List<TrocknungZiel.Vergleich>();
        foreach (var (rolle, plan) in rollen)
        {
            if (!zuordnung.TryGetValue(rolle, out var deviceId))
            {
                ergebnis.Add(TrocknungZiel.OhnePort(rolle, null, plan));
                continue;
            }

            var port = ports?.FirstOrDefault(p => p.DeviceId == deviceId);
            if (port is null)
            {
                // Zugeordnet, aber gerade nicht auffindbar (Home Assistant nicht erreichbar, Port entfernt): unbekannt, nicht „nicht zugeordnet“.
                ergebnis.Add(new TrocknungZiel.Vergleich(rolle, null, null, null, null, plan, TrocknungZiel.Urteil.Unbekannt));
                continue;
            }

            // Alle benötigten Zustände einmal und gleichzeitig holen.
            var entitaeten = new[] { port.ModusEntity, port.FeuchteWertEntity, port.FeuchteAktivEntity, port.TempWertEntity, port.TempAktivEntity }
                .Where(e => e is not null).Select(e => e!).Distinct().ToList();
            var zustaende = await Task.WhenAll(entitaeten.Select(async e => (e, Zustand: await _zustand(e, ct))));
            var nachId = zustaende.ToDictionary(z => z.e, z => z.Zustand, StringComparer.OrdinalIgnoreCase);
            ergebnis.Add(TrocknungZiel.Vergleiche(rolle, port, e => nachId.GetValueOrDefault(e), plan));
        }

        return (erreichbar, ergebnis);
    }
}
