using GrowDiary.Web.Infrastructure;

namespace GrowDiary.Web.Services;

/// <summary>
/// Zieht die Grenzen am Trockenzelt nach (A-029, Etappe 3, Teil B): beim Start und bei jedem Etappenwechsel, ohne zu
/// wissen, wann einer war — der Abgleich vergleicht nur Ist und Plan und schreibt bei Abweichung.
/// </summary>
public sealed class TrocknungSteuerungWorker : BackgroundService
{
    private static readonly TimeSpan Takt = TimeSpan.FromMinutes(5);

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
                await EinmalAsync(stoppingToken);
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
    public async Task<int> EinmalAsync(CancellationToken ct)
    {
        var repository = _serviceProvider.GetRequiredService<TrocknungSteuerungRepository>();
        var dienst = _serviceProvider.GetRequiredService<TrocknungSteuerungService>();
        var geschrieben = 0;
        foreach (var zelt in repository.ZelteImBetrieb(TrocknungSteuerungRepository.BetriebRegeln))
            geschrieben += (await dienst.AbgleichenAsync(zelt, ct)).Geschrieben;
        return geschrieben;
    }
}
