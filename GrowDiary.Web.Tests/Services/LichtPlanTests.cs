using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Lichtplan: die Kurve Stufe → Lichtwert, die Wahl der Stufe, die Entscheidung der Automatik und das Speichern der
/// Kalibrierung. Die Zahlen sind das Rechenbeispiel aus dem freigegebenen Mockup (Hersteller 1200 bei Stufe 10, gemessen
/// 610 bei Stufe 5 und 355 bei Stufe 3).
/// </summary>
public sealed class LichtPlanTests : IDisposable
{
    private readonly string _wurzel;
    private readonly SteuerungRepository _repo;

    public LichtPlanTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "LichtPlan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.Initialize(pfade);
        _repo = new SteuerungRepository(pfade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch (IOException) { /* Aufräumen darf den Test nicht kippen */ }
    }

    private static LichtKalibrierung Beispiel() => new()
    {
        HerstellerPpfd = 1200,
        HerstellerAbstandCm = 30,
        Messpunkte = [new() { Stufe = 5, Ppfd = 610 }, new() { Stufe = 3, Ppfd = 355 }],
    };

    [Fact]
    public void Ohne_Angaben_gibt_es_keine_Kurve_und_keine_Stufe()
    {
        var kurve = LichtKennlinie.Kurve(new LichtKalibrierung());
        Assert.Empty(kurve);
        Assert.Null(LichtKennlinie.StufeFuer(kurve, 800));
    }

    [Fact]
    public void Gemessene_Punkte_und_Hersteller_stehen_fest_der_Rest_wird_verbunden()
    {
        var kurve = LichtKennlinie.Kurve(Beispiel());
        Assert.Equal(10, kurve.Count);
        Assert.Equal(new LichtKurvenPunkt(3, 355, LichtKennlinie.Gemessen), kurve[2]);
        Assert.Equal(new LichtKurvenPunkt(5, 610, LichtKennlinie.Gemessen), kurve[4]);
        Assert.Equal(new LichtKurvenPunkt(10, 1200, LichtKennlinie.Hersteller), kurve[9]);
        // Zwischen Stufe 3 und 5 gerade verbunden: Stufe 4 liegt in der Mitte.
        Assert.Equal(LichtKennlinie.Rechnung, kurve[3].Herkunft);
        Assert.Equal(Math.Round((355 + 610) / 2.0), kurve[3].Ppfd);
        // Unter der ersten Messung läuft die Gerade zum Ursprung (Stufe 0 = dunkel).
        Assert.Equal(Math.Round(355 / 3.0), kurve[0].Ppfd);
        // Die Kurve steigt mit der Stufe.
        Assert.True(kurve.Zip(kurve.Skip(1), (a, b) => b.Ppfd > a.Ppfd).All(x => x));
    }

    [Fact]
    public void Nur_die_Herstellerangabe_ergibt_eine_gerade_Linie_zum_Ursprung()
    {
        var kurve = LichtKennlinie.Kurve(new LichtKalibrierung { HerstellerPpfd = 1000 });
        Assert.Equal(100, kurve[0].Ppfd);
        Assert.Equal(500, kurve[4].Ppfd);
        Assert.Equal(LichtKennlinie.Hersteller, kurve[9].Herkunft);
    }

    [Fact]
    public void Eine_Messung_bei_Stufe_10_geht_der_Herstellerangabe_vor()
    {
        var kurve = LichtKennlinie.Kurve(new LichtKalibrierung
        {
            HerstellerPpfd = 1200,
            Messpunkte = [new() { Stufe = 10, Ppfd = 1100 }],
        });
        Assert.Equal(new LichtKurvenPunkt(10, 1100, LichtKennlinie.Gemessen), kurve[9]);
    }

    [Theory]
    [InlineData(300, 3)]   // Vega 1: 355 liegt näher als 240 (gerade Linie)
    [InlineData(800, 7)]
    [InlineData(1000, 8)]
    [InlineData(5000, 10)] // mehr als die Lampe kann: höchste Stufe
    [InlineData(1, 1)]
    public void Die_Stufe_ist_die_naechste_zum_Planwert(double ppfd, int erwartet)
        => Assert.Equal(erwartet, LichtKennlinie.StufeFuer(LichtKennlinie.Kurve(Beispiel()), ppfd));

    [Fact]
    public void Ungueltige_Angaben_werden_abgelehnt()
    {
        Assert.Contains(nameof(LichtKalibrierung.HerstellerPpfd), LichtKennlinie.Pruefen(new LichtKalibrierung { HerstellerPpfd = 0 }).Keys);
        Assert.Contains(nameof(LichtKalibrierung.HerstellerAbstandCm), LichtKennlinie.Pruefen(new LichtKalibrierung { HerstellerAbstandCm = 999 }).Keys);
        Assert.Contains(nameof(LichtKalibrierung.Messpunkte), LichtKennlinie.Pruefen(new LichtKalibrierung { Messpunkte = [new() { Stufe = 11, Ppfd = 500 }] }).Keys);
        Assert.Contains(nameof(LichtKalibrierung.Messpunkte), LichtKennlinie.Pruefen(new LichtKalibrierung { Messpunkte = [new() { Stufe = 3, Ppfd = 300 }, new() { Stufe = 3, Ppfd = 310 }] }).Keys);
        Assert.Empty(LichtKennlinie.Pruefen(Beispiel()));
    }

    [Theory]
    [InlineData(GrowStage.Seedling, "veggie")]
    [InlineData(GrowStage.Clone, "veggie")]
    [InlineData(GrowStage.Veg, "veggie")]
    [InlineData(GrowStage.Transition, "bluete")]
    [InlineData(GrowStage.Flower, "bluete")]
    [InlineData(GrowStage.Finish, "bluete")]
    [InlineData(GrowStage.Dry, null)]
    [InlineData(GrowStage.Cure, null)]
    public void Die_Phase_kommt_aus_dem_Grow_Plan(GrowStage stufe, string? erwartet)
        => Assert.Equal(erwartet, LichtPlanService.PhaseFuer(stufe));

    // ---------------------------------------------------------------- Automatik

    [Fact]
    public void Die_Automatik_ist_ohne_Einschalten_still()
    {
        var k = Beispiel(); // AutomatikStufe: Voreinstellung aus
        Assert.False(k.AutomatikStufe);
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, "flower-w1", 8, 3).Entscheidung);
    }

    [Fact]
    public void Eine_neue_Planwoche_setzt_die_vorgeschlagene_Stufe()
    {
        var k = Beispiel();
        k.AutomatikStufe = true;
        k.AutoWoche = "veg-w4";
        k.AutoStufe = 6;
        Assert.Equal((LichtAutoEntscheidung.Setzen, (int?)8), LichtPlanService.Entscheiden(k, "flower-w1", 8, 6));
    }

    [Fact]
    public void In_derselben_Woche_bleibt_sie_still_solange_die_Stufe_ihre_ist()
    {
        var k = Beispiel();
        k.AutomatikStufe = true;
        k.AutoWoche = "flower-w1";
        k.AutoStufe = 8;
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, "flower-w1", 8, 8).Entscheidung);
    }

    [Fact]
    public void Eine_Handstellung_wird_erkannt_und_in_derselben_Woche_nie_zurueckgesetzt()
    {
        var k = Beispiel();
        k.AutomatikStufe = true;
        k.AutoWoche = "flower-w1";
        k.AutoStufe = 8;
        // Der Nutzer stellt Stufe 3 gegen Wärme.
        Assert.Equal(LichtAutoEntscheidung.HandErkannt, LichtPlanService.Entscheiden(k, "flower-w1", 8, 3).Entscheidung);

        k.HandBisWoche = "flower-w1";
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, "flower-w1", 8, 3).Entscheidung);
        // Mit der nächsten Woche ist die Automatik wieder dran.
        Assert.Equal(LichtAutoEntscheidung.Setzen, LichtPlanService.Entscheiden(k, "flower-w2", 9, 3).Entscheidung);
    }

    [Fact]
    public void Ohne_Woche_oder_Vorschlag_passiert_nichts()
    {
        var k = Beispiel();
        k.AutomatikStufe = true;
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, null, 8, 3).Entscheidung);
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, "flower-w1", null, 3).Entscheidung);
    }

    // ---------------------------------------------------------------- Speichern

    private LichtPlanService Dienst()
        => new(_repo, null!, null!, null!, NullLogger<LichtPlanService>.Instance, () => new DateTime(2026, 11, 1));

    [Fact]
    public void Ohne_Speichern_ist_die_Automatik_aus_und_leer()
    {
        var k = Dienst().Kalibrierung;
        Assert.False(k.AutomatikStufe);
        Assert.Empty(k.Messpunkte);
        Assert.Null(k.HerstellerPpfd);
    }

    [Fact]
    public void Kalibrierung_ueberlebt_das_Speichern_und_die_Automatik_bleibt_aus()
    {
        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(Beispiel());
        Assert.NotNull(gespeichert);
        Assert.Empty(fehler);

        var gelesen = dienst.Kalibrierung;
        Assert.Equal(1200, gelesen.HerstellerPpfd);
        Assert.Equal(30, gelesen.HerstellerAbstandCm);
        Assert.Equal([(5, 610.0), (3, 355.0)], gelesen.Messpunkte.Select(m => (m.Stufe, m.Ppfd)));
        Assert.False(gelesen.AutomatikStufe);
    }

    [Fact]
    public void Ungueltige_Kalibrierung_wird_nicht_gespeichert()
    {
        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(new LichtKalibrierung { HerstellerPpfd = -5 });
        Assert.Null(gespeichert);
        Assert.NotEmpty(fehler);
        Assert.Null(dienst.Kalibrierung.HerstellerPpfd);
    }

    [Fact]
    public void Die_Merker_der_Automatik_gehoeren_der_Automatik_nicht_dem_Speichern_der_Oberflaeche()
    {
        var dienst = Dienst();
        _repo.SetEinstellungen(LichtPlanService.Modul, new LichtKalibrierung { AutomatikStufe = true, AutoWoche = "flower-w1", AutoStufe = 8, HandBisWoche = "flower-w1" });

        var vonOberflaeche = Beispiel();
        vonOberflaeche.AutomatikStufe = true;
        vonOberflaeche.AutoStufe = 1;       // darf die Oberfläche nicht überschreiben
        vonOberflaeche.AutoWoche = "x";
        dienst.Speichern(vonOberflaeche);

        var k = dienst.Kalibrierung;
        Assert.Equal("flower-w1", k.AutoWoche);
        Assert.Equal(8, k.AutoStufe);
        Assert.Equal("flower-w1", k.HandBisWoche);
    }

    [Fact]
    public void Ausschalten_loescht_die_Merker()
    {
        var dienst = Dienst();
        _repo.SetEinstellungen(LichtPlanService.Modul, new LichtKalibrierung { AutomatikStufe = true, AutoWoche = "flower-w1", AutoStufe = 8, HandBisWoche = "flower-w1" });
        var aus = Beispiel();
        aus.AutomatikStufe = false;
        dienst.Speichern(aus);

        var k = dienst.Kalibrierung;
        Assert.Null(k.AutoWoche);
        Assert.Null(k.AutoStufe);
        Assert.Null(k.HandBisWoche);
    }

    // ---------------------------------------------------------------- Befunde des Prüfers

    [Fact]
    public void Nur_ein_laufender_Grow_bestimmt_die_Phase_Planung_und_Mehrdeutiges_zaehlen_nicht()
    {
        GrowRun Grow(GrowStatus status) => new() { Status = status };
        Assert.Null(LichtPlanService.LaufenderGrow([]));
        Assert.Null(LichtPlanService.LaufenderGrow([Grow(GrowStatus.Planning)]));
        Assert.Null(LichtPlanService.LaufenderGrow([Grow(GrowStatus.Running), Grow(GrowStatus.Running)]));
        var laufend = Grow(GrowStatus.Running);
        Assert.Same(laufend, LichtPlanService.LaufenderGrow([Grow(GrowStatus.Planning), laufend]));
    }

    [Fact]
    public void Autoflower_bekommen_keine_Phase_untergeschoben()
    {
        var grow = new GrowRun { Status = GrowStatus.Running, SeedType = SeedType.Autoflower, StartDate = new DateTime(2026, 9, 1) };
        Assert.Null(LichtPlanService.PhaseFuerGrow(grow, new DateTime(2026, 11, 1)));
        Assert.Null(LichtPlanService.PhaseFuerGrow(null, new DateTime(2026, 11, 1)));
    }

    [Fact]
    public void Eine_fallende_Kurve_wird_abgelehnt()
    {
        var fallend = new LichtKalibrierung { Messpunkte = [new() { Stufe = 8, Ppfd = 900 }, new() { Stufe = 9, Ppfd = 850 }] };
        Assert.Contains(nameof(LichtKalibrierung.Messpunkte), LichtKennlinie.Pruefen(fallend).Keys);
        // Auch eine Herstellerangabe unter der Messung bei Stufe 8 macht die Kurve unsinnig.
        var zuNiedrig = new LichtKalibrierung { HerstellerPpfd = 500, Messpunkte = [new() { Stufe = 8, Ppfd = 900 }] };
        Assert.Contains(nameof(LichtKalibrierung.Messpunkte), LichtKennlinie.Pruefen(zuNiedrig).Keys);
    }

    [Fact]
    public void Die_Automatik_laesst_sich_ohne_Kurve_nicht_einschalten()
    {
        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(new LichtKalibrierung { AutomatikStufe = true });
        Assert.Null(gespeichert);
        Assert.Contains(nameof(LichtKalibrierung.AutomatikStufe), fehler.Keys);
        Assert.False(dienst.Kalibrierung.AutomatikStufe);
    }

    [Fact]
    public void Stufe_null_in_der_Dunkelphase_gilt_nicht_als_Handstellung()
    {
        var k = Beispiel();
        k.AutomatikStufe = true;
        k.AutoWoche = "flower-w1";
        k.AutoStufe = 8;
        Assert.Equal(LichtAutoEntscheidung.Nichts, LichtPlanService.Entscheiden(k, "flower-w1", 8, 0).Entscheidung);
    }
}
