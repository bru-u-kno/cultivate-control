using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Entfeuchter-Instanzen (Etappe 2, Release 6b): Rechenwerte zeigen auf das CO₂ des eigenen Zeltes, der Wochenplan führt die
/// VPD-Helfer jeder Instanz in deren Zelt, „Zelt ruhend“ kennt jede Instanz. Die erste Instanz bleibt, wie sie war.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class EntfeuchterInstanzPlanTests : IDisposable
{
    private readonly Co2Zelte.Stand _co2Vorher = Co2Zelte.Aktuell;
    private readonly string _wurzel;
    private readonly AppPaths _pfade;

    public EntfeuchterInstanzPlanTests()
    {
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "entfeuchter", Nummer = 2, Name = "Zelt B" }]);
        _wurzel = Path.Combine(Path.GetTempPath(), "EntfeuchterInstanzPlan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        _pfade = new AppPaths(_wurzel);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        SteuerungInstanzen.ZelteFestlegen(new Dictionary<string, int?>());
        Co2Zelte.Festlegen(_co2Vorher.BestandZeltId, _co2Vorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private static string Vorlage(string kennung)
        => SteuerungBauteile.FuerModul("entfeuchter-i2").Single(b => b.EntityId == kennung).Vorlage!;

    // ------------------------------------------------------------------ Katalog: CO₂ des eigenen Zeltes

    [Fact]
    public void DieRechenwerteZeigenAufDasCo2DesEigenenZeltes()
    {
        Co2Zelte.Festlegen(1, [1, 2]);
        SteuerungInstanzen.ZelteFestlegen(new Dictionary<string, int?> { ["entfeuchter-i2"] = 2 });

        var aus = Vorlage("sensor.fork_entfeuchter_feuchte_aus_aktiv_i2");

        Assert.Contains("input_number.co2_z2_rh_obergrenze", aus);
        Assert.Contains("input_number.co2_z2_klima_hysterese", aus);
        Assert.DoesNotContain("input_number.co2_rh_obergrenze", aus);
        Assert.DoesNotContain("input_number.co2_klima_hysterese", aus);
        // Die eigenen Helfer tragen weiter die Endung der Instanz, nicht die des Zeltes.
        Assert.Contains("input_number.fork_entfeuchter_hysterese_i2", aus);
    }

    [Fact]
    public void ImZeltDesBestandCo2BleibenDieKennungenDesBestands()
    {
        Co2Zelte.Festlegen(1, [1, 2]);
        SteuerungInstanzen.ZelteFestlegen(new Dictionary<string, int?> { ["entfeuchter-i2"] = 1 });

        var aus = Vorlage("sensor.fork_entfeuchter_feuchte_aus_aktiv_i2");

        Assert.Contains("input_number.co2_rh_obergrenze", aus);
        Assert.DoesNotContain("co2_z", aus);
    }

    [Fact]
    public void DerKatalogFolgtDemZeltNachEinemWechsel()
    {
        Co2Zelte.Festlegen(1, [1, 2, 3]);
        SteuerungInstanzen.ZelteFestlegen(new Dictionary<string, int?> { ["entfeuchter-i2"] = 2 });
        Assert.Contains("co2_z2_rh_obergrenze", Vorlage("sensor.fork_entfeuchter_feuchte_aus_aktiv_i2"));

        SteuerungInstanzen.ZelteFestlegen(new Dictionary<string, int?> { ["entfeuchter-i2"] = 3 });
        Assert.Contains("co2_z3_rh_obergrenze", Vorlage("sensor.fork_entfeuchter_feuchte_aus_aktiv_i2"));
    }

    [Fact]
    public void DieZuweisungMeldetDasZeltDerInstanzAnsRegister()
    {
        TestDatabase.InitializeWithDefaultTent(_pfade);
        var zelte = new TentRepository(_pfade);
        var zelt2 = zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        var dienst = new SteuerungZeltService(new SteuerungRepository(_pfade), zelte, new GrowRepository(_pfade));

        Assert.Null(dienst.Speichern(new Dictionary<string, int> { ["entfeuchter-i2"] = zelt2 }));

        Assert.Equal(zelt2, SteuerungInstanzen.ZeltFuer("entfeuchter-i2"));
        Assert.Null(SteuerungInstanzen.ZeltFuer("entfeuchter-i9"));
    }

    // ------------------------------------------------------------------ Zelt ruhend

    [Fact]
    public void ZeltRuhendKenntJedenWeiterenEntfeuchterOhneVorauswahl()
    {
        var art = ZeltRuheArten.Finden("entfeuchter-i2");

        Assert.NotNull(art);
        Assert.Equal("Entfeuchter Zelt B", art!.Titel);
        Assert.Equal("entfeuchter-i2", art.Modul);
        Assert.Equal("port_schalter", art.Rolle);
        Assert.False(art.Standard);                       // wie der erste: läuft weiter, bis jemand es ankreuzt
        Assert.True(ZeltRuheArten.IstEntfeuchterInstanz("entfeuchter-i2"));
        Assert.False(ZeltRuheArten.IstEntfeuchterInstanz("entfeuchter"));
        Assert.False(ZeltRuheArten.IstEntfeuchterInstanz("entfeuchter-zusatz-i2"));
        Assert.DoesNotContain("entfeuchter-i2", ZeltRuheArten.Standard(["entfeuchter", "entfeuchter-i2"]));
        Assert.Equal(7, ZeltRuheArten.Alle.Count);
    }

    // ------------------------------------------------------------------ Wochenplan

    [Fact]
    public void DerWochenplanFuehrtDieVpdHelferDerInstanzInIhremZelt()
    {
        TestDatabase.InitializeWithDefaultTent(_pfade);
        var quelle = Path.Combine(ProjektWurzel(), "GrowDiary.Web", "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(_wurzel, "wwwroot", "knowledge-defaults", Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }

        var zelte = new TentRepository(_pfade);
        var zelt1 = zelte.GetTents().First().Id;
        var zelt2 = zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        grows.CreateGrow(new GrowRun
        {
            Name = "Grow A", TentId = zelt1, StartDate = DateTime.Today.AddDays(-75), FlipDate = DateTime.Today.AddDays(-20), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        });
        grows.CreateGrow(new GrowRun
        {
            Name = "Grow B", TentId = zelt2, StartDate = DateTime.Today.AddDays(-5), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        });
        var steuerung = new SteuerungRepository(_pfade);
        var zuweisung = new SteuerungZeltService(steuerung, zelte, grows);
        var ha = new HomeAssistantService(new StubHttpClientFactory(new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"))), NullLogger<HomeAssistantService>.Instance);
        var plan = new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha,
            new HomeAssistantSettingsRepository(_pfade), NullLogger<WochenplanSyncService>.Instance, zuweisung);

        Assert.Null(zuweisung.Speichern(new Dictionary<string, int> { ["entfeuchter"] = zelt1, ["entfeuchter-i2"] = zelt2 }));

        var ersteHelfer = plan.GefuehrteHelfer();
        var zweiteHelfer = plan.GefuehrteEntfeuchterHelfer("entfeuchter-i2");
        var planB = plan.PlanWerte(zelt2);
        var planA = plan.PlanWerte(zelt1);

        Assert.Contains("input_number.vpd_ziel_unten_i2", zweiteHelfer.Keys);
        Assert.Contains("input_number.vpd_ziel_abschaltung_i2", zweiteHelfer.Keys);
        Assert.Equal(planB[WochenplanSyncService.Rollen.VpdUnten], zweiteHelfer["input_number.vpd_ziel_unten_i2"]);
        Assert.Equal(planA[WochenplanSyncService.Rollen.VpdUnten], ersteHelfer["input_number.vpd_ziel_unten"]);
        Assert.NotEqual(planA[WochenplanSyncService.Rollen.VpdUnten], planB[WochenplanSyncService.Rollen.VpdUnten]); // sonst belegt der Vergleich nichts
        // Jede Seite führt nur ihre eigenen Helfer.
        Assert.DoesNotContain("input_number.vpd_ziel_unten_i2", ersteHelfer.Keys);
        Assert.DoesNotContain("input_number.vpd_ziel_unten", zweiteHelfer.Keys);
        // Die Feuchte-Obergrenze und die Zelt-Regeln gehören nicht der Instanz.
        Assert.All(zweiteHelfer.Keys, k => Assert.EndsWith("_i2", k));
        Assert.DoesNotContain(zweiteHelfer.Keys, k => k.Contains("co2", StringComparison.Ordinal));

        Assert.Empty(plan.GefuehrteEntfeuchterHelfer("entfeuchter"));
        Assert.Empty(plan.GefuehrteEntfeuchterHelfer("chiller-i2"));
    }

    private static string ProjektWurzel()
    {
        var ordner = new DirectoryInfo(AppContext.BaseDirectory);
        while (ordner is not null && !Directory.Exists(Path.Combine(ordner.FullName, "GrowDiary.Web", "wwwroot"))) ordner = ordner.Parent;
        return ordner?.FullName ?? throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }
}
