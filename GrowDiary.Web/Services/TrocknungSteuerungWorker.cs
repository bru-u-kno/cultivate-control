using GrowDiary.Web.Infrastructure;

namespace GrowDiary.Web.Services;

/// <summary>
/// Zieht die Grenzen am Trockenzelt nach (A-029, Etappe 3, Teil B): beim Start und bei jedem Etappenwechsel, ohne zu
/// wissen, wann einer war — der Abgleich vergleicht nur Ist und Plan und schreibt bei Abweichung. Den Entfeuchter schaltet
/// der Fork selbst ein und aus (jede Minute, mit Hysterese und Mindestpause).
/// </summary>
public sealed class TrocknungSteuerungWorker : BackgroundService
{
    // Der Entfeuchter wird jede Minute geprüft (Schalten mit Hysterese), die Grenzen der übrigen Geräte nur jede fünfte.
    private static readonly TimeSpan Takt = TimeSpan.FromMinutes(1);
    private const int GrenzenAlle = 5;
    private int _lauf;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TrocknungSteuerungWorker> _logger;

    public TrocknungSteuerungWorker(IServiceProvider serviceProvider, ILogger<TrocknungSteuerungWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(100), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EinmalAsync(stoppingToken, _lauf++ % GrenzenAlle == 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Trocknung: Durchlauf fehlgeschlagen.");
            }

            try { await Task.Delay(Takt, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    /// <summary>Ein Durchlauf über alle Zelte auf „Regeln“. Öffentlich für den Test der Verdrahtung.</summary>
    public async Task<int> EinmalAsync(CancellationToken ct, bool grenzen = true)
    {
        var repository = _serviceProvider.GetRequiredService<TrocknungSteuerungRepository>();
        var dienst = _serviceProvider.GetRequiredService<TrocknungSteuerungService>();
        var zuluft = _serviceProvider.GetRequiredService<TrocknungZuluftService>();
        var geschrieben = 0;
        var imBetrieb = repository.ZelteImBetrieb(TrocknungSteuerungRepository.BetriebRegeln);
        // Ein Zelt, dessen Zuluft der Fork gerade führt, aber nicht mehr auf „Regeln“ steht, bekommt noch einen Lauf: Er gibt die Zuluft zurück.
        foreach (var zelt in zuluft.GefuehrteZelte().Except(imBetrieb))
            await zuluft.LaufAsync(zelt, ct);
        foreach (var zelt in imBetrieb)
        {
            if (await dienst.SchaltenAsync(zelt, ct)) geschrieben++;
            if (await dienst.AbluftStufeAsync(zelt, ct)) geschrieben++;
            if (await zuluft.LaufAsync(zelt, ct)) geschrieben++;
            if (grenzen) geschrieben += (await dienst.AbgleichenAsync(zelt, ct)).Geschrieben;
        }

        return geschrieben;
    }
}
