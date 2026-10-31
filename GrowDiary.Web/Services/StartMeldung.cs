using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Services;

/// <summary>
/// Die Start-Meldungen: Cultivate Control wurde neu gestartet oder aktualisiert, Home Assistant ist wieder da.
/// Die Texte sind rein (testbar); der Versand läuft über <see cref="NotificationService"/> und folgt damit
/// dem Schalter „Start-Meldungen“, der Ruhezeit und dem gewählten Gerät.
/// </summary>
public sealed class StartMeldung
{
    /// <summary>Der Schlüssel, unter dem die zuletzt gestartete Version steht.</summary>
    public const string VersionSchluessel = "system:letzteGestarteteVersion";

    /// <summary>Eine kürzere Lücke zählt nicht als Neustart von Home Assistant, sondern als Wackler.</summary>
    public static readonly TimeSpan MindestLuecke = TimeSpan.FromSeconds(90);

    private readonly NotificationService _benachrichtigung;
    private readonly AppSettingsRepository _einstellungen;
    private readonly SupervisorInfoService _supervisor;
    private readonly ILogger<StartMeldung> _logger;

    public StartMeldung(
        NotificationService benachrichtigung,
        AppSettingsRepository einstellungen,
        SupervisorInfoService supervisor,
        ILogger<StartMeldung> logger)
    {
        _benachrichtigung = benachrichtigung;
        _einstellungen = einstellungen;
        _supervisor = supervisor;
        _logger = logger;
    }

    /// <summary>
    /// Text beim Start. Neue Version (oder erste bekannte): „aktualisiert“ mit Nummer.
    /// Gleiche oder unbekannte Version: „neu gestartet“ ohne Nummer.
    /// </summary>
    public static (string Titel, string Text) Start(string? bisher, string? jetzt)
    {
        var aktuell = jetzt?.Trim();
        if (!string.IsNullOrEmpty(aktuell) && !string.Equals(bisher?.Trim(), aktuell, StringComparison.Ordinal))
        {
            return ("🌱 Cultivate Control · Update", $"Cultivate Control wurde auf Version {aktuell} aktualisiert.");
        }

        return ("🌱 Cultivate Control · Neustart", "Cultivate Control wurde neu gestartet.");
    }

    /// <summary>Text, wenn Home Assistant nach einer Lücke wieder antwortet; null bei einem bloßen Wackler.</summary>
    public static (string Titel, string Text)? HomeAssistantZurueck(TimeSpan luecke)
    {
        if (luecke < MindestLuecke)
        {
            return null;
        }

        var minuten = Math.Max(1, (int)Math.Round(luecke.TotalMinutes, MidpointRounding.AwayFromZero));
        return ("🌱 Cultivate Control · Home Assistant", $"Home Assistant ist wieder erreichbar (nach {minuten} Min.).");
    }

    /// <summary>
    /// Meldet den Start einmal je Prozess. Home Assistant braucht nach einem gemeinsamen Neustart oft
    /// noch einige Minuten — deshalb mehrere Versuche, aber nur, solange die Meldung überhaupt gewollt ist.
    /// </summary>
    public async Task StartMelden(CancellationToken ct, TimeSpan? abstand = null, int versuche = 20)
    {
        var jetzt = await _supervisor.GetAddonVersionAsync(ct);
        var bisher = _einstellungen.GetValue(VersionSchluessel);
        if (!string.IsNullOrWhiteSpace(jetzt))
        {
            // Zuerst merken: stürzt der Prozess in einer Schleife, kommt nicht jedes Mal eine neue Meldung.
            _einstellungen.SetValue(VersionSchluessel, jetzt.Trim());
        }

        var (titel, text) = Start(bisher, jetzt);
        await MeldenAsync(titel, text, ct, abstand ?? TimeSpan.FromSeconds(30), versuche);
    }

    public async Task HomeAssistantZurueckMelden(TimeSpan luecke, CancellationToken ct)
    {
        if (HomeAssistantZurueck(luecke) is not { } meldung)
        {
            return;
        }

        await MeldenAsync(meldung.Titel, meldung.Text, ct, TimeSpan.FromSeconds(30), 3);
    }

    private async Task MeldenAsync(string titel, string text, CancellationToken ct, TimeSpan abstand, int versuche)
    {
        for (var versuch = 1; versuch <= versuche; versuch++)
        {
            var einstellungen = _benachrichtigung.GetSettings();
            // Nicht eingerichtet, abgeschaltet oder Ruhezeit: kein Versand und auch kein Wiederholen.
            if (!einstellungen.IsConfigured
                || !einstellungen.IsCategoryEnabled(NotificationCategory.Start)
                || einstellungen.IsQuietHour(DateTime.Now.Hour))
            {
                return;
            }

            if (await _benachrichtigung.SendAsync(NotificationCategory.Start, titel, text, ct))
            {
                return;
            }

            if (versuch < versuche)
            {
                await Task.Delay(abstand, ct);
            }
        }

        _logger.LogWarning("Start-Meldung nicht zugestellt: {Text}", text);
    }
}

/// <summary>Meldet den Start des Add-ons, sobald der Prozess eine Weile läuft.</summary>
public sealed class StartMeldungWorker : BackgroundService
{
    private readonly IServiceProvider _dienste;
    private readonly ILogger<StartMeldungWorker> _logger;

    public StartMeldungWorker(IServiceProvider dienste, ILogger<StartMeldungWorker> logger)
    {
        _dienste = dienste;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Die übrigen Worker brauchen einen Moment, bis Home Assistant erreichbar ist.
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            using var scope = _dienste.CreateScope();
            await scope.ServiceProvider.GetRequiredService<StartMeldung>().StartMelden(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Start-Meldung fehlgeschlagen.");
        }
    }
}
