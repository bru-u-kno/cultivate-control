using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Wasserkühler-Instanzen (Etappe 2, Release 5a): Der Kühler-Dienst arbeitet mit den Kennungen seiner Instanz, der Wochenplan führt
/// die Zieltemperatur-Helfer jeder Instanz in deren Zelt, „Zelt ruhend“ kennt jede Instanz als eigenes Gerät. Die erste Instanz
/// bleibt, wie sie war.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class ChillerInstanzTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;

    public ChillerInstanzTests()
    {
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "chiller", Nummer = 2, Name = "Reserve" }]);
        _wurzel = Path.Combine(Path.GetTempPath(), "ChillerInstanz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        _pfade = new AppPaths(_wurzel);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private static IEnumerable<string> Konstanten()
        => typeof(ChillerSteuerungService.Entitaeten).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    // ------------------------------------------------------------------ Kennungen

    [Fact]
    public void JedeKennungDesDienstesStehtImKatalogUndWirdFuerDieInstanzUebersetzt()
    {
        var konstanten = Konstanten().ToList();
        Assert.True(konstanten.Count >= 12, $"Gefunden: {konstanten.Count} Kennungen.");

        // Die Konstanten sind die Kennungen der ersten Instanz; im Katalog stehen sie bei Bauteilen (Helfer, Rechenwerte) oder bei
        // den Automationen der Vorlagen. Was Bauteil ist, muss in der Instanz unter Kennung(…, 2) stehen.
        var erste = SteuerungBauteile.FuerModul("chiller").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var zweite = SteuerungBauteile.FuerModul("chiller-i2").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var kennung in konstanten.Where(erste.Contains))
        {
            Assert.Contains(ChillerSteuerungService.Kennung(kennung, 2), zweite);
            Assert.Equal(kennung + "_i2", ChillerSteuerungService.Kennung(kennung, 2));
            Assert.Equal(kennung, ChillerSteuerungService.Kennung(kennung, null));
        }

        // Mengenwächter: ohne Treffer liefe die Schleife null Mal.
        Assert.True(konstanten.Count(erste.Contains) >= 9, "Zu wenige Kennungen des Dienstes stehen im Katalog.");
    }

    [Fact]
    public void DieAutomationenDerVorlagenHeissenInDerInstanzWieDerDienstSieSucht()
    {
        // Der Dienst schaltet automation.water_chiller_regelung_i2 / _sollwert_i2 — HA leitet die Kennung aus dem Alias ab.
        var ordner = Path.Combine(ProjektWurzel(), "GrowDiary.Web", "Vorlagen", "chiller");
        var eigene = SteuerungBauteile.FuerModul("chiller").Select(b => b.EntityId).ToList();
        var aliase = Directory.EnumerateFiles(ordner, "*.json")
            .Select(d => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(d))!)
            .Select(v => InstanzModul.Vorlage(v, "chiller-i2", eigene)["alias"]!.GetValue<string>())
            .ToList();

        Assert.Contains("Water Chiller Regelung I2", aliase);
        Assert.Contains("Water Chiller Sollwert I2", aliase);
        Assert.Equal("automation.water_chiller_regelung_i2", ChillerSteuerungService.AutomatikFuer(ChillerAnsteuerung.Steckdose, 2));
        Assert.Equal("automation.water_chiller_sollwert_i2", ChillerSteuerungService.AutomatikFuer(ChillerAnsteuerung.Regelbar, 2));
        Assert.Equal("automation.water_chiller_regelung", ChillerSteuerungService.AutomatikFuer(ChillerAnsteuerung.Steckdose));
    }

    [Fact]
    public void EinstellungenWerdenAusDenHelfernDerInstanzGelesen()
    {
        var zustaende = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input_number.chiller_zieltemperatur_tag"] = "18",       // erste Instanz — darf die zweite nicht beeinflussen
            ["input_number.chiller_zieltemperatur_tag_i2"] = "21.5",
            ["input_number.chiller_zieltemperatur_nacht_i2"] = "19",
            ["input_number.chiller_hysterese_i2"] = "0.8",
            ["automation.water_chiller_regelung_i2"] = "on",
        };

        var zweite = ChillerSteuerungService.AusHomeAssistant(zustaende, ChillerSteuerungService.AutomatikFuer(ChillerAnsteuerung.Steckdose, 2), 2);
        var erste = ChillerSteuerungService.AusHomeAssistant(zustaende);

        Assert.Equal(21.5, zweite.ZielTagC);
        Assert.Equal(19, zweite.ZielNachtC);
        Assert.Equal(0.8, zweite.HystereseK);
        Assert.True(zweite.AutomatikAktiv);
        Assert.Equal(18, erste.ZielTagC);
    }

    [Fact]
    public void DieSchreibelisteDerInstanzTrifftNurIhreHelfer()
    {
        var e = new ChillerEinstellungen { ZielTagC = 22, ZielNachtC = 20 };

        var zweite = ChillerSteuerungService.Schreibliste(e, [], 2).Select(z => z.Entity).ToList();
        var erste = ChillerSteuerungService.Schreibliste(e, []).Select(z => z.Entity).ToList();

        Assert.Equal(5, zweite.Count);
        Assert.All(zweite, id => Assert.EndsWith("_i2", id));
        Assert.All(erste, id => Assert.DoesNotContain("_i2", id));
        // Was der Wochenplan der Instanz führt, schreibt der Dienst nicht.
        var ohneZiele = ChillerSteuerungService.Schreibliste(e, ["input_number.chiller_zieltemperatur_tag_i2"], 2).Select(z => z.Entity).ToList();
        Assert.DoesNotContain("input_number.chiller_zieltemperatur_tag_i2", ohneZiele);
        Assert.Equal(4, ohneZiele.Count);
    }

    // ------------------------------------------------------------------ Dienst

    private ChillerSteuerungService Dienst()
    {
        var steuerung = new SteuerungRepository(_pfade);
        var ha = new HomeAssistantService(new StubHttpClientFactory(new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"))), NullLogger<HomeAssistantService>.Instance);
        var einst = new HomeAssistantSettingsRepository(_pfade);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        var plan = new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha, einst, NullLogger<WochenplanSyncService>.Instance);
        return new ChillerSteuerungService(steuerung, ha, einst, new SteuerungGeraeteService(steuerung), grows, plan, NullLogger<ChillerSteuerungService>.Instance);
    }

    [Fact]
    public void DerDienstEinerInstanzHatSeinModulUndSpeichertGetrennt()
    {
        var erste = Dienst();
        var zweite = erste.Fuer("chiller-i2");

        Assert.Equal("chiller", erste.ModulName);
        Assert.Null(erste.Nummer);
        Assert.Equal("chiller-i2", zweite.ModulName);
        Assert.Equal(2, zweite.Nummer);

        // Ein Modul, das keine Wasserkühler-Instanz ist, bekommt die erste Instanz.
        Assert.Null(erste.Fuer("zuluft-i2").Nummer);
        Assert.Null(zweite.Fuer("entfeuchter-i2").Nummer);
        Assert.Equal("chiller", zweite.Bestand().ModulName);

        // Der Eigenbestand ist getrennt: Speichern in der Instanz lässt die erste unberührt.
        var repo = new SteuerungRepository(_pfade);
        repo.SetEinstellungen("chiller-i2", new ChillerEinstellungen { ZielTagC = 23, HystereseGefuehrt = true });
        Assert.Equal(23, zweite.Gespeichert!.ZielTagC);
        Assert.Null(erste.Gespeichert);
    }

    [Fact]
    public void DieKopieDerInstanzVeraendertDenDienstDerErstenInstanzNicht()
    {
        var erste = Dienst();
        _ = erste.Fuer("chiller-i2");
        Assert.Equal("chiller", erste.ModulName);
        Assert.Null(erste.Nummer);
    }

    // ------------------------------------------------------------------ Startwerte

    [Theory]
    [InlineData("chiller-i2", true)]
    [InlineData("chiller-i17", true)]
    [InlineData("zuluft-i2", false)]       // deren Dienste können noch kein Fuer(modul)
    [InlineData("entfeuchter-zusatz-i2", false)]
    [InlineData("licht-i2", false)]
    public void NurDieInstanzenMitFuerModulHabenEinenStartwertWeg(string modul, bool kann)
        => Assert.Equal(kann, SteuerungStartwerteService.Kann(modul));

    [Fact]
    public void FuerEineInstanzWaehltDieStartwertWahlDenDienstMitIhremModul()
    {
        var erste = Dienst();
        var dienste = new Dictionary<string, IStartwertDienst>(StringComparer.OrdinalIgnoreCase) { ["chiller"] = erste };

        var gewaehlt = SteuerungStartwerteService.Waehlen("chiller-i2", dienste);

        Assert.NotNull(gewaehlt);
        Assert.Equal("chiller-i2", gewaehlt!.StartModul);
        Assert.Equal("chiller", ((IStartwertDienst)erste).StartModul); // die erste Instanz bleibt, wie sie war
        Assert.Throws<InvalidOperationException>(() => SteuerungStartwerteService.Waehlen("chiller-i2", new Dictionary<string, IStartwertDienst>()));
    }

    // ------------------------------------------------------------------ Zelt ruhend

    [Fact]
    public void ZeltRuhendKenntJedeInstanzAlsEigenesGeraet()
    {
        var art = ZeltRuheArten.Finden("chiller-i2");

        Assert.NotNull(art);
        Assert.Equal("Wasserkühler Reserve", art!.Titel);
        Assert.Equal("chiller-i2", art.Modul);
        Assert.Equal("steckdose", art.Rolle);
        Assert.True(art.Standard);
        Assert.True(ZeltRuheArten.IstKuehlerInstanz("chiller-i2"));
        Assert.False(ZeltRuheArten.IstKuehlerInstanz("chiller"));
        Assert.False(ZeltRuheArten.IstKuehlerInstanz("zuluft-i2"));
        Assert.Contains("chiller-i2", ZeltRuheArten.Standard(["chiller", "chiller-i2"]));
        Assert.Equal(["chiller"], ZeltRuheArten.Standard(["chiller"])); // ohne Instanz im Register: nichts dazu
        Assert.Null(ZeltRuheArten.Finden("chiller-i3"));

        // Die feste Liste bleibt unberührt.
        Assert.Equal(7, ZeltRuheArten.Alle.Count);
        Assert.Equal(8, ZeltRuheArten.AlleMitInstanzen().Count);
    }

    [Fact]
    public void DieWirksameAuswahlNimmtDieInstanzMitWennSieGewaehltIst()
    {
        var wirksam = ZeltRuheService.Wirksam(["chiller", "chiller-i2", "licht"], ["chiller", "chiller-i2", "licht"]);
        Assert.Equal(["licht", "chiller", "chiller-i2"], wirksam);

        // Ein Gerät ohne Schalter-Zuordnung (nicht „vorhanden“) bleibt außen vor.
        Assert.Equal(["chiller"], ZeltRuheService.Wirksam(["chiller", "chiller-i2"], ["chiller"]));
    }

    // ------------------------------------------------------------------ Wochenplan

    [Fact]
    public void DerWochenplanFuehrtDieZielHelferDerInstanzInIhremZelt()
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
        var z1 = zelte.GetTents().First();
        var zelt1 = z1.Id;
        var zelt2 = zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        var a = grows.GetGrow(grows.CreateGrow(new GrowRun
        {
            Name = "Grow A", TentId = zelt1, StartDate = DateTime.Today.AddDays(-75), FlipDate = DateTime.Today.AddDays(-20), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        }))!;
        var b = grows.GetGrow(grows.CreateGrow(new GrowRun
        {
            Name = "Grow B", TentId = zelt2, StartDate = DateTime.Today.AddDays(-5), Status = GrowStatus.Running,
            UseFeedChartTargets = true, FeedProgramId = "skx-canna-aqua",
        }))!;
        var steuerung = new SteuerungRepository(_pfade);
        var zuweisung = new SteuerungZeltService(steuerung, zelte, grows);
        var ha = new HomeAssistantService(new StubHttpClientFactory(new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"))), NullLogger<HomeAssistantService>.Instance);
        var plan = new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha,
            new HomeAssistantSettingsRepository(_pfade), NullLogger<WochenplanSyncService>.Instance, zuweisung);

        // Erste Instanz in Zelt A, die Reserve in Zelt B.
        Assert.Null(zuweisung.Speichern(new Dictionary<string, int> { ["entfeuchter"] = zelt1, ["chiller"] = zelt1, ["chiller-i2"] = zelt2 }));
        var spalteA = MischplanService.ZielSpalteFuerGrow(a, wissen.NutrientPrograms)!.Value.Spalte;
        var spalteB = MischplanService.ZielSpalteFuerGrow(b, wissen.NutrientPrograms)!.Value.Spalte;

        var ersteHelfer = plan.GefuehrteHelfer();
        var zweiteHelfer = plan.GefuehrteChillerHelfer("chiller-i2");

        Assert.Equal(spalteA.WaterTempDayC!.Value, ersteHelfer["input_number.chiller_zieltemperatur_tag"]);
        Assert.Equal(spalteB.WaterTempDayC!.Value, zweiteHelfer["input_number.chiller_zieltemperatur_tag_i2"]);
        Assert.DoesNotContain("input_number.chiller_zieltemperatur_tag_i2", ersteHelfer.Keys);
        Assert.DoesNotContain("input_number.chiller_zieltemperatur_tag", zweiteHelfer.Keys);
        Assert.NotEqual(spalteA.WaterTempDayC.Value, spalteB.WaterTempDayC.Value); // sonst belegt der Vergleich nichts

        // Kein Wasserkühler-Modul, kein Plan: leer.
        Assert.Empty(plan.GefuehrteChillerHelfer("chiller"));
        Assert.Empty(plan.GefuehrteChillerHelfer("zuluft-i2"));
        // Die Dienste sagen, was der Plan führt: die Instanz schreibt ihr Zielpaar nicht.
        Assert.DoesNotContain(ChillerSteuerungService.Schreibliste(new ChillerEinstellungen(), zweiteHelfer.Keys, 2),
            z => z.Entity == "input_number.chiller_zieltemperatur_tag_i2");
    }

    private static string ProjektWurzel()
    {
        var ordner = new DirectoryInfo(AppContext.BaseDirectory);
        while (ordner is not null && !Directory.Exists(Path.Combine(ordner.FullName, "GrowDiary.Web", "wwwroot"))) ordner = ordner.Parent;
        return ordner?.FullName ?? throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }
}
