using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Antwort der Controller-Seite.</summary>
public sealed record ControllerWaechterAntwort(IReadOnlyList<ControllerStand> Controller);

/// <summary>Ein Controller für die Oberfläche: Einstellungen plus, was er gerade meldet.</summary>
public sealed record ControllerStand(
    ControllerWaechterEintrag Controller,
    string Befund,
    DateTime? SeitUtc,
    string? StatusZustand,
    string? FeuchteZustand,
    string? SteckdoseZustand,
    bool SteckdoseSchaltbar,
    int NeustartsHeute,
    bool Pausiert,
    string? LetzteMeldung,
    DateTime? GeprueftUtc);

/// <summary>
/// Der Controller-Wächter: prüft im Minutentakt Verbindung und Feuchtefühler jedes eingetragenen Controllers,
/// meldet einen Ausfall und startet den Controller bei Bedarf über seine Steckdose neu.
/// Die Entscheidungen stehen in <see cref="ControllerWaechterRegeln"/>.
/// </summary>
/// <remarks>
/// Läuft im Minutentakt des Alarm-Wächters (<see cref="AlertWatchWorker"/>). Der Merker eines Ausfalls steht in der
/// Datenbank (<see cref="AppSettingsRepository"/>) — nach einem Neustart der App wird weder doppelt gemeldet noch die
/// Tagesgrenze der Neustarts vergessen. Der Neustart wird VOR dem Schalten gemerkt: Stürzt die App mittendrin ab,
/// gibt es höchstens einen Versuch zu wenig, nie eine Schleife.
/// </remarks>
public sealed class ControllerWaechterService
{
    public const string Modul = "controller_waechter";
    public const string ProtokollTyp = "controller-waechter";
    private const string MerkerSchluessel = "controller-waechter:merker:";

    private static readonly ConcurrentDictionary<string, (string Text, DateTime Utc)> Letzte = new();
    private static readonly ConcurrentDictionary<string, DateTime> Geprueft = new();

    private readonly SteuerungRepository _repo;
    private readonly IAcFunk _funk;
    private readonly Ausschalter _ausschalter;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly AppSettingsRepository _ablage;
    private readonly NotificationService _notifications;
    private readonly GrowRepository _grows;
    private readonly JournalRepository _journal;
    private readonly SystemAuditRepository _audit;
    private readonly ILogger<ControllerWaechterService> _logger;
    private readonly Func<DateTime> _jetztUtc;
    private readonly Func<TimeSpan, CancellationToken, Task> _warten;

    public ControllerWaechterService(
        SteuerungRepository repo,
        IAcFunk funk,
        Ausschalter ausschalter,
        HomeAssistantSettingsRepository haSettings,
        AppSettingsRepository ablage,
        NotificationService notifications,
        GrowRepository grows,
        JournalRepository journal,
        SystemAuditRepository audit,
        ILogger<ControllerWaechterService> logger,
        Func<DateTime>? jetztUtc = null,
        Func<TimeSpan, CancellationToken, Task>? warten = null)
    {
        _repo = repo;
        _funk = funk;
        _ausschalter = ausschalter;
        _haSettings = haSettings;
        _ablage = ablage;
        _notifications = notifications;
        _grows = grows;
        _journal = journal;
        _audit = audit;
        _logger = logger;
        _jetztUtc = jetztUtc ?? (() => DateTime.UtcNow);
        _warten = warten ?? ((dauer, token) => Task.Delay(dauer, token));
    }

    // ----------------------------------------------------------- Einstellungen

    public ControllerWaechterEinstellungen Einstellungen
        => _repo.GetEinstellungen<ControllerWaechterEinstellungen>(Modul) ?? new ControllerWaechterEinstellungen();

    public static Dictionary<string, string> Pruefen(ControllerWaechterEinstellungen e)
    {
        var fehler = new Dictionary<string, string>();
        for (var i = 0; i < e.Controller.Count; i++)
        {
            var c = e.Controller[i];
            string Feld(string name) => $"Controller[{i}].{name}";
            if (string.IsNullOrWhiteSpace(c.Name)) fehler[Feld(nameof(c.Name))] = "Gib dem Controller einen Namen.";
            if (c.Modus is not (ControllerWaechterEintrag.Neustarten or ControllerWaechterEintrag.NurMelden))
                fehler[Feld(nameof(c.Modus))] = "Melden + neu starten oder Nur melden wählen.";
            if (c.MeldenNachMin is < 1 or > 120) fehler[Feld(nameof(c.MeldenNachMin))] = "Zwischen 1 und 120 Minuten.";
            if (c.NeustartNachMin is < 1 or > 240) fehler[Feld(nameof(c.NeustartNachMin))] = "Zwischen 1 und 240 Minuten.";
            if (c.NeustartNachMin < c.MeldenNachMin) fehler[Feld(nameof(c.NeustartNachMin))] = "Der Neustart kommt nie vor der Meldung.";
            if (c.AusDauerSek is < 5 or > 120) fehler[Feld(nameof(c.AusDauerSek))] = "Zwischen 5 und 120 Sekunden.";
            if (c.MaxNeustartsProTag is < 1 or > 6) fehler[Feld(nameof(c.MaxNeustartsProTag))] = "Zwischen 1 und 6 pro Tag.";
            if (c.StatusEntity is null && c.FeuchteEntity is null)
                fehler[Feld(nameof(c.StatusEntity))] = "Wähle die Verbindung oder den Feuchtefühler des Controllers.";
            if (!Domaene(c.StatusEntity, "binary_sensor")) fehler[Feld(nameof(c.StatusEntity))] = "Die Verbindung ist ein binary_sensor.";
            if (!Domaene(c.FeuchteEntity, "sensor")) fehler[Feld(nameof(c.FeuchteEntity))] = "Der Feuchtefühler ist ein sensor.";
            if (!Domaene(c.SteckdoseEntity, "switch")) fehler[Feld(nameof(c.SteckdoseEntity))] = "Die Steckdose ist ein switch.";
        }

        if (e.Controller.Where(c => !string.IsNullOrEmpty(c.Id)).GroupBy(c => c.Id).Any(g => g.Count() > 1))
            fehler["Controller"] = "Jeder Controller kommt nur einmal vor.";
        return fehler;
    }

    private static bool Domaene(string? entityId, string domaene)
        => string.IsNullOrWhiteSpace(entityId) || entityId.StartsWith(domaene + ".", StringComparison.Ordinal);

    public (ControllerWaechterEinstellungen? Gespeichert, Dictionary<string, string> Fehler) Speichern(ControllerWaechterEinstellungen e)
    {
        foreach (var c in e.Controller)
        {
            c.Name = c.Name?.Trim() ?? string.Empty;
            c.StatusEntity = Leer(c.StatusEntity);
            c.FeuchteEntity = Leer(c.FeuchteEntity);
            c.SteckdoseEntity = Leer(c.SteckdoseEntity);
        }

        var fehler = Pruefen(e);
        if (fehler.Count > 0) return (null, fehler);

        var alt = Einstellungen.Controller.ToDictionary(c => c.Id);
        foreach (var c in e.Controller)
        {
            if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString("N")[..8];
            // Die Pause ist keine Einstellung, die das Formular mitschickt — sie bleibt, wie sie ist.
            c.PauseBisUtc = alt.TryGetValue(c.Id, out var vorher) ? vorher.PauseBisUtc : null;
        }

        // Ein entfernter Controller nimmt sein Gedächtnis mit.
        foreach (var weg in alt.Keys.Where(id => e.Controller.All(c => c.Id != id)))
        {
            _ablage.SetValue(MerkerSchluessel + weg, null);
            Letzte.TryRemove(weg, out _);
            Geprueft.TryRemove(weg, out _);
        }

        _repo.SetEinstellungen(Modul, e);
        return (e, fehler);
    }

    private static string? Leer(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Pause setzen (Stunden &gt; 0) oder aufheben (0).</summary>
    public bool Pausieren(string id, int stunden)
    {
        var e = Einstellungen;
        var c = e.Controller.FirstOrDefault(x => x.Id == id);
        if (c is null) return false;
        c.PauseBisUtc = stunden > 0 ? _jetztUtc().AddHours(stunden) : null;
        _repo.SetEinstellungen(Modul, e);
        return true;
    }

    private bool Pausiert(ControllerWaechterEintrag c) => c.PauseBisUtc is { } bis && bis > _jetztUtc();

    // ------------------------------------------------------------------ Stand

    public async Task<IReadOnlyList<ControllerStand>> StandAsync(CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var liste = new List<ControllerStand>();
        foreach (var c in Einstellungen.Controller)
        {
            var (lage, status, feuchte, dose) = await LageAsync(settings, c, ct);
            var merker = MerkerLaden(c.Id);
            var heute = merker.NeustartsUtc.Count(t => _jetztUtc() - t < ControllerWaechterRegeln.Tag);
            Letzte.TryGetValue(c.Id, out var letzte);
            liste.Add(new ControllerStand(
                c, lage.Befund.ToString(), lage.SeitUtc, status?.State, feuchte?.State, dose?.State, lage.SteckdoseSchaltbar,
                heute, Pausiert(c), letzte.Text, Geprueft.TryGetValue(c.Id, out var g) ? g : null));
        }
        return liste;
    }

    // ------------------------------------------------------------------ Lauf

    /// <summary>Ein Durchgang im Minutentakt über alle Controller.</summary>
    public async Task LaufAsync(CancellationToken ct)
    {
        var alle = Einstellungen.Controller;
        if (alle.Count == 0) return;
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        if (!settings.IsConfigured) return;

        foreach (var c in alle)
        {
            if (!c.Aktiv || Pausiert(c)) continue;
            try
            {
                await EinenPruefenAsync(settings, c, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Controller-Wächter für {Controller} fehlgeschlagen.", c.Id);
            }
        }
    }

    private async Task EinenPruefenAsync(HomeAssistantSettings settings, ControllerWaechterEintrag c, CancellationToken ct)
    {
        var (lage, status, feuchte, _) = await LageAsync(settings, c, ct);
        Geprueft[c.Id] = _jetztUtc();
        var merker = MerkerLaden(c.Id);
        var jetzt = _jetztUtc();
        var seitVorher = merker.AusfallSeitUtc;
        var aktionen = ControllerWaechterRegeln.Entscheiden(c, lage, merker, jetzt);
        var dauer = jetzt - (merker.AusfallSeitUtc ?? seitVorher ?? jetzt);

        // Erst merken, dann handeln (siehe Klassenkommentar).
        MerkerSpeichern(c.Id, merker);

        foreach (var a in aktionen)
        {
            switch (a)
            {
                case ControllerAktion.MeldenAusfall:
                    await MeldenAsync(c.Id,
                        $"{c.Name}: Controller {(lage.Befund == ControllerBefund.Eingefroren ? "liefert nichts mehr" : "offline")}",
                        AusfallText(c, lage, dauer, feuchte?.State), ct);
                    break;
                case ControllerAktion.Neustarten:
                    await NeustartenAsync(settings, c, dauer, ct);
                    break;
                case ControllerAktion.MeldenKeineHilfe:
                    await MeldenAsync(c.Id, $"{c.Name}: Neustart hat nicht geholfen",
                        $"Der Controller ist {ControllerWaechterRegeln.Kontrolle.TotalMinutes:0} Minuten nach dem Neustart noch offline. " +
                        $"Nächster Versuch frühestens in {ControllerWaechterRegeln.Ruhe.TotalMinutes:0} Minuten.", ct);
                    break;
                case ControllerAktion.MeldenLimit:
                    await MeldenAsync(c.Id, $"{c.Name}: Neustart-Grenze erreicht",
                        $"Der Controller wurde heute schon {c.MaxNeustartsProTag}× neu gestartet und ist noch offline. Der Wächter meldet nur noch — bitte nachsehen.", ct);
                    break;
                case ControllerAktion.MeldenWieder:
                    var text = merker.NeustartsUtc.Count > 0 && jetzt - merker.NeustartsUtc.Max() < TimeSpan.FromHours(2)
                        ? $"Der Controller ist nach dem Neustart wieder online{FeuchteSatz(feuchte?.State)} Der Ausfall dauerte {ControllerWaechterRegeln.Dauer(dauer)}."
                        : $"Der Controller ist wieder online{FeuchteSatz(feuchte?.State)} Der Ausfall dauerte {ControllerWaechterRegeln.Dauer(dauer)}.";
                    await MeldenAsync(c.Id, $"{c.Name}: Controller läuft wieder", text, ct);
                    Journalisieren($"{c.Name}: Controller war offline, läuft wieder", text);
                    break;
            }
        }
    }

    private static string FeuchteSatz(string? feuchte)
        => Zahlenlesen.Maschine(feuchte) is { } f
            ? $", Feuchte jetzt {f.ToString("0.0", CultureInfo.GetCultureInfo("de-DE"))} %."
            : ".";

    private static string AusfallText(ControllerWaechterEintrag c, ControllerLage lage, TimeSpan dauer, string? feuchte)
    {
        var wert = Zahlenlesen.Maschine(feuchte) is { } f
            ? $" (letzter Wert {f.ToString("0.0", CultureInfo.GetCultureInfo("de-DE"))} %)" : "";
        var was = lage.Befund == ControllerBefund.Eingefroren
            ? $"Die Feuchte steht seit {ControllerWaechterRegeln.Dauer(dauer)} still{wert}, obwohl der Controller verbunden ist."
            : $"Der Controller antwortet seit {ControllerWaechterRegeln.Dauer(dauer)} nicht{(c.FeuchteEntity is not null ? $"; die Feuchte wird nicht mehr gemessen{wert}" : "")}.";
        var folge = c.Modus == ControllerWaechterEintrag.Neustarten && lage.SteckdoseSchaltbar
            ? $" Neustart über die Steckdose nach {c.NeustartNachMin} Minuten Ausfall."
            : c.Modus == ControllerWaechterEintrag.Neustarten ? " Keine schaltbare Steckdose gewählt: bitte Strom kurz trennen." : " Im Modus „Nur melden“ ändert der Wächter nichts.";
        return was + folge;
    }

    // --------------------------------------------------------------- Lage lesen

    private async Task<(ControllerLage Lage, HomeAssistantState? Status, HomeAssistantState? Feuchte, HomeAssistantState? Dose)>
        LageAsync(HomeAssistantSettings settings, ControllerWaechterEintrag c, CancellationToken ct)
    {
        async Task<HomeAssistantState?> Lesen(string? id)
            => id is null ? null : await _funk.ZustandAsync(settings, id, ct);

        var status = await Lesen(c.StatusEntity);
        var feuchte = await Lesen(c.FeuchteEntity);
        var dose = await Lesen(c.SteckdoseEntity);

        // Eine zugeordnete Entität, die Home Assistant nicht liefert, ist „nicht prüfbar“, nicht „offline“:
        // Wer HA nicht erreicht, schaltet nichts.
        var erreichbar = (c.StatusEntity is null || status is not null) && (c.FeuchteEntity is null || feuchte is not null);
        var (befund, seit) = ControllerWaechterRegeln.Beurteilen(
            erreichbar,
            status?.State, status?.LastChanged,
            feuchte?.State, feuchte?.LastUpdated ?? feuchte?.LastChanged,
            _jetztUtc());
        var schaltbar = dose is not null && string.Equals(dose.State, "on", StringComparison.OrdinalIgnoreCase);
        return (new ControllerLage(befund, seit, schaltbar), status, feuchte, dose);
    }

    // ----------------------------------------------------------------- Neustart

    private async Task NeustartenAsync(HomeAssistantSettings settings, ControllerWaechterEintrag c, TimeSpan dauer, CancellationToken ct)
    {
        var dose = c.SteckdoseEntity!;
        await MeldenAsync(c.Id, $"{c.Name}: Controller startet neu",
            $"Offline seit {ControllerWaechterRegeln.Dauer(dauer)}. Die Steckdose wird für {c.AusDauerSek} Sekunden ausgeschaltet.", ct);
        Protokoll("restart-started", $"Neustart über {dose} nach {ControllerWaechterRegeln.Dauer(dauer)} Ausfall.", true);

        var aus = false;
        try
        {
            aus = await _ausschalter.AusschaltenAsync(settings, dose, _warten);
            if (aus) await _warten(TimeSpan.FromSeconds(c.AusDauerSek), CancellationToken.None);
        }
        finally
        {
            // Die Steckdose darf NIE aus bleiben, auch wenn das Ausschalten nicht bestätigt wurde oder etwas dazwischenkam.
            var an = await EinschaltenAsync(settings, dose);
            Protokoll(an ? "restart-done" : "restart-failed",
                an ? $"{dose} ist wieder an." : $"{dose} ließ sich nicht bestätigt einschalten.", an);
            if (!an)
            {
                await MeldenAsync(c.Id, $"{c.Name}: Steckdose bleibt AUS",
                    "Nach dem Neustart ließ sich die Steckdose nicht bestätigt einschalten. Bitte sofort in Home Assistant einschalten — der Controller hat keinen Strom.", CancellationToken.None);
            }
            else if (!aus)
            {
                _logger.LogWarning("Controller-Wächter: {Steckdose} meldete nach dem Ausschalten nicht „aus“; wieder eingeschaltet.", dose);
            }
        }

        Journalisieren($"{c.Name}: Controller neu gestartet", $"Offline seit {ControllerWaechterRegeln.Dauer(dauer)}, Steckdose {c.AusDauerSek} Sekunden aus und wieder an.");
    }

    public async Task<bool> EinschaltenAsync(HomeAssistantSettings settings, string entityId)
    {
        var domain = entityId.Split('.', 2)[0];
        var leer = new Dictionary<string, object>();
        var nachfragen = Math.Max(1, (int)Math.Ceiling(Ausschalter.Wartezeit / Ausschalter.Nachfragetakt));
        for (var versuch = 1; versuch <= Ausschalter.Versuche; versuch++)
        {
            await _funk.SchickenAsync(settings, domain, "turn_on", entityId, leer, CancellationToken.None);
            for (var frage = 1; frage <= nachfragen; frage++)
            {
                await _warten(Ausschalter.Nachfragetakt, CancellationToken.None);
                var ist = (await _funk.ZustandAsync(settings, entityId, CancellationToken.None))?.State;
                if (string.Equals(ist, "on", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------- Helfer

    private ControllerMerker MerkerLaden(string id)
    {
        try
        {
            var json = _ablage.GetValue(MerkerSchluessel + id);
            return string.IsNullOrWhiteSpace(json) ? new ControllerMerker() : JsonSerializer.Deserialize<ControllerMerker>(json) ?? new ControllerMerker();
        }
        catch (JsonException)
        {
            // Ein unlesbarer Merker gilt als leer: im Zweifel einmal zu viel melden, nie schweigen.
            return new ControllerMerker();
        }
    }

    private void MerkerSpeichern(string id, ControllerMerker m)
        => _ablage.SetValue(MerkerSchluessel + id, JsonSerializer.Serialize(m));

    private async Task MeldenAsync(string id, string titel, string text, CancellationToken ct)
    {
        Letzte[id] = (titel + " — " + text, _jetztUtc());
        // Trotz Ruhezeit: ein Ausfall mitten in der Nacht lässt die Feuchte bis zum Morgen steigen.
        await _notifications.SendAsync(NotificationCategory.Risk, titel, text, ct,
            trotzRuhezeit: true, seite: NotificationService.LiveSeite());
    }

    private void Protokoll(string aktion, string text, bool ok)
        => _audit.Add(new SystemAuditEvent
        {
            EventType = ProtokollTyp,
            Action = aktion,
            Summary = text,
            Severity = ok ? "info" : "warning",
            Success = ok,
        });

    private void Journalisieren(string titel, string text)
    {
        try
        {
            foreach (var zelt in _grows.GetTents())
            foreach (var grow in zelt.ActiveGrows)
            {
                _journal.Create(new JournalEntry
                {
                    GrowId = grow.Id,
                    Title = titel,
                    Body = text,
                    EntryType = JournalEntryType.Action,
                    Source = ValueOrigin.Derived,
                    OccurredAtUtc = _jetztUtc(),
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Controller-Wächter: Journal-Eintrag fehlgeschlagen.");
        }
    }

    /// <summary>Für Tests: Anzeigezustand vergessen.</summary>
    public static void Vergessen()
    {
        Letzte.Clear();
        Geprueft.Clear();
    }
}
