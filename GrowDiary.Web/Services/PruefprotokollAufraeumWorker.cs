using GrowDiary.Web.Infrastructure;

namespace GrowDiary.Web.Services;

/// <summary>
/// Hält das Prüfprotokoll klein: löscht beim Start die Lese-Zeilen der Zugriffs-Middleware
/// aus der Zeit vor dem 02.11.2026 und danach einmal am Tag alles, was älter als
/// <see cref="Aufbewahrung"/> ist (das KI-Zugriffs-Protokoll: <see cref="AufbewahrungKi"/>).
/// </summary>
/// <remarks>
/// <para><b>Der Anlass (02.11.2026).</b> Die Datenbank hatte 87 MB, davon 81 MB im Prüfprotokoll:
/// 182.180 von 182.400 Zeilen waren <c>GET /api/settings/…</c>, ohne Aufbewahrungsfrist.</para>
///
/// <para><b>Verdichten nur nach großem Löschen.</b> <c>VACUUM</c> sperrt das Schreiben kurz und
/// schreibt die Datei neu; deshalb erst ab <see cref="VerdichtenAb"/> gelöschten Zeilen, nicht
/// nach jeder Handvoll.</para>
/// </remarks>
public sealed class PruefprotokollAufraeumWorker : BackgroundService
{
    public static readonly TimeSpan Aufbewahrung = TimeSpan.FromDays(90);
    public static readonly TimeSpan AufbewahrungKi = TimeSpan.FromDays(365);
    public const int VerdichtenAb = 5000;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PruefprotokollAufraeumWorker> _logger;

    public PruefprotokollAufraeumWorker(IServiceProvider serviceProvider, ILogger<PruefprotokollAufraeumWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Einmal(_serviceProvider.GetRequiredService<SystemAuditRepository>(), DateTime.UtcNow, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Aufräumen des Prüfprotokolls fehlgeschlagen.");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Ein Durchlauf; gibt die Zahl gelöschter Zeilen zurück. Für Tests ohne Dienst-Rahmen.</summary>
    public static int Einmal(SystemAuditRepository audit, DateTime jetztUtc, ILogger? logger = null)
    {
        var vorher = audit.Zaehle();
        var geloescht = audit.LoescheErlaubteLesezugriffe()
                        + audit.LoescheAelterAls(jetztUtc - Aufbewahrung, jetztUtc - AufbewahrungKi);
        if (geloescht >= VerdichtenAb)
        {
            audit.Verdichten();
        }

        if (geloescht > 0)
        {
            logger?.LogInformation(
                "Prüfprotokoll aufgeräumt: {Geloescht} von {Vorher} Zeilen gelöscht{Verdichtet}.",
                geloescht, vorher, geloescht >= VerdichtenAb ? ", Datei verdichtet" : string.Empty);
        }

        return geloescht;
    }
}
