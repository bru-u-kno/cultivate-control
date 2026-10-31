using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Start-Meldungen: Update mit Versionsnummer, Neustart ohne, Home Assistant wieder da —
/// und die Tages-Merker, die einen Neustart überleben müssen.
/// </summary>
public sealed class StartMeldungTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"grow-test-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly NotificationSettingsRepository _settingsRepo;
    private readonly GrowRepository _growRepository;
    private readonly string? _savedSupervisorToken;

    public StartMeldungTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _paths = new AppPaths(_contentRoot);
        TestDatabase.Initialize(_paths);
        _settingsRepo = new NotificationSettingsRepository(_paths);
        _growRepository = new GrowRepository(_paths);
        _savedSupervisorToken = Environment.GetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable);
        Environment.SetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable, null);
        _growRepository.SaveHomeAssistantSettings(new HomeAssistantSettings
        {
            BaseUrl = "http://ha.local:8123",
            AccessToken = "token",
            Enabled = true,
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeAssistantAddon.SupervisorTokenEnvironmentVariable, _savedSupervisorToken);
        try { Directory.Delete(_contentRoot, recursive: true); } catch { }
    }

    [Fact]
    public void NeueVersion_MeldetUpdate_MitVersionsnummer()
    {
        var (titel, text) = StartMeldung.Start("2.0.0-cc.287", "2.0.0-cc.288");

        Assert.Contains("Update", titel);
        Assert.Equal("Cultivate Control wurde auf Version 2.0.0-cc.288 aktualisiert.", text);
    }

    [Fact]
    public void ErsterStartOhneBekannteVersion_MeldetUpdate()
    {
        var (_, text) = StartMeldung.Start(null, "2.0.0-cc.288");

        Assert.Contains("2.0.0-cc.288", text);
    }

    [Theory]
    [InlineData("2.0.0-cc.288", "2.0.0-cc.288")]
    [InlineData("2.0.0-cc.288", " 2.0.0-cc.288 ")]
    [InlineData("2.0.0-cc.288", null)]
    [InlineData(null, null)]
    [InlineData("2.0.0-cc.288", "")]
    public void GleicheOderUnbekannteVersion_MeldetNeustart_OhneVersionsnummer(string? bisher, string? jetzt)
    {
        var (titel, text) = StartMeldung.Start(bisher, jetzt);

        Assert.Contains("Neustart", titel);
        Assert.Equal("Cultivate Control wurde neu gestartet.", text);
        Assert.DoesNotContain("cc.", text);
        Assert.DoesNotContain("Version", text);
    }

    [Fact]
    public void HomeAssistantZurueck_NennetDieDauer_UndKennt_KeinenWackler()
    {
        Assert.Null(StartMeldung.HomeAssistantZurueck(TimeSpan.FromSeconds(60)));
        Assert.Null(StartMeldung.HomeAssistantZurueck(TimeSpan.FromSeconds(89)));

        var text = StartMeldung.HomeAssistantZurueck(TimeSpan.FromMinutes(4.2))!.Value.Text;
        Assert.Equal("Home Assistant ist wieder erreichbar (nach 4 Min.).", text);

        // Mindestens eine Minute, nie „0 Min.“.
        Assert.Contains("(nach 2 Min.)", StartMeldung.HomeAssistantZurueck(TimeSpan.FromSeconds(95))!.Value.Text);
    }

    [Fact]
    public void Luecke_WirdGenauEinmalGemeldet()
    {
        var heartbeat = new SystemHeartbeat();
        var t0 = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

        Assert.Null(heartbeat.MarkHaReachable(t0));

        heartbeat.MarkHaUnreachable(t0);
        heartbeat.MarkHaUnreachable(t0.AddMinutes(1)); // verschiebt den Beginn nicht
        var luecke = heartbeat.MarkHaReachable(t0.AddMinutes(3));

        Assert.Equal(TimeSpan.FromMinutes(3), luecke);
        Assert.Null(heartbeat.MarkHaReachable(t0.AddMinutes(4)));
    }

    [Fact]
    public void Schalter_Standard_An_UndUeberlebtDieDatenbank()
    {
        Assert.True(_settingsRepo.GetNotificationSettings().StartMessages);

        _settingsRepo.SaveNotificationSettings(new NotificationSettings { NotifyService = "notify.x", StartMessages = false });

        Assert.False(_settingsRepo.GetNotificationSettings().StartMessages);
        Assert.False(_settingsRepo.GetNotificationSettings().IsCategoryEnabled(NotificationCategory.Start));
    }

    [Fact]
    public async Task Schalter_Aus_SendetNichts_Und_An_SendetEinen_Push()
    {
        var handler = new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"));
        var dienst = new NotificationService(
            _settingsRepo, _growRepository,
            new HomeAssistantService(new StubHttpClientFactory(handler), NullLogger<HomeAssistantService>.Instance),
            NullLogger<NotificationService>.Instance);

        _settingsRepo.SaveNotificationSettings(new NotificationSettings { NotifyService = "notify.x", StartMessages = false });
        Assert.False(await dienst.SendAsync(NotificationCategory.Start, "t", "m"));
        Assert.Empty(handler.Requests);

        _settingsRepo.SaveNotificationSettings(new NotificationSettings { NotifyService = "notify.x", StartMessages = true });
        Assert.True(await dienst.SendAsync(NotificationCategory.Start, "t", "m"));
        Assert.EndsWith("/api/services/notify/x", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Theory]
    [InlineData("2026-10-31", 2026, 10, 31)]
    public void TagesMerker_WirdAusDerDatenbankGelesen(string wert, int jahr, int monat, int tag)
        => Assert.Equal(new DateOnly(jahr, monat, tag), HomeAssistantSnapshotWorker.MerkerLesen(wert));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gestern")]
    public void TagesMerker_Unlesbar_GiltAlsNichtGemeldet(string? wert)
        => Assert.Null(HomeAssistantSnapshotWorker.MerkerLesen(wert));
}
