using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Wochenplan je Zelt (Etappe 2, Release 1): Laufen in zwei Zelten zwei Wochenziel-Durchgänge, schweigt der Plan nicht mehr für alles —
/// jede Steuerung folgt dem Durchgang in dem Zelt, dem sie zugewiesen ist.
/// </summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class WochenplanJeZeltTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;
    private readonly TentRepository _zelte;
    private readonly Co2Zelte.Stand _registerVorher = Co2Zelte.Aktuell;
    private readonly int _zelt1;
    private readonly int _zelt2;

    public WochenplanJeZeltTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "WochenplanJeZelt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var quelle = Path.Combine(ProjektWurzel(), "GrowDiary.Web", "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(_wurzel, "wwwroot", "knowledge-defaults", Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
        _pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        _zelte = new TentRepository(_pfade);
        var z1 = _zelte.GetTents().First();
        z1.Name = "Zelt A";
        _zelte.UpdateTent(z1);
        _zelt1 = z1.Id;
        _zelt2 = _zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        Co2Zelte.Festlegen(_zelt1, [_zelt1]);
    }

    public void Dispose()
    {
        Co2Zelte.Festlegen(_registerVorher.BestandZeltId, _registerVorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private static string ProjektWurzel()
    {
        var ordner = new DirectoryInfo(AppContext.BaseDirectory);
        while (ordner is not null && !Directory.Exists(Path.Combine(ordner.FullName, "GrowDiary.Web", "wwwroot"))) ordner = ordner.Parent;
        return ordner?.FullName ?? throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }

    private (WochenplanSyncService Plan, SteuerungZeltService Zuweisung, GrowRun A, GrowRun B, KnowledgeBaseLoader Wissen) Aufbau(bool mitZuweisung)
    {
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        var a = grows.GetGrow(grows.CreateGrow(new GrowRun
        {
            Name = "Grow A", TentId = _zelt1, StartDate = DateTime.Today.AddDays(-75), FlipDate = DateTime.Today.AddDays(-20), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        }))!;
        var b = grows.GetGrow(grows.CreateGrow(new GrowRun
        {
            Name = "Grow B", TentId = _zelt2, StartDate = DateTime.Today.AddDays(-5), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        }))!;
        var steuerung = new SteuerungRepository(_pfade);
        var zuweisung = new SteuerungZeltService(steuerung, _zelte, grows);
        var ha = new HomeAssistantService(new StubHttpClientFactory(new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"))), NullLogger<HomeAssistantService>.Instance);
        var plan = new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha,
            new HomeAssistantSettingsRepository(_pfade), NullLogger<WochenplanSyncService>.Instance, mitZuweisung ? zuweisung : null);
        return (plan, zuweisung, a, b, wissen);
    }

    [Fact]
    public void OhneZuweisung_SchweigtDerPlanBeiZweiDurchgaengen_WieBisher()
    {
        var (plan, _, _, _, _) = Aufbau(mitZuweisung: false);
        Assert.Empty(plan.GefuehrteHelfer());
        Assert.Null(plan.PlanLuft());
        Assert.Null(plan.PlanFeuchte());
    }

    [Fact]
    public void JedeSteuerungFolgtDemDurchgangInIhremZelt()
    {
        var (plan, zuweisung, a, b, wissen) = Aufbau(mitZuweisung: true);
        // Entfeuchter in Zelt A, Wasserkühler in Zelt B, CO₂-Bestand in Zelt A (Register).
        Assert.Null(zuweisung.Speichern(new Dictionary<string, int> { ["entfeuchter"] = _zelt1, ["chiller"] = _zelt2 }));
        var spalteA = MischplanService.ZielSpalteFuerGrow(a, wissen.NutrientPrograms)!.Value.Spalte;
        var spalteB = MischplanService.ZielSpalteFuerGrow(b, wissen.NutrientPrograms)!.Value.Spalte;

        var gefuehrt = plan.GefuehrteHelfer();

        Assert.Equal(spalteB.WaterTempDayC!.Value, gefuehrt["input_number.chiller_zieltemperatur_tag"]);
        Assert.Equal(spalteA.RhMax!.Value, gefuehrt[EntfeuchterSteuerungService.Entitaeten.RhObergrenze]);
        Assert.Equal(spalteA.VpdMin!.Value, gefuehrt[EntfeuchterSteuerungService.Entitaeten.VpdUnten]);
        Assert.NotNull(plan.PlanLuft());
        Assert.Equal(spalteA.AirTempC!.Value, plan.PlanLuft()!.Value.LuftTagC);
        Assert.Equal(spalteA.RhMax!.Value, plan.PlanFeuchte()!.Value.RhTag);
        Assert.Equal(spalteA.AirTempC!.Value, plan.PlanWerte()[WochenplanSyncService.Rollen.LuftOben] - WochenplanSyncService.LufttemperaturSpanne);
    }

    [Fact]
    public void ZweiDurchgaengeImSelbenZelt_LegenNurDiesesZeltStill()
    {
        var (plan, zuweisung, _, _, wissen) = Aufbau(mitZuweisung: true);
        var grows = new GrowRepository(_pfade);
        var c = grows.GetGrow(grows.CreateGrow(new GrowRun
        {
            Name = "Grow C", TentId = _zelt2, StartDate = DateTime.Today.AddDays(-3), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        }))!;
        Assert.NotNull(c);
        Assert.Null(zuweisung.Speichern(new Dictionary<string, int> { ["entfeuchter"] = _zelt1, ["chiller"] = _zelt2 }));

        var gefuehrt = plan.GefuehrteHelfer();

        Assert.False(gefuehrt.ContainsKey("input_number.chiller_zieltemperatur_tag")); // Zelt B: zwei Durchgänge
        Assert.True(gefuehrt.ContainsKey(EntfeuchterSteuerungService.Entitaeten.VpdUnten)); // Zelt A: einer
        Assert.NotNull(wissen);
    }
}
