using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Tests.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Services.Knowledge.Schema;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Der eigene Trocknungsplan des Trockenzelts (A-029): Er gehört dem Zelt, nicht dem Düngeprogramm — Speichern, Wiederfinden,
/// Prüfungen, einmalige Übernahme alter Etappen am Programm und die Kacheln, die ihm folgen.
/// </summary>
public sealed class TrocknungPlanApiTests : IDisposable
{
    private readonly string _wurzel;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPlanRepository _plaene;
    private readonly TrocknungPlanQuelle _quelle;
    private readonly KnowledgeBaseLoader _wissen;
    private readonly Tent _trockenZelt;
    private readonly int _growId;
    private readonly AppPaths _paths;

    public TrocknungPlanApiTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrocknungPlan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        _paths = new AppPaths(_wurzel);
        var bluete = TestDatabase.InitializeWithDefaultTent(_paths);
        _grows = new GrowRepository(_paths);
        _ernten = new HarvestRepository(_paths);
        _plaene = new TrocknungPlanRepository(_paths);
        KopiereWissen(_wurzel);
        _wissen = new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance);
        _wissen.Initialize();
        _quelle = new TrocknungPlanQuelle(_wissen, _plaene);
        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
        _growId = _grows.CreateGrow(new GrowRun { Name = "Charge A", TentId = bluete.Id, StartDate = DateTime.Today.AddDays(-90), Status = GrowStatus.Running });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private TrocknungPlanApiController Controller() => new(_grows, _ernten, _quelle);

    private static T Wert<T>(ActionResult<T> ergebnis) where T : class
        => (ergebnis.Result as ObjectResult)?.Value as T ?? ergebnis.Value ?? throw new InvalidOperationException("Keine Antwort: " + ergebnis.Result);

    private void Ernte(int tageHer) => _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-tageHer), DryingTentId = _trockenZelt.Id });

    private static TrockenEtappe E(string name, int tage, double temp, double ziel, double max)
        => new() { Name = name, DauerTage = tage, LufttemperaturC = temp, LuftfeuchteZiel = ziel, LuftfeuchteMax = max };

    [Fact]
    public void Ohne_eigenen_Plan_gilt_der_Standard_und_ein_unbekanntes_Zelt_ist_404()
    {
        var plan = Wert(Controller().Lesen(_trockenZelt.Id));
        Assert.True(plan.IstStandard);
        Assert.Equal(3, plan.Etappen.Count);
        Assert.Equal("Anfang, langsam", plan.Etappen[0].Name);
        Assert.Null(plan.Laeuft);
        Assert.IsType<NotFoundObjectResult>(Controller().Lesen(99999).Result);
    }

    [Fact]
    public void Speichern_ersetzt_den_Plan_als_Ganzes_und_er_ist_danach_wiederzufinden()
    {
        var c = Controller();
        var gespeichert = Wert(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Kalt", 2, 17.5, 55, 60), E("Zusatz", 3, 16, 50, 55)] }));
        Assert.False(gespeichert.IstStandard);
        Assert.Equal(2, gespeichert.Etappen.Count);

        // Neuer Controller, neue Quelle: kommt aus der Datenbank, nicht aus dem Gedächtnis.
        var wieder = Wert(new TrocknungPlanApiController(_grows, _ernten, new TrocknungPlanQuelle(_wissen, new TrocknungPlanRepository(_paths))).Lesen(_trockenZelt.Id));
        Assert.Equal(["Kalt", "Zusatz"], wieder.Etappen.Select(e => e.Name));
        Assert.Equal(17.5, wieder.Etappen[0].LufttemperaturC);

        // Zweites Speichern: wieder der ganze Plan, nicht eine Mischung.
        var zweites = Wert(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Nur eine", 4, 18, 55, 60)] }));
        Assert.Single(zweites.Etappen);
    }

    [Fact]
    public void Falsche_Angaben_werden_abgewiesen_und_der_bisherige_Plan_bleibt()
    {
        var c = Controller();
        Wert(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Gut", 3, 18, 55, 60)] }));

        Assert.IsType<BadRequestObjectResult>(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [] }).Result);
        Assert.IsType<BadRequestObjectResult>(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("", 3, 18, 55, 60)] }).Result);
        Assert.IsType<BadRequestObjectResult>(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Zu lang", 30, 18, 55, 60)] }).Result);
        Assert.IsType<BadRequestObjectResult>(c.Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Obergrenze zu tief", 3, 18, 60, 55)] }).Result);

        Assert.Equal("Gut", Wert(c.Lesen(_trockenZelt.Id)).Etappen.Single().Name);
        var gewoehnlich = _grows.CreateTent(new Tent { Name = "Gewöhnlich", TentType = TentType.Production });
        Assert.IsType<BadRequestObjectResult>(c.Speichern(gewoehnlich.Id, new TrocknungPlanRequest { Etappen = [E("X", 3, 18, 55, 60)] }).Result);
        Assert.IsType<NotFoundObjectResult>(c.Speichern(99999, new TrocknungPlanRequest { Etappen = [E("X", 3, 18, 55, 60)] }).Result);
    }

    [Fact]
    public void Der_Plan_gehoert_dem_Zelt_und_folgt_keinem_Wechsel_des_Duengeprogramms()
    {
        Ernte(1); // Tag 2
        Wert(Controller().Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Mein Plan", 5, 16, 50, 55)] }));

        var grow = _grows.GetGrow(_growId)!;
        foreach (var programm in new[] { "skx-canna-aqua", "athena", null })
        {
            grow.FeedProgramId = programm;
            _grows.UpdateGrow(grow);
            var plan = Wert(Controller().Lesen(_trockenZelt.Id));
            Assert.Equal("Mein Plan", plan.Etappen.Single().Name);
            Assert.Equal("Mein Plan", plan.Laeuft!.EtappeName);
        }
    }

    [Fact]
    public void Eigene_Etappen_am_Programm_werden_einmalig_uebernommen_danach_gilt_der_Plan_des_Zelts()
    {
        Ernte(1);
        var eigene = new EigeneProgramme(_paths, _wissen, NullLogger<EigeneProgramme>.Instance);
        var kopie = eigene.Anlegen(eigene.Finden("skx-canna-aqua")!, "Mit Trocknung");
        kopie.Trocknung = [E("Altbestand", 4, 19, 58, 62)];
        eigene.Speichern(kopie);
        var grow = _grows.GetGrow(_growId)!;
        grow.FeedProgramId = kopie.Id;
        _grows.UpdateGrow(grow);

        Assert.Equal("Altbestand", Wert(Controller().Lesen(_trockenZelt.Id)).Etappen.Single().Name);
        Assert.NotNull(_plaene.Get(_trockenZelt.Id)); // übernommen, jetzt gespeichert

        // Ändert sich das Programm danach, bleibt der Plan des Zelts.
        kopie.Trocknung = [E("Spaeter am Programm", 2, 15, 45, 50)];
        eigene.Speichern(kopie);
        Assert.Equal("Altbestand", Wert(Controller().Lesen(_trockenZelt.Id)).Etappen.Single().Name);
    }

    [Fact]
    public void Die_Kacheln_des_Trockenzelts_folgen_dem_Plan_des_Zelts()
    {
        Ernte(0); // Tag 1
        Wert(Controller().Speichern(_trockenZelt.Id, new TrocknungPlanRequest { Etappen = [E("Eigener Anfang", 3, 16, 50, 55)] }));
        var composer = new GrowDashboardComposer(
            TestKnowledgeBase.TargetValues(), NullLogger<GrowDashboardComposer>.Instance,
            grows: _grows, harvests: _ernten, knowledge: _wissen, trocknungsplan: _quelle);
        var karten = composer.BuildTentMetrics(_trockenZelt, new Dictionary<string, HomeAssistantState>(),
            [new Measurement { Id = 1, TakenAt = DateTime.Now.AddMinutes(-5), AirTemperatureC = 18, HumidityPercent = 57 }]);

        var temperatur = Assert.Single(karten, k => k.Key == "temperature");
        Assert.Equal(15, temperatur.TargetMin);
        Assert.Equal(17, temperatur.TargetMax);
        Assert.Equal("Trocknung · Tag 1 · Eigener Anfang", temperatur.TargetNote);
    }

    private static void KopiereWissen(string wurzel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) dir = Path.GetDirectoryName(dir);
        var quelle = Path.Combine(dir ?? throw new InvalidOperationException("Projektwurzel nicht gefunden."),
            "GrowDiary.Web", "wwwroot", "knowledge-defaults");
        var ziel = Path.Combine(wurzel, "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(ziel, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
    }
}
