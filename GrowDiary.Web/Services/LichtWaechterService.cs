using System.Collections.Concurrent;
using System.Globalization;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>Wie der Wächter die jüngste Phase beurteilt hat — für die Anzeige.</summary>
/// <param name="Phase">Geplante Zeit der Phase, HH:mm.</param>
/// <param name="Hell">Die Phase war eine Lichtphase.</param>
/// <param name="Ergebnis">ok / korrigiert / gemeldet</param>
/// <param name="Text">Ein Satz für die Oberfläche.</param>
/// <param name="GeprueftUtc">Wann zuletzt geprüft wurde.</param>
public sealed record WaechterLetzter(string Phase, bool Hell, string Ergebnis, string Text, DateTime GeprueftUtc);

/// <summary>Der Zustand des Wächters für die Oberfläche.</summary>
public sealed record WaechterStand(
    LichtWaechterEinstellungen Einstellungen,
    bool PlanVorhanden,
    string? Plan,
    bool SensorZugeordnet,
    double? Sensor,
    bool Pausiert,
    WaechterLetzter? Letzter);

/// <summary>
/// Der Licht-Wächter: prüft fünf Minuten nach jeder geplanten Ein- und Ausschaltzeit, ob der
/// Lichtsensor zum Lichtplan passt, und handelt nach den Regeln in
/// <see cref="LichtWaechterRegeln"/>.
/// </summary>
/// <remarks>
/// Läuft im Minutentakt des Alarm-Wächters (<see cref="AlertWatchWorker"/>). Der Zustand je Phase
/// steht im Speicher, weil der Dienst je Anfrage neu entsteht; nach einem Neustart innerhalb des
/// Prüffensters wird die Phase einfach noch einmal beurteilt — ist alles in Ordnung, bleibt es still.
/// </remarks>
public sealed class LichtWaechterService
{
    public const string Modul = "licht_waechter";

    private sealed class PhasenZustand
    {
        public int Korrekturen;
        public DateTime NaechsteAbLokal = DateTime.MinValue;
        public bool Fertig;
        public string? ErsteUrsache;
        public string? ErsteLage;
    }

    private static readonly ConcurrentDictionary<string, PhasenZustand> Phasen = new();
    private static WaechterLetzter? _letzter;

    private readonly SteuerungRepository _repo;
    private readonly LichtSteuerungService _licht;
    private readonly GrowRepository _grows;
    private readonly LightRepository _lights;
    private readonly NotificationService _notifications;
    private readonly JournalRepository _journal;
    private readonly ILogger<LichtWaechterService> _logger;
    private readonly Func<DateTime> _jetztUtc;

    public LichtWaechterService(
        SteuerungRepository repo,
        LichtSteuerungService licht,
        GrowRepository grows,
        LightRepository lights,
        NotificationService notifications,
        JournalRepository journal,
        ILogger<LichtWaechterService> logger,
        Func<DateTime>? jetztUtc = null)
    {
        _repo = repo;
        _licht = licht;
        _grows = grows;
        _lights = lights;
        _notifications = notifications;
        _journal = journal;
        _logger = logger;
        _jetztUtc = jetztUtc ?? (() => DateTime.UtcNow);
    }

    // ----------------------------------------------------------- Einstellungen

    public LichtWaechterEinstellungen Einstellungen
        => _repo.GetEinstellungen<LichtWaechterEinstellungen>(Modul) ?? new LichtWaechterEinstellungen();

    public static Dictionary<string, string> Pruefen(LichtWaechterEinstellungen e)
    {
        var fehler = new Dictionary<string, string>();
        if (e.Modus is not (LichtWaechterEinstellungen.Korrigieren or LichtWaechterEinstellungen.NurMelden))
            fehler[nameof(e.Modus)] = "Korrigieren + melden oder Nur melden wählen.";
        if (e.HellAbProzent is < 1 or > 100)
            fehler[nameof(e.HellAbProzent)] = "Wert zwischen 1 und 100 Prozent.";
        return fehler;
    }

    public (LichtWaechterEinstellungen? Gespeichert, Dictionary<string, string> Fehler) Speichern(LichtWaechterEinstellungen e)
    {
        var fehler = Pruefen(e);
        if (fehler.Count > 0) return (null, fehler);
        _repo.SetEinstellungen(Modul, e);
        return (e, fehler);
    }

    /// <summary>Pause setzen (Stunden &gt; 0) oder aufheben (0).</summary>
    public LichtWaechterEinstellungen Pausieren(int stunden)
    {
        var e = Einstellungen;
        e.PauseBisUtc = stunden > 0 ? _jetztUtc().AddHours(stunden) : null;
        _repo.SetEinstellungen(Modul, e);
        return e;
    }

    /// <summary>
    /// Fork AI (cc.277, „Zelt ruhend"): Pause bis zu einem festen Zeitpunkt — für die Ruhezeit des Zelts, die länger dauert
    /// als die 72 Stunden „bis zur Ernte". „Zelt wieder aktiv" hebt sie mit <see cref="Pausieren"/>(0) auf.
    /// </summary>
    public LichtWaechterEinstellungen PausierenBis(DateTime bisUtc)
    {
        var e = Einstellungen;
        e.PauseBisUtc = bisUtc;
        _repo.SetEinstellungen(Modul, e);
        return e;
    }

    // ------------------------------------------------------------------ Stand

    public async Task<WaechterStand> StandAsync(CancellationToken ct)
    {
        var e = Einstellungen;
        var plan = PlanLaden();
        var (zugeordnet, prozent) = await _licht.LichtSensorAsync(ct);
        return new WaechterStand(
            e, plan is not null, plan is { } p ? $"{p.Ein}–{p.Aus}" : null,
            zugeordnet, prozent, Pausiert(e), _letzter);
    }

    private bool Pausiert(LichtWaechterEinstellungen e) => e.PauseBisUtc is { } bis && bis > _jetztUtc();

    // ------------------------------------------------------------------ Lauf

    /// <summary>Ein Durchgang im Minutentakt.</summary>
    public async Task LaufAsync(CancellationToken ct)
    {
        var e = Einstellungen;
        if (!e.Aktiv || Pausiert(e)) return;

        var plan = PlanLaden();
        if (plan is null) return;

        var jetzt = TimeZoneInfo.ConvertTimeFromUtc(_jetztUtc(), plan.Value.Zone);
        if (LichtWaechterRegeln.LetzteGrenze(jetzt, plan.Value.Ein, plan.Value.Aus) is not { } grenze) return;
        if (jetzt < grenze.Zeitpunkt + LichtWaechterRegeln.Wartezeit
            || jetzt > grenze.Zeitpunkt + LichtWaechterRegeln.Fenster) return;

        var schluessel = grenze.Zeitpunkt.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        foreach (var alt in Phasen.Keys.Where(k => string.CompareOrdinal(k, schluessel) < 0).ToList())
            Phasen.TryRemove(alt, out _);

        var zustand = Phasen.GetOrAdd(schluessel, _ => new PhasenZustand());
        if (zustand.Fertig || zustand.NaechsteAbLokal > jetzt) return;

        var live = await _licht.LiveAsync(ct);
        var (zugeordnet, prozent) = await _licht.LichtSensorAsync(ct);
        var lage = new WaechterLage(
            SollHell: grenze.Hell,
            Sensor: prozent,
            SensorZugeordnet: zugeordnet,
            HellAb: e.HellAbProzent,
            Erreichbar: live.HaErreichbar && live.ControllerOnline != false,
            Modus: live.Modus,
            EinZeit: live.EinZeit,
            AusZeit: live.AusZeit,
            Stufe: live.Stufe,
            PlanEin: plan.Value.Ein,
            PlanAus: plan.Value.Aus);

        var befund = LichtWaechterRegeln.Beurteilen(lage);
        var seit = grenze.Zeitpunkt.ToString("HH:mm", CultureInfo.InvariantCulture);
        var kopf = $"{LichtWaechterRegeln.Soll(lage, seit)}, {LichtWaechterRegeln.SensorText(lage)}.";
        var uhr = jetzt.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (befund == WaechterBefund.Ok)
        {
            zustand.Fertig = true;
            if (zustand.Korrekturen > 0)
            {
                var text = $"{zustand.ErsteLage} {zustand.ErsteUrsache} Zeitplan wiederhergestellt, {uhr} {LichtWaechterRegeln.SensorText(lage)}.";
                await MeldenAsync(grenze.Hell ? "Licht war aus – behoben" : "Licht war an – behoben", text, ct);
                Journalisieren(grenze.Hell ? "Licht-Wächter: Licht war aus, behoben" : "Licht-Wächter: Licht war an, behoben", text);
                Merken(seit, grenze.Hell, "korrigiert", "Betriebsart oder Zeiten korrigiert, danach in Ordnung.");
            }
            else
            {
                Merken(seit, grenze.Hell, "ok", $"Sensor passt zum Plan ({LichtWaechterRegeln.SensorText(lage)}).");
            }
            return;
        }

        if (LichtWaechterRegeln.Korrigierbar(befund)
            && e.Modus == LichtWaechterEinstellungen.Korrigieren
            && zustand.Korrekturen < LichtWaechterRegeln.MaxKorrekturen)
        {
            zustand.ErsteLage ??= kopf;
            zustand.ErsteUrsache ??= LichtWaechterRegeln.Ursache(befund, lage);
            zustand.Korrekturen++;
            zustand.NaechsteAbLokal = jetzt + LichtWaechterRegeln.Kontrollabstand;
            var ok = await _licht.ZeitplanWiederherstellenAsync(plan.Value.Ein, plan.Value.Aus, ct);
            _logger.LogWarning("Licht-Wächter: {Ursache} Zeitplan {Ergebnis} (Versuch {N}).",
                LichtWaechterRegeln.Ursache(befund, lage), ok ? "wiederhergestellt" : "nicht bestätigt", zustand.Korrekturen);
            Merken(seit, grenze.Hell, "korrigiert", $"{LichtWaechterRegeln.Ursache(befund, lage)} Zeitplan zurückgesetzt, Kontrolle in {LichtWaechterRegeln.Kontrollabstand.TotalMinutes:0} Minuten.");
            return;
        }

        zustand.Fertig = true;
        var (titel, nachricht) = MeldungFuer(befund, lage, grenze.Hell, kopf, zustand.Korrekturen);
        await MeldenAsync(titel, nachricht, ct);
        Merken(seit, grenze.Hell, "gemeldet", nachricht);
    }

    private static (string Titel, string Text) MeldungFuer(WaechterBefund befund, WaechterLage l, bool hell, string kopf, int korrekturen)
    {
        var ursache = LichtWaechterRegeln.Ursache(befund, l);
        return befund switch
        {
            WaechterBefund.NichtErreichbar => ("Licht nicht prüfbar", $"{kopf} {ursache} Es wird nichts geschaltet."),
            WaechterBefund.SensorAusgefallen => ("Licht nicht prüfbar", $"{ursache} Betriebsart und Zeiten wurden weiter geprüft."),
            WaechterBefund.Ungeklaert or WaechterBefund.StufeNull => (
                hell ? "Licht dunkel – bitte prüfen" : "Licht an in der Dunkelphase – bitte prüfen",
                $"{kopf} {ursache} Das kann der Fork nicht beheben: Lampe, Netzteil oder Sensor?"),
            _ when korrekturen > 0 => (
                hell ? "Licht dunkel – Korrektur ohne Wirkung" : "Licht an – Korrektur ohne Wirkung",
                $"{kopf} {ursache} Der Zeitplan wurde {korrekturen}× zurückgesetzt, der Sensor bestätigt es nicht. Bitte nachsehen."),
            _ => (
                hell ? "Licht dunkel – bitte prüfen" : "Licht an in der Dunkelphase – bitte prüfen",
                $"{kopf} {ursache} Im Modus „Nur melden“ ändert der Fork nichts."),
        };
    }

    private async Task MeldenAsync(string titel, string text, CancellationToken ct)
    {
        // Trotz Ruhezeit: ein dunkles Zelt um 05:05 will niemand erst um 07:00 erfahren.
        await _notifications.SendAsync(NotificationCategory.Risk, titel, text, ct,
            trotzRuhezeit: true, seite: NotificationService.LiveSeite());
    }

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
            _logger.LogWarning(ex, "Licht-Wächter: Journal-Eintrag fehlgeschlagen.");
        }
    }

    private void Merken(string phase, bool hell, string ergebnis, string text)
        => _letzter = new WaechterLetzter(phase, hell, ergebnis, text, _jetztUtc());

    /// <summary>Für Tests: Zustand vergessen.</summary>
    internal static void Vergessen()
    {
        Phasen.Clear();
        _letzter = null;
    }

    // ------------------------------------------------------------------ Plan

    private (string Ein, string Aus, TimeZoneInfo Zone)? PlanLaden()
    {
        foreach (var zelt in _grows.GetTents())
        {
            var plan = _lights.GetActiveLightScheduleForTent(zelt.Id);
            if (plan is null) continue;
            if (!TimeOnly.TryParse(plan.LightsOnTime, out var ein) || !TimeOnly.TryParse(plan.LightsOffTime, out var aus) || ein == aus)
                continue;
            return (ein.ToString("HH:mm", CultureInfo.InvariantCulture), aus.ToString("HH:mm", CultureInfo.InvariantCulture), ZoneFuer(plan));
        }

        return null;
    }

    private static TimeZoneInfo ZoneFuer(LightSchedule plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.TimeZoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(plan.TimeZoneId); }
            catch (TimeZoneNotFoundException) { /* falsch geschriebene Zone: Serverzeit */ }
        }

        return TimeZoneInfo.Local;
    }
}
