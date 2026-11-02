using System.Net;
using System.Text.Json;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>Das Register <see cref="Co2Zelte"/> ist prozessweit: Tests, die es setzen, laufen nacheinander.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Co2ZelteRegisterCollection
{
    public const string Name = "Co2ZelteRegister";
}

/// <summary>
/// Die CO₂-Steuerung je Zelt: jedes Zelt hat eigene Einstellungen, eigene Helfer in Home Assistant, eigene Live-Werte und eigene Tage.
/// Das Bestand-Zelt (Modul „co2“) bleibt unverändert.
/// </summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class Co2JeZeltDienstTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;
    private readonly RecordingHttpHandler _ha;
    private readonly TentRepository _zelte;
    private readonly Co2Zelte.Stand _registerVorher = Co2Zelte.Aktuell;
    private string _zustaende = "[]";
    private int _zelt1;
    private int _zelt2;

    public Co2JeZeltDienstTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "Co2JeZelt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        KopiereWissen(Path.Combine(ProjektWurzel(), "GrowDiary.Web", "wwwroot", "knowledge-defaults"), _wurzel);
        _pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        new HomeAssistantSettingsRepository(_pfade).SaveHomeAssistantSettings(new HomeAssistantSettings
        {
            BaseUrl = "http://ha.local:8123", AccessToken = "token", Enabled = true,
        });
        _ha = new RecordingHttpHandler((anfrage, _) => anfrage.Method == HttpMethod.Get
            ? RecordingHttpHandler.Json(anfrage.RequestUri!.AbsolutePath.EndsWith("/api/states") ? _zustaende : "{}",
                anfrage.RequestUri!.AbsolutePath.EndsWith("/api/states") ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            : RecordingHttpHandler.Json("[]"));
        _zelte = new TentRepository(_pfade);

        var z1 = _zelte.GetTents().First();
        z1.Name = "Zelt A"; z1.HasCo2Enrichment = true; z1.WidthCm = 240; z1.DepthCm = 120; z1.TentHeightCm = 200;
        _zelte.UpdateTent(z1);
        _zelt1 = z1.Id;
        _zelt2 = _zelte.CreateTent(new Tent { Name = "Zelt B", HasCo2Enrichment = true, WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        Co2Zelte.Festlegen(_zelt1, [_zelt1, _zelt2]);
    }

    public void Dispose()
    {
        Co2Zelte.Festlegen(_registerVorher.BestandZeltId, _registerVorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private Co2SteuerungService Bestand()
    {
        var ha = new HomeAssistantService(new StubHttpClientFactory(_ha), NullLogger<HomeAssistantService>.Instance);
        var haSettings = new HomeAssistantSettingsRepository(_pfade);
        var steuerung = new SteuerungRepository(_pfade);
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        return new Co2SteuerungService(
            steuerung, new KostenRepository(_pfade), new JournalRepository(_pfade), grows,
            new HydroSetupRepository(_pfade, _zelte), _zelte, new TargetValueService(wissen), wissen,
            ha, haSettings, new SteuerungGeraeteService(steuerung),
            new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha, haSettings,
                NullLogger<WochenplanSyncService>.Instance),
            new SteuerungMittelwertService(ha, haSettings, NullLogger<SteuerungMittelwertService>.Instance),
            NullLogger<Co2SteuerungService>.Instance);
    }

    private IEnumerable<string> GeschriebeneEntitaeten()
        => _ha.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.StartsWith("/api/services/", StringComparison.Ordinal))
            .Select(r => JsonDocument.Parse(r.Body!).RootElement.GetProperty("entity_id").GetString()!);

    private static string Zustand(string id, string wert) => $$"""{"entity_id":"{{id}}","state":"{{wert}}","attributes":{},"last_changed":"2026-11-02T10:00:00+00:00"}""";

    [Fact]
    public void FuerDasBestandZelt_GiltDerDienstSelbst_FuerEinAnderesZeltEineKopieMitEigenemModul()
    {
        var bestand = Bestand();
        Assert.Same(bestand, bestand.Fuer(_zelt1));
        Assert.Null(bestand.ZeltId);
        Assert.Equal("co2", bestand.ModulName);

        var zelt = bestand.Fuer(_zelt2);
        Assert.NotSame(bestand, zelt);
        Assert.Equal(_zelt2, zelt.ZeltId);
        Assert.Equal($"co2-z{_zelt2}", zelt.ModulName);
        Assert.Null(zelt.Bestand().ZeltId);               // zurück zum Bestand: wieder Modul „co2“, ohne Zelt
        Assert.Equal("co2", zelt.Bestand().ModulName);
    }

    [Fact]
    public void Einstellungen_SindJeZeltGetrennt()
    {
        var bestand = Bestand();
        var zelt = bestand.Fuer(_zelt2);
        var repo = new SteuerungRepository(_pfade);
        repo.SetEinstellungen("co2", new Co2Einstellungen { ZielWarmPpm = 900 });
        repo.SetEinstellungen(zelt.ModulName, new Co2Einstellungen { ZielWarmPpm = 1100 });

        Assert.Equal(900, bestand.Einstellungen.ZielWarmPpm);
        Assert.Equal(1100, zelt.Einstellungen.ZielWarmPpm);
    }

    [Fact]
    public void Zeltvolumen_KommtVomEigenenZelt_AuchMitZweiCo2Zelten()
    {
        var bestand = Bestand();
        Assert.Equal(5.76, bestand.Einstellungen.ZeltvolumenM3);                 // Zelt A: 240×120×200
        Assert.True(bestand.WirksamesZeltvolumen(1).Quelle.AusMassen);
        Assert.Equal(2.88, bestand.Fuer(_zelt2).Einstellungen.ZeltvolumenM3);    // Zelt B: 120×120×200
        Assert.Equal("Zelt B", bestand.Fuer(_zelt2).WirksamesZeltvolumen(1).Quelle.ZeltName);
    }

    [Fact]
    public void Schreibliste_BenenntDieHelferDesZeltes_BestandBleibtBeiSeinenKennungen()
    {
        var e = new Co2Einstellungen();
        var bestand = Co2SteuerungService.Schreibliste(e, 900, 800, 650, []);
        var zelt = Co2SteuerungService.Schreibliste(e, 900, 800, 650, [], zeltId: 2);

        Assert.NotEmpty(bestand);
        Assert.Equal(bestand.Count, zelt.Count);
        Assert.All(bestand, z => Assert.DoesNotMatch(@"co2_z\d+_", z.Entity));
        Assert.All(zelt, z => Assert.StartsWith("input_number.co2_z2_", z.Entity));
        Assert.Equal(bestand.Select(z => z.Wert), zelt.Select(z => z.Wert));
    }

    [Fact]
    public void Schalterliste_UndAutomatik_TragenDieZeltKennung()
    {
        var e = new Co2Einstellungen { AutomatikAktiv = true };
        var liste = Co2SteuerungService.Schalterliste(e, mitAutomatik: true, zeltId: 3);
        Assert.Contains(liste, s => s.Entity == "input_boolean.co2_z3_autokalibrierung");
        Assert.Contains(liste, s => s.Entity == "input_boolean.co2_z3_abluft_drosseln");
        Assert.Contains(liste, s => s.Domain == "automation" && s.Entity == "automation.co2_z3_dosierung");
    }

    [Fact]
    public async Task SpeichernFuerEinZelt_SchreibtNurSeineHelfer_NieDieDesBestands()
    {
        var zelt = Bestand().Fuer(_zelt2);

        var (gespeichert, fehler, _) = await zelt.SpeichernAsync(new Co2Einstellungen { AutomatikAktiv = false }, CancellationToken.None);

        Assert.NotNull(gespeichert);
        Assert.Empty(fehler);
        var ids = GeschriebeneEntitaeten().ToList();
        Assert.NotEmpty(ids); // Selbsttest: es wurde überhaupt geschrieben
        Assert.All(ids, id => Assert.Contains("co2_z" + _zelt2 + "_", id));
        // gespeichert unter dem Zelt-Modul, das Bestand-Modul bleibt leer
        var repo = new SteuerungRepository(_pfade);
        Assert.NotNull(repo.GetEinstellungen<Co2Einstellungen>(zelt.ModulName));
        Assert.Null(repo.GetEinstellungen<Co2Einstellungen>("co2"));
    }

    [Fact]
    public async Task Live_LiestJeZeltDieEigenenHelferUndRollen()
    {
        var bestand = Bestand();
        var zelt = bestand.Fuer(_zelt2);
        var repo = new SteuerungRepository(_pfade);
        repo.SetGeraet("co2", "co2_sensor", "sensor.a_co2");
        repo.SetGeraet(zelt.ModulName, "co2_sensor", "sensor.b_co2");
        _zustaende = "[" + string.Join(",", new[]
        {
            Zustand("sensor.a_co2", "700"), Zustand("sensor.b_co2", "1111"),
            Zustand("input_number.co2_flasche_rest", "9.9"),
            Zustand($"input_number.co2_z{_zelt2}_flasche_rest", "4.5"),
        }) + "]";

        var liveA = await bestand.LiveAsync(CancellationToken.None);
        var liveB = await zelt.LiveAsync(CancellationToken.None);

        Assert.Equal(9.9, liveA.FlascheRestKg);
        Assert.Equal(4.5, liveB.FlascheRestKg);
        Assert.Equal(700, liveA.Co2Ppm);
        Assert.Equal(1111, liveB.Co2Ppm);
    }

    [Fact]
    public void Tage_LiegenJeZeltGetrennt()
    {
        var repo = new SteuerungRepository(_pfade);
        var bestand = Bestand();
        var zelt = bestand.Fuer(_zelt2);
        repo.CreateCo2Tag(new Co2Tag { Datum = "2026-11-02", Impulse = 7 });
        repo.CreateCo2Tag(new Co2Tag { Datum = "2026-11-02", Impulse = 2, ZeltId = _zelt2 });

        Assert.Equal([7], bestand.LetzteTage(5).Select(t => t.Impulse));
        Assert.Equal([2], zelt.LetzteTage(5).Select(t => t.Impulse));
    }

    [Fact]
    public void LaufenderGrow_FolgtImWeiterenZeltDemGrowInDiesemZelt()
    {
        var grows = new GrowRepository(_pfade);
        var a = grows.CreateGrow(new GrowRun { Name = "Grow A", TentId = _zelt1, StartDate = DateTime.Today.AddDays(-30), Status = GrowStatus.Running });
        var b = grows.CreateGrow(new GrowRun { Name = "Grow B", TentId = _zelt2, StartDate = DateTime.Today.AddDays(-5), Status = GrowStatus.Running });
        var bestand = Bestand();

        // Bestand wie bisher: der älteste laufende Grow. Weiteres Zelt: der Grow in seinem Zelt.
        Assert.Equal("Grow A", bestand.LaufenderGrow().Grow?.Name);
        Assert.Equal("Grow B", bestand.Fuer(_zelt2).LaufenderGrow().Grow?.Name);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Worker_BewirtschaftetJedesZeltMitEinstellungen_UndNurSeineHelfer()
    {
        var repo = new SteuerungRepository(_pfade);
        repo.SetEinstellungen("co2", new Co2Einstellungen());
        repo.SetEinstellungen($"co2-z{_zelt2}", new Co2Einstellungen { ZielWarmPpm = 1000 });
        var dienste = new ServiceCollection().AddScoped(_ => Bestand()).AddScoped(_ => new SteuerungRepository(_pfade)).BuildServiceProvider();
        var worker = new Co2SyncWorker(dienste, NullLogger<Co2SyncWorker>.Instance);

        await worker.EinmalAsync(CancellationToken.None);

        var ids = GeschriebeneEntitaeten().ToList();
        Assert.Contains("input_number.co2_zielwert", ids);                          // Bestand-Zelt: wie bisher
        Assert.Contains($"input_number.co2_z{_zelt2}_zielwert", ids);               // weiteres Zelt: eigener Satz
        // Jede geschriebene Kennung gehört zu genau einem der beiden Sätze.
        Assert.All(ids, id => Assert.Equal(id.Contains($"co2_z{_zelt2}_"), id.Contains("co2_z" + _zelt2)));
    }

    [Fact]
    public async Task Worker_LaesstEinZeltOhneGespeicherteEinstellungenInRuhe()
    {
        new SteuerungRepository(_pfade).SetEinstellungen("co2", new Co2Einstellungen());
        var dienste = new ServiceCollection().AddScoped(_ => Bestand()).AddScoped(_ => new SteuerungRepository(_pfade)).BuildServiceProvider();
        var worker = new Co2SyncWorker(dienste, NullLogger<Co2SyncWorker>.Instance);

        await worker.EinmalAsync(CancellationToken.None);

        var ids = GeschriebeneEntitaeten().ToList();
        Assert.Contains("input_number.co2_zielwert", ids);                          // Selbsttest: der Lauf hat geschrieben
        Assert.DoesNotContain(ids, id => id.Contains($"co2_z{_zelt2}_"));
        Assert.Null(new SteuerungRepository(_pfade).GetCo2Tag(DateTime.Now.ToString("yyyy-MM-dd"), _zelt2));
    }

    [Fact]
    public void Startwerte_FuerEinZeltModul_LiefernDenDienstDesZeltes()
    {
        var bestand = Bestand();
        var dienste = new Dictionary<string, IStartwertDienst>(StringComparer.OrdinalIgnoreCase) { ["co2"] = bestand };

        var dienst = SteuerungStartwerteService.Waehlen($"co2-z{_zelt2}", dienste);

        Assert.NotNull(dienst);
        Assert.Equal($"co2-z{_zelt2}", dienst!.StartModul);
        Assert.Equal("co2", SteuerungStartwerteService.Waehlen("co2", dienste)!.StartModul);
        Assert.True(SteuerungStartwerteService.Kann($"co2-z{_zelt2}"));
    }

    private static string ProjektWurzel()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }

    private static void KopiereWissen(string quelle, string ziel)
    {
        var nach = Path.Combine(ziel, "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(nach, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
    }
}
