using System.Text.RegularExpressions;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Wohin der Tipp auf eine Push-Meldung führt — je Meldungsart.
/// </summary>
/// <remarks>
/// forkai.168 schickte Grenzwert- und Risiko-Meldungen auf „Aufgaben". Dort
/// steht weder eine Grenzwert-Überschreitung noch ein Trend-Befund; der Nutzer bekam
/// fast nur Grenzwert-Meldungen und landete „eigentlich immer bei den Aufgaben"
/// (04.10.2026). Geprüft wird hier der ganze Weg: Alarm → Push an HA →
/// <c>clickAction</c>.
/// </remarks>
public sealed class PushZielJeMeldungTests : IDisposable
{
    private const string Slug = "00d580e3_cultivate_control";

    private readonly string _contentRoot;
    private readonly AppPaths _paths;
    private readonly Tent _tent;
    private readonly AlertRuleRepository _rules;
    private readonly NotificationSettingsRepository _notificationSettings;
    private readonly GrowRepository _growRepository;
    private readonly string? _savedSupervisorToken;

    public PushZielJeMeldungTests()
    {
        _contentRoot = Path.Combine(Path.GetTempPath(), $"grow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_contentRoot);
        _paths = new AppPaths(_contentRoot);
        _tent = TestDatabase.InitializeWithDefaultTent(_paths);
        _rules = new AlertRuleRepository(_paths);
        _notificationSettings = new NotificationSettingsRepository(_paths);
        _growRepository = new GrowRepository(_paths);

        // Mit Supervisor-Token: nur dann kennt Cultivate Control seinen Slug und setzt ein Ziel.
        _savedSupervisorToken = Environment.GetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable);
        Environment.SetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable, "supervisor-token");

        _growRepository.SaveHomeAssistantSettings(new HomeAssistantSettings { BaseUrl = "http://ha.local:8123", AccessToken = "token", Enabled = true });
        _notificationSettings.SaveNotificationSettings(new NotificationSettings { NotifyService = "notify.mobile_app_test", QuietHoursStartHour = null, QuietHoursEndHour = null });
        _rules.ReplaceForTent(_tent.Id, new[]
        {
            new TentAlertRule { TentId = _tent.Id, MetricKey = "reservoir-ph", MinValue = 5.5, MaxValue = 6.5, Enabled = true, CooldownMinutes = 30 },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable, _savedSupervisorToken);
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    /// <summary>HA und Supervisor in einem: der Supervisor nennt den Slug, HA nimmt den Push an.</summary>
    private static RecordingHttpHandler Handler() => new((request, _) =>
        request.RequestUri!.Host == "supervisor"
            ? RecordingHttpHandler.Json($$$"""{"result":"ok","data":{"slug":"{{{Slug}}}"}}""")
            : RecordingHttpHandler.Json("[]"));

    private NotificationService Benachrichtigung(RecordingHttpHandler handler, IngressPanelService? ingressPanel = null)
    {
        var fabrik = new StubHttpClientFactory(handler);
        return new NotificationService(
            _notificationSettings,
            _growRepository,
            new HomeAssistantService(fabrik, NullLogger<HomeAssistantService>.Instance),
            NullLogger<NotificationService>.Instance,
            new SupervisorInfoService(fabrik, NullLogger<SupervisorInfoService>.Instance),
            ingressPanel);
    }

    /// <summary>Home Assistant mit einem Ingress-Panel ohne Kopfleiste für dieses Add-on — oder ohne.</summary>
    private static IngressPanelService IngressPanel(bool vorhanden) => new(
        NullLogger<IngressPanelService>.Instance,
        TimeProvider.System,
        (_, _) => Task.FromResult<System.Text.Json.JsonElement?>(System.Text.Json.JsonDocument.Parse(vorhanden
            ? """{"growos":{"component_name":"custom","url_path":"growos","config":{"addon":"SLUG","ui_mode":"normal","index":"","_panel_custom":{"name":"ha-panel-ingress"}}}}""".Replace("SLUG", Slug)
            : """{"lovelace":{"component_name":"lovelace","url_path":"lovelace","config":{}}}""").RootElement.Clone()));

    [Fact]
    public async Task Mit_Ingress_Panel_fuehrt_der_Tipp_dorthin_ohne_HA_Kopfleiste()
    {
        // Der Nutzer öffnet Cultivate Control am Handy über dieses Panel (05.10.2026); /app/<slug>
        // zeigt die weiße HA-Leiste darüber.
        var handler = Handler();
        var alarme = new AlertEvaluationService(_rules, Benachrichtigung(handler, IngressPanel(vorhanden: true)), NullLogger<AlertEvaluationService>.Instance);

        await alarme.EvaluateAsync(_tent, new Dictionary<string, HomeAssistantState>
        {
            ["reservoir-ph"] = new HomeAssistantState { State = "5", NumericValue = 5.0 },
        });

        var body = Push(handler);
        Assert.Contains($"\"clickAction\":\"/growos?index=live%2F{_tent.Id}\"", body);
        Assert.Contains($"\"url\":\"/growos?index=live%2F{_tent.Id}\"", body);
    }

    [Fact]
    public async Task Ohne_Ingress_Panel_bleibt_es_beim_App_Panel()
    {
        var handler = Handler();

        Assert.True(await Benachrichtigung(handler, IngressPanel(vorhanden: false)).SendAsync(NotificationCategory.System, "t", "m"));

        Assert.Contains($"\"clickAction\":\"/app/{Slug}/aufgaben\"", Push(handler));
    }

    [Fact]
    public async Task Trockenzelt_meldet_keine_Reservoirwerte()
    {
        // Ein Trockenzelt hat kein Reservoir: eine pH-Regel darf dort nie klingeln.
        var handler = Handler();
        var alarme = new AlertEvaluationService(_rules, Benachrichtigung(handler), NullLogger<AlertEvaluationService>.Instance);
        _tent.TentType = TentType.Drying;

        await alarme.EvaluateAsync(_tent, new Dictionary<string, HomeAssistantState>
        {
            ["reservoir-ph"] = new HomeAssistantState { State = "5", NumericValue = 5.0 },
        });

        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/api/services/notify/", StringComparison.Ordinal));
    }

    // ---- Zelt ruhend / leeres Zelt: die Verdrahtung der Alarm-Regel (SchweigtZelt) gegen echte Daten ----

    private static readonly Dictionary<string, HomeAssistantState> PhZuNiedrig = new()
    {
        ["reservoir-ph"] = new HomeAssistantState { State = "5", NumericValue = 5.0 },
    };

    private AlertEvaluationService AlarmeMitGrows(RecordingHttpHandler handler)
        => new(_rules, Benachrichtigung(handler), NullLogger<AlertEvaluationService>.Instance,
               grows: _growRepository, steuerung: new SteuerungRepository(_paths));

    private static bool PushGesendet(RecordingHttpHandler handler)
        => handler.Requests.Any(r => r.Uri.AbsolutePath.Contains("/api/services/notify/", StringComparison.Ordinal));

    private int NeuerGrow(GrowStatus status)
        => _growRepository.CreateGrow(new GrowRun { TentId = _tent.Id, Name = "Lauf", StartDate = new DateTime(2026, 5, 1), Status = status });

    [Fact]
    public async Task Mit_laufendem_Grow_im_Zelt_meldet_es_wie_bisher()
    {
        var handler = Handler();
        _tent.TentType = TentType.Production;
        NeuerGrow(GrowStatus.Running);

        await AlarmeMitGrows(handler).EvaluateAsync(_tent, PhZuNiedrig);

        Assert.True(PushGesendet(handler), "Ein Zelt mit laufendem Grow muss bei einer Verletzung melden.");
    }

    [Theory]
    [InlineData(TentType.Production)]
    [InlineData(TentType.MultiPurpose)]
    public async Task Nach_dem_Abschluss_ist_das_Zelt_leer_und_schweigt(TentType zweck)
    {
        // Der Anlass (30.10.2026): nach der Ernte klingelte der Feuchte-Alarm weiter aufs Handy.
        var handler = Handler();
        _tent.TentType = zweck;
        NeuerGrow(GrowStatus.Completed);

        await AlarmeMitGrows(handler).EvaluateAsync(_tent, PhZuNiedrig);

        Assert.False(PushGesendet(handler), "Ein leeres Zelt ohne laufenden Grow hat nichts zu melden.");
    }

    [Theory]
    [InlineData(TentType.Mother)]
    [InlineData(TentType.Propagation)]
    [InlineData(TentType.Quarantine)]
    public async Task Mutter_Anzucht_und_Quarantaene_melden_auch_ohne_Grow(TentType zweck)
    {
        var handler = Handler();
        _tent.TentType = zweck;

        await AlarmeMitGrows(handler).EvaluateAsync(_tent, PhZuNiedrig);

        Assert.True(PushGesendet(handler), "Dort stehen Pflanzen ohne Grow — der Alarm gilt.");
    }

    [Fact]
    public async Task Von_Hand_ruhend_schweigt_auch_ein_Mutterzelt_aber_nicht_mit_laufendem_Grow()
    {
        var steuerung = new SteuerungRepository(_paths);
        steuerung.SetEinstellungen(ZeltRuheService.Modul, new ZeltRuheEinstellungen { Ruhend = true });
        _tent.TentType = TentType.Mother;
        var growId = NeuerGrow(GrowStatus.Completed);

        // Ruhend und kein Grow darin → still.
        var still = Handler();
        await AlarmeMitGrows(still).EvaluateAsync(_tent, PhZuNiedrig);
        Assert.False(PushGesendet(still), "Von Hand ruhend: das Zelt meldet nichts.");

        // Ein neuer Grow im Zelt: es meldet sofort wieder, auch wenn der Zustand noch „ruhend“ heißt.
        var grow = _growRepository.GetGrow(growId)!;
        grow.Status = GrowStatus.Running;
        _growRepository.UpdateGrow(grow);
        var laut = Handler();
        await AlarmeMitGrows(laut).EvaluateAsync(_tent, PhZuNiedrig);
        Assert.True(PushGesendet(laut), "Ein laufender Grow darf nie am Alarm vorbei laufen.");
    }

    private static string Push(RecordingHttpHandler handler)
        => Assert.Single(handler.Requests, r => r.Uri.AbsolutePath.Contains("/api/services/notify/", StringComparison.Ordinal)).Body!;

    [Fact]
    public async Task Grenzwert_fuehrt_auf_Live_mit_dem_Zelt_der_Meldung()
    {
        var handler = Handler();
        var alarme = new AlertEvaluationService(_rules, Benachrichtigung(handler), NullLogger<AlertEvaluationService>.Instance);

        await alarme.EvaluateAsync(_tent, new Dictionary<string, HomeAssistantState>
        {
            ["reservoir-ph"] = new HomeAssistantState { State = "5", NumericValue = 5.0 },
        });

        var body = Push(handler);
        Assert.Contains($"\"clickAction\":\"/app/{Slug}/live/{_tent.Id}\"", body);
        Assert.Contains($"\"url\":\"/app/{Slug}/live/{_tent.Id}\"", body);
    }

    [Theory]
    [InlineData(NotificationCategory.Threshold, "live")]
    [InlineData(NotificationCategory.Risk, "live")]
    [InlineData(NotificationCategory.System, "aufgaben")]
    [InlineData(NotificationCategory.Calibration, "sensoren")]
    [InlineData(NotificationCategory.Maintenance, "sensoren")]
    [InlineData(NotificationCategory.SensorOffline, "live")]
    public async Task Ohne_genaueres_Ziel_gilt_die_Seite_der_Meldungsart(NotificationCategory art, string seite)
    {
        var handler = Handler();

        Assert.True(await Benachrichtigung(handler).SendAsync(art, "t", "m"));

        Assert.Contains($"\"clickAction\":\"/app/{Slug}/{seite}\"", Push(handler));
    }

    [Fact]
    public async Task Ein_genaueres_Ziel_des_Absenders_geht_vor()
    {
        var handler = Handler();

        await Benachrichtigung(handler).SendAsync(NotificationCategory.System, "t", "m", seite: "dosierung");

        Assert.Contains($"\"clickAction\":\"/app/{Slug}/dosierung\"", Push(handler));
    }

    [Fact]
    public async Task Testmeldung_und_Tagesbericht_nennen_Live_ausdruecklich()
    {
        // Ein leerer Rest („/app/<slug>") kommt in der App als route.path "" an —
        // genau wie bei jeder Größenänderung. Die App bliebe dann auf der Seite,
        // die gerade offen ist.
        Assert.Equal($"/app/{Slug}/live", await Benachrichtigung(Handler()).StartPfadAsync(default));
    }

    /// <summary>
    /// Zählung: jede Meldungsart zeigt auf eine Seite, die es in der App gibt —
    /// gelesen aus den Routen in <c>App.tsx</c>, nicht aus einer Liste hier.
    /// </summary>
    [Fact]
    public void Jedes_Ziel_ist_eine_Route_der_App()
    {
        var app = File.ReadAllText(Path.Combine(ProjektWurzel(), "GrowDiary.React", "src", "App.tsx"));
        var routen = Regex.Matches(app, "<Route path=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(routen.Count >= 30, $"Nur {routen.Count} Routen gelesen — sieht die Zählung App.tsx überhaupt?");

        var arten = Enum.GetValues<NotificationCategory>();
        Assert.True(arten.Length >= 6);
        foreach (var art in arten)
        {
            Assert.Contains("/" + NotificationService.SeiteFuer(art), routen);
        }

        Assert.Contains("/live/:zeltId", routen);
        Assert.Equal("live/7", NotificationService.LiveSeite(7));
        Assert.Contains("/dosierung", routen);
    }

    private static string ProjektWurzel()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }
}
