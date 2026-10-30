using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Services.Knowledge.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Der Plan der Trocknung (A-029, Etappe 2): welche Etappe gilt am wievielten Tag, welches Band zeigt die
/// Kachel, und welcher Grow trocknet gerade.
/// </summary>
/// <remarks>
/// <para><b>Der Anlass (30.10.2026).</b> Das Trocknungs-Klima stand als feste Zahlen in <c>MoldGuard</c>
/// (18–20 °C, 55–60 %) und galt für jeden Tag gleich. Ein Test dafür gab es nicht — die Kachel-Ziele
/// lagen in einer privaten Methode des Composers. Jetzt ist die Rechnung rein und wird hier geprüft.</para>
/// </remarks>
public sealed class TrocknungsPlanTests : IDisposable
{
    private readonly string _wurzel;

    public TrocknungsPlanTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "Trocknung_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
    }

    public void Dispose()
    {
        // Wer die Variable setzt, räumt sie auf: sonst greifen die nächsten Testklassen im selben Lauf auf
        // eine gelöschte Datenbank zu („Sequence contains no elements“ in anderen Tests).
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private static TrockenEtappe E(string name, int tage, double temp = 19, double ziel = 58, double max = 65)
        => new() { Name = name, DauerTage = tage, LufttemperaturC = temp, LuftfeuchteZiel = ziel, LuftfeuchteMax = max };

    // ---- Standard ---------------------------------------------------------------------------

    [Fact]
    public void Der_Standard_ist_eine_gueltige_Folge_und_haelt_das_Trocknungsfenster_ein()
    {
        Assert.Null(TrocknungsPlan.Pruefen(TrocknungsPlan.Standard));
        Assert.True(TrocknungsPlan.Standard.Count >= 2, "Ein Standard aus einer Etappe wäre kein Plan.");
        Assert.True(TrocknungsPlan.Standard.Sum(e => e.DauerTage) <= TrocknungsPlan.MaxTage);
    }

    [Fact]
    public void Ohne_eigene_Etappen_gilt_der_Standard_mit_ihnen_die_des_Programms()
    {
        Assert.Same(TrocknungsPlan.Standard, TrocknungsPlan.Fuer(null));
        Assert.Same(TrocknungsPlan.Standard, TrocknungsPlan.Fuer(new NutrientProgramDefinition()));
        Assert.Same(TrocknungsPlan.Standard, TrocknungsPlan.Fuer(new NutrientProgramDefinition { Trocknung = [] }));

        var eigene = new List<TrockenEtappe> { E("Meine", 5) };
        Assert.Same(eigene, TrocknungsPlan.Fuer(new NutrientProgramDefinition { Trocknung = eigene }));
        Assert.True(TrocknungsPlan.HatEigene(new NutrientProgramDefinition { Trocknung = eigene }));
        Assert.False(TrocknungsPlan.HatEigene(new NutrientProgramDefinition { Trocknung = [] }));
    }

    // ---- Welche Etappe gilt -----------------------------------------------------------------

    [Theory]
    [InlineData(1, 0, 1)]   // erster Tag
    [InlineData(3, 0, 3)]   // letzter Tag der ersten Etappe
    [InlineData(4, 1, 1)]   // erster Tag der zweiten
    [InlineData(7, 1, 4)]   // letzter Tag der zweiten
    [InlineData(8, 2, 1)]   // erster Tag der dritten
    [InlineData(10, 2, 3)]  // letzter Tag des Plans
    public void Die_Etappe_folgt_den_Tagen_ohne_Luecke_und_ohne_Doppelung(int tag, int index, int tagInEtappe)
    {
        var stand = TrocknungsPlan.EtappeAm([E("a", 3), E("b", 4), E("c", 3)], tag)!;

        Assert.Equal(index, stand.Index);
        Assert.Equal(tagInEtappe, stand.TagInEtappe);
        Assert.False(stand.Gehalten);
    }

    [Fact]
    public void Hinter_dem_Plan_gilt_die_letzte_Etappe_weiter_und_sagt_es()
    {
        var stand = TrocknungsPlan.EtappeAm([E("a", 3), E("b", 4), E("c", 3)], 14)!;

        Assert.Equal(2, stand.Index);
        Assert.Equal("c", stand.Etappe.Name);
        Assert.True(stand.Gehalten);
        Assert.Equal(7, stand.TagInEtappe); // der siebte Tag in der letzten Etappe: 14 − 7
    }

    [Fact]
    public void Ohne_Etappen_und_vor_dem_ersten_Tag_gibt_es_keine_Etappe()
    {
        Assert.Null(TrocknungsPlan.EtappeAm([], 3));
        Assert.Null(TrocknungsPlan.EtappeAm([E("a", 3)], 0));
        Assert.Null(TrocknungsPlan.EtappeAm([E("a", 3)], -2));
    }

    [Fact]
    public void Eine_Dauer_unter_einem_Tag_blockiert_den_Plan_nicht()
    {
        // Gespeichert wird so etwas nie (Pruefen lehnt es ab), aber eine von Hand editierte Datei
        // darf die Kachel nicht in eine Endlosschleife oder auf „keine Etappe“ schicken.
        var stand = TrocknungsPlan.EtappeAm([E("a", 0), E("b", 3)], 2)!;

        Assert.Equal("b", stand.Etappe.Name);
    }

    // ---- Zielband der Kachel ----------------------------------------------------------------

    [Fact]
    public void Das_Band_liegt_um_das_Ziel_und_die_Feuchte_bleibt_unter_der_Obergrenze()
    {
        var band = TrocknungsPlan.BandFuer(E("a", 3, temp: 19, ziel: 58, max: 65));

        Assert.Equal(18, band.TempMin);
        Assert.Equal(20, band.TempMax);
        Assert.Equal(56, band.FeuchteMin);
        Assert.Equal(60, band.FeuchteMax);

        // Liegt die Obergrenze knapp über dem Ziel, deckelt sie das Band.
        var knapp = TrocknungsPlan.BandFuer(E("a", 3, ziel: 60, max: 61));
        Assert.Equal(61, knapp.FeuchteMax);
        Assert.True(knapp.FeuchteMin <= knapp.FeuchteMax);

        // Ziel gleich Obergrenze: das Band ist ein Punkt oder darunter, nie verkehrt herum.
        var gleich = TrocknungsPlan.BandFuer(E("a", 3, ziel: 60, max: 60));
        Assert.Equal(60, gleich.FeuchteMax);
        Assert.True(gleich.FeuchteMin <= gleich.FeuchteMax);
    }

    // ---- Prüfregeln -------------------------------------------------------------------------

    [Fact]
    public void Gueltige_Folgen_werden_angenommen()
    {
        Assert.Null(TrocknungsPlan.Pruefen([E("eine", 10)]));
        Assert.Null(TrocknungsPlan.Pruefen([E("a", 7), E("b", 7), E("c", 7)])); // genau 21 Tage
    }

    [Theory]
    [MemberData(nameof(Ungueltige))]
    public void Ungueltiges_wird_mit_einem_Satz_abgelehnt(string erwartet, List<TrockenEtappe> etappen)
    {
        var meldung = TrocknungsPlan.Pruefen(etappen);

        Assert.NotNull(meldung);
        Assert.Contains(erwartet, meldung);
    }

    public static IEnumerable<object[]> Ungueltige()
    {
        yield return ["Mindestens eine Etappe", new List<TrockenEtappe>()];
        yield return ["Höchstens 8", Enumerable.Range(1, 9).Select(i => E($"e{i}", 1)).ToList()];
        yield return ["Etappe 1: Der Name fehlt", new List<TrockenEtappe> { E("  ", 3) }];
        yield return ["höchstens 60 Zeichen", new List<TrockenEtappe> { E(new string('x', 61), 3) }];
        yield return ["Etappe 2: Die Dauer", new List<TrockenEtappe> { E("a", 3), E("b", 0) }];
        yield return ["Die Dauer", new List<TrockenEtappe> { E("a", 22) }];
        yield return ["Temperatur", new List<TrockenEtappe> { E("a", 3, temp: 50) }];
        yield return ["Temperatur", new List<TrockenEtappe> { E("a", 3, temp: double.NaN) }];
        yield return ["Luftfeuchte muss", new List<TrockenEtappe> { E("a", 3, ziel: 95) }];
        yield return ["Obergrenze der Luftfeuchte muss", new List<TrockenEtappe> { E("a", 3, max: 20) }];
        yield return ["Die Obergrenze (55 %) liegt unter dem Ziel (60 %)", new List<TrockenEtappe> { E("a", 3, ziel: 60, max: 55) }];
        yield return ["Zusammen 24 Tage", new List<TrockenEtappe> { E("a", 12), E("b", 12) }];
    }

    [Fact]
    public void Bereinigt_schneidet_Namen_ab_und_rundet_ohne_das_Original_zu_aendern()
    {
        var original = new List<TrockenEtappe> { E("  Anfang  ", 3, temp: 19.04, ziel: 58.06) };

        var sauber = TrocknungsPlan.Bereinigt(original);

        Assert.Equal("Anfang", sauber[0].Name);
        Assert.Equal(19.0, sauber[0].LufttemperaturC);
        Assert.Equal(58.1, sauber[0].LuftfeuchteZiel);
        Assert.Equal("  Anfang  ", original[0].Name); // die Eingabe bleibt, wie sie war
    }

    // ---- Die Kachel folgt der Etappe --------------------------------------------------------

    /// <summary>Ein Zelt, dessen Grow vor <paramref name="tageSeitErnte"/> Tagen geerntet wurde, mit eigenem Programm.</summary>
    private (GrowDashboardComposer Composer, Tent Zelt) Trockenzelt(int tageSeitErnte, List<TrockenEtappe>? etappen, bool mitProgramm = true)
    {
        // Jeder Aufruf bekommt Ordner und Datenbank für sich: zwei Zelte im selben Test dürfen
        // einander nicht überdecken (der jüngste geerntete Grow gewinnt).
        var wurzel = Path.Combine(_wurzel, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(wurzel, "test.db"));
        var paths = new AppPaths(wurzel);
        var zelt = TestDatabase.InitializeWithDefaultTent(paths);
        KopiereWissen(wurzel);
        var wissenPfade = paths;
        var wissen = new KnowledgeBaseLoader(wissenPfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();

        var programmId = "skx-canna-aqua";
        if (etappen is not null)
        {
            var eigene = new EigeneProgramme(wissenPfade, wissen, NullLogger<EigeneProgramme>.Instance);
            var kopie = eigene.Anlegen(eigene.Finden("skx-canna-aqua")!, "Trocknung-Test");
            kopie.Trocknung = etappen;
            eigene.Speichern(kopie);
            programmId = kopie.Id;
        }

        var grows = new GrowRepository(paths);
        var ernten = new HarvestRepository(paths);
        var heute = DateTime.Today;
        var growId = grows.CreateGrow(new GrowRun
        {
            Name = "Geerntet", TentId = zelt.Id, FeedProgramId = mitProgramm ? programmId : null,
            StartDate = heute.AddDays(-90), EndDate = heute.AddDays(-tageSeitErnte), Status = GrowStatus.Completed,
        });
        ernten.Create(new HarvestEntry { GrowId = growId, WetWeightG = 500 });

        var composer = new GrowDashboardComposer(
            TestKnowledgeBase.TargetValues(), NullLogger<GrowDashboardComposer>.Instance,
            grows: grows, harvests: ernten, knowledge: wissen);
        return (composer, zelt);
    }

    private static List<MetricCard> Karten(GrowDashboardComposer composer, Tent zelt)
        => composer.BuildTentMetrics(zelt, new Dictionary<string, HomeAssistantState>(),
            [new Measurement { Id = 1, TakenAt = DateTime.Now.AddMinutes(-5), AirTemperatureC = 18, HumidityPercent = 57 }]);

    [Fact]
    public void Die_Kacheln_zeigen_das_Zielband_der_Etappe_des_Tages_mit_ihrem_Namen()
    {
        var etappen = new List<TrockenEtappe> { E("Zuerst", 2, temp: 20, ziel: 60, max: 65), E("Dann", 5, temp: 17, ziel: 50, max: 55) };

        // Tag 1 (Ernte vor 0 Tagen): erste Etappe.
        var (c1, z1) = Trockenzelt(0, etappen);
        var t1 = Assert.Single(Karten(c1, z1), k => k.Key == "temperature");
        Assert.Equal(19, t1.TargetMin);
        Assert.Equal(21, t1.TargetMax);
        Assert.Equal("Trocknung · Tag 1 · Zuerst", t1.TargetNote);

        // Tag 4 (Ernte vor 3 Tagen): zweite Etappe, anderes Band — nicht mehr die festen 18–20 °C.
        var (c2, z2) = Trockenzelt(3, etappen);
        var karten = Karten(c2, z2);
        var t2 = Assert.Single(karten, k => k.Key == "temperature");
        var f2 = Assert.Single(karten, k => k.Key == "humidity");
        Assert.Equal(16, t2.TargetMin);
        Assert.Equal(18, t2.TargetMax);
        Assert.Equal(48, f2.TargetMin);
        Assert.Equal(52, f2.TargetMax);
        Assert.Equal("Trocknung · Tag 4 · Dann", f2.TargetNote);
        Assert.False(t2.TargetDerived);
    }

    [Fact]
    public void Ohne_eigene_Etappen_zeigt_die_Kachel_den_Standard_und_hinter_dem_Plan_die_letzte_Etappe()
    {
        // Eigenes Programm, aber ohne Etappen → Standard: Tag 1 gehört zu „Anfang, langsam“ (20 °C).
        var (c1, z1) = Trockenzelt(0, etappen: null);
        var t1 = Assert.Single(Karten(c1, z1), k => k.Key == "temperature");
        Assert.Equal(19, t1.TargetMin);
        Assert.Equal(21, t1.TargetMax);
        Assert.Equal($"Trocknung · Tag 1 · {TrocknungsPlan.Standard[0].Name}", t1.TargetNote);

        // Tag 15, hinter den 10 Tagen des Standards: die letzte Etappe gilt weiter (18 °C).
        var (c2, z2) = Trockenzelt(14, etappen: null);
        var t2 = Assert.Single(Karten(c2, z2), k => k.Key == "temperature");
        Assert.Equal(17, t2.TargetMin);
        Assert.Equal(19, t2.TargetMax);
        Assert.Equal($"Trocknung · Tag 15 · {TrocknungsPlan.Standard[^1].Name}", t2.TargetNote);
    }

    [Fact]
    public void Ein_Grow_ohne_Programm_trocknet_nach_dem_Standard_statt_ohne_Ziele()
    {
        var (composer, zelt) = Trockenzelt(0, etappen: null, mitProgramm: false);

        var temperatur = Assert.Single(Karten(composer, zelt), k => k.Key == "temperature");

        Assert.NotNull(temperatur.TargetMin);
        Assert.Contains("Trocknung · Tag 1", temperatur.TargetNote);
    }

    [Fact]
    public void Ohne_Trocknung_im_Zelt_bleiben_die_Kachelziele_unberuehrt()
    {
        // Die Ernte liegt hinter dem Fenster: kein Trocknungsvermerk.
        var (composer, zelt) = Trockenzelt(40, etappen: null);

        var temperatur = Assert.Single(Karten(composer, zelt), k => k.Key == "temperature");

        Assert.DoesNotContain("Trocknung", temperatur.TargetNote ?? string.Empty);
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

    // ---- Welcher Grow trocknet gerade -------------------------------------------------------

    [Fact]
    public void StandFor_nennt_den_Grow_und_denselben_Tag_wie_DayFor()
    {
        var paths = new AppPaths(_wurzel);
        var zelt = TestDatabase.InitializeWithDefaultTent(paths);
        var grows = new GrowRepository(paths);
        var ernten = new HarvestRepository(paths);
        var heute = new DateTime(2026, 10, 30);
        var growId = grows.CreateGrow(new GrowRun
        {
            Name = "Geerntet",
            TentId = zelt.Id,
            FeedProgramId = "eigen-test",
            StartDate = heute.AddDays(-90),
            EndDate = heute.AddDays(-2),
            Status = GrowStatus.Completed,
        });
        ernten.Create(new HarvestEntry { GrowId = growId, WetWeightG = 500 });

        var stand = DryingWindow.StandFor(grows, ernten, zelt.Id, heute);

        Assert.NotNull(stand);
        Assert.Equal(growId, stand!.Grow.Id);
        Assert.Equal("eigen-test", stand.Grow.FeedProgramId);
        Assert.Equal(3, stand.Tag); // Erntetag + 2 Tage → Trocknungstag 3
        Assert.Equal(stand.Tag, DryingWindow.DayFor(grows, ernten, zelt.Id, heute));
    }

    [Fact]
    public void StandFor_endet_mit_dem_Trockengewicht_und_nach_dem_Fenster()
    {
        var paths = new AppPaths(_wurzel);
        var zelt = TestDatabase.InitializeWithDefaultTent(paths);
        var grows = new GrowRepository(paths);
        var ernten = new HarvestRepository(paths);
        var heute = new DateTime(2026, 10, 30);
        var growId = grows.CreateGrow(new GrowRun
        {
            Name = "Geerntet", TentId = zelt.Id, StartDate = heute.AddDays(-90),
            EndDate = heute.AddDays(-2), Status = GrowStatus.Completed,
        });
        var ernteId = ernten.Create(new HarvestEntry { GrowId = growId, WetWeightG = 500 });
        Assert.NotNull(DryingWindow.StandFor(grows, ernten, zelt.Id, heute));

        // Das Trockengewicht ist der natürliche Abschluss.
        ernten.Update(new HarvestEntry { Id = ernteId, GrowId = growId, WetWeightG = 500, DryWeightG = 110 });
        Assert.Null(DryingWindow.StandFor(grows, ernten, zelt.Id, heute));
        Assert.Null(DryingWindow.DayFor(grows, ernten, zelt.Id, heute));

        // Und nach dem Fenster klebt der Modus nicht ewig.
        ernten.Update(new HarvestEntry { Id = ernteId, GrowId = growId, WetWeightG = 500 });
        Assert.NotNull(DryingWindow.StandFor(grows, ernten, zelt.Id, heute));
        Assert.Null(DryingWindow.StandFor(grows, ernten, zelt.Id, heute.AddDays(30)));
    }

    [Fact]
    public void StandFor_ohne_Repositories_ist_leer_statt_ein_Fehler()
    {
        Assert.Null(DryingWindow.StandFor(null, null, 1, DateTime.Today));
    }
}
