using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Die Entscheidungen des Controller-Wächters. Anlass: Der Controller des Trockenzelts fiel am 08.11.2026 für 3 Std. 48 Min. aus,
/// der Feuchtewert stand still auf 54,0 %, im Zelt waren es danach 68,6 %.
/// </summary>
public sealed class ControllerWaechterRegelnTests
{
    private static readonly DateTime T0 = new(2026, 11, 8, 10, 54, 0, DateTimeKind.Utc);

    private static ControllerWaechterEintrag Eintrag(string modus = ControllerWaechterEintrag.Neustarten)
        => new() { Id = "a", Name = "Trockenzelt", StatusEntity = "binary_sensor.x_status", Modus = modus };

    private static ControllerLage Offline(DateTime seit, bool dose = true) => new(ControllerBefund.Offline, seit, dose);

    private static IReadOnlyList<ControllerAktion> Schritt(ControllerWaechterEintrag e, ControllerLage l, ControllerMerker m, int minuten)
        => ControllerWaechterRegeln.Entscheiden(e, l, m, T0.AddMinutes(minuten));

    // ------------------------------------------------------------- Beurteilen

    [Fact]
    public void Verbunden_und_frische_Feuchte_ist_gesund()
    {
        var (b, _) = ControllerWaechterRegeln.Beurteilen(true, "on", T0, "54.0", T0.AddMinutes(30), T0.AddMinutes(30));
        Assert.Equal(ControllerBefund.Gesund, b);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("unavailable")]
    [InlineData("unknown")]
    public void Status_aus_ist_ein_Ausfall_seit_dem_Wechsel(string status)
    {
        var (b, seit) = ControllerWaechterRegeln.Beurteilen(true, status, T0, "54.0", T0, T0.AddMinutes(20));
        Assert.Equal(ControllerBefund.Offline, b);
        Assert.Equal(T0, seit);
    }

    [Fact]
    public void Status_an_aber_Feuchte_steht_still_ist_eingefroren()
    {
        var (b, seit) = ControllerWaechterRegeln.Beurteilen(true, "on", T0, "54.0", T0, T0.AddMinutes(16));
        Assert.Equal(ControllerBefund.Eingefroren, b);
        Assert.Equal(T0, seit);
    }

    [Fact]
    public void Vierzehn_Minuten_Stillstand_ist_noch_kein_Befund()
    {
        var (b, _) = ControllerWaechterRegeln.Beurteilen(true, "on", T0, "54.0", T0, T0.AddMinutes(14));
        Assert.Equal(ControllerBefund.Gesund, b);
    }

    [Fact]
    public void Nicht_verfuegbare_Feuchte_zaehlt_als_Ausfall()
    {
        var (b, _) = ControllerWaechterRegeln.Beurteilen(true, "on", T0, "unavailable", T0, T0.AddMinutes(1));
        Assert.Equal(ControllerBefund.Offline, b);
    }

    [Fact]
    public void Ohne_Home_Assistant_wird_nichts_beurteilt()
    {
        var (b, seit) = ControllerWaechterRegeln.Beurteilen(false, null, null, null, null, T0);
        Assert.Equal(ControllerBefund.NichtPruefbar, b);
        Assert.Null(seit);
    }

    [Fact]
    public void Ohne_zugeordnete_Rollen_ist_nichts_pruefbar()
    {
        var (b, _) = ControllerWaechterRegeln.Beurteilen(true, null, null, null, null, T0);
        Assert.Equal(ControllerBefund.NichtPruefbar, b);
    }

    [Fact]
    public void Nur_mit_Feuchtefuehler_wird_der_Stillstand_trotzdem_gefunden()
    {
        var (b, _) = ControllerWaechterRegeln.Beurteilen(true, null, null, "54.0", T0, T0.AddMinutes(20));
        Assert.Equal(ControllerBefund.Eingefroren, b);
    }

    // ----------------------------------------------------------- Entscheiden

    [Fact]
    public void Kurzer_Aussetzer_bleibt_still()
    {
        var m = new ControllerMerker();
        Assert.Empty(Schritt(Eintrag(), Offline(T0), m, 1));
        Assert.Empty(Schritt(Eintrag(), Offline(T0), m, 9));
        var wieder = Schritt(Eintrag(), new ControllerLage(ControllerBefund.Gesund, null, true), m, 10);
        Assert.Empty(wieder); // nie gemeldet, also auch keine Entwarnung
    }

    [Fact]
    public void Nach_zehn_Minuten_kommt_die_Meldung_genau_einmal()
    {
        var m = new ControllerMerker();
        Assert.Equal(new[] { ControllerAktion.MeldenAusfall }, Schritt(Eintrag(ControllerWaechterEintrag.NurMelden), Offline(T0), m, 10));
        Assert.Empty(Schritt(Eintrag(ControllerWaechterEintrag.NurMelden), Offline(T0), m, 11));
        Assert.Empty(Schritt(Eintrag(ControllerWaechterEintrag.NurMelden), Offline(T0), m, 300));
    }

    [Fact]
    public void Der_Ausfall_beginnt_beim_Zustandswechsel_nicht_beim_ersten_Blick()
    {
        // Die App startet 20 Minuten nach dem Ausfall neu: Es ist schon überfällig, nicht erst in 10 Minuten.
        var m = new ControllerMerker();
        Assert.Contains(ControllerAktion.MeldenAusfall, Schritt(Eintrag(ControllerWaechterEintrag.NurMelden), Offline(T0), m, 20));
    }

    [Fact]
    public void Im_Modus_Nur_melden_wird_nie_neu_gestartet()
    {
        var m = new ControllerMerker();
        for (var min = 0; min <= 600; min += 5)
            Assert.DoesNotContain(ControllerAktion.Neustarten, Schritt(Eintrag(ControllerWaechterEintrag.NurMelden), Offline(T0), m, min));
    }

    [Fact]
    public void Ohne_schaltbare_Steckdose_wird_nie_neu_gestartet()
    {
        var m = new ControllerMerker();
        for (var min = 0; min <= 600; min += 5)
            Assert.DoesNotContain(ControllerAktion.Neustarten, Schritt(Eintrag(), Offline(T0, dose: false), m, min));
        Assert.Empty(m.NeustartsUtc);
    }

    [Fact]
    public void Neustart_erst_nach_der_Neustart_Wartezeit_und_nur_einmal_je_Kontrollfenster()
    {
        var e = Eintrag();
        var m = new ControllerMerker();
        Assert.DoesNotContain(ControllerAktion.Neustarten, Schritt(e, Offline(T0), m, 14));
        Assert.Contains(ControllerAktion.Neustarten, Schritt(e, Offline(T0), m, 15));
        // Innerhalb der fünf Minuten Kontrolle: nichts, schon gar kein zweiter Neustart.
        Assert.Empty(Schritt(e, Offline(T0), m, 16));
        Assert.Empty(Schritt(e, Offline(T0), m, 19));
    }

    [Fact]
    public void Hilft_der_Neustart_nicht_kommt_eine_Meldung_und_dann_eine_Stunde_Ruhe()
    {
        var e = Eintrag();
        var m = new ControllerMerker();
        Schritt(e, Offline(T0), m, 15);
        Assert.Equal(new[] { ControllerAktion.MeldenKeineHilfe }, Schritt(e, Offline(T0), m, 20));
        Assert.Empty(Schritt(e, Offline(T0), m, 21));
        Assert.Empty(Schritt(e, Offline(T0), m, 74));
        Assert.Contains(ControllerAktion.Neustarten, Schritt(e, Offline(T0), m, 75));
    }

    [Fact]
    public void Nach_der_Tagesgrenze_wird_nur_noch_gemeldet_nie_wieder_geschaltet()
    {
        var e = Eintrag();
        var m = new ControllerMerker();
        Schritt(e, Offline(T0), m, 15);
        Schritt(e, Offline(T0), m, 75);
        Assert.Equal(2, m.NeustartsUtc.Count);
        var spaeter = Schritt(e, Offline(T0), m, 140);
        Assert.Equal(new[] { ControllerAktion.MeldenLimit }, spaeter.Where(a => a == ControllerAktion.MeldenLimit).ToArray());
        Assert.DoesNotContain(ControllerAktion.Neustarten, spaeter);
        for (var min = 141; min < 600; min += 7)
            Assert.DoesNotContain(ControllerAktion.Neustarten, Schritt(e, Offline(T0), m, min));
        Assert.Equal(2, m.NeustartsUtc.Count);
    }

    [Fact]
    public void Nach_vierundzwanzig_Stunden_zaehlt_ein_alter_Neustart_nicht_mehr()
    {
        var e = Eintrag();
        var m = new ControllerMerker { NeustartsUtc = { T0.AddMinutes(-1500), T0.AddMinutes(-1490) } };
        Assert.Contains(ControllerAktion.Neustarten, Schritt(e, Offline(T0), m, 15));
        Assert.Single(m.NeustartsUtc);
    }

    [Fact]
    public void Kommt_der_Controller_wieder_gibt_es_die_Entwarnung_und_der_Merker_ist_frisch()
    {
        var e = Eintrag();
        var m = new ControllerMerker();
        Schritt(e, Offline(T0), m, 15);
        var wieder = Schritt(e, new ControllerLage(ControllerBefund.Gesund, null, true), m, 22);
        Assert.Equal(new[] { ControllerAktion.MeldenWieder }, wieder);
        Assert.Null(m.AusfallSeitUtc);
        Assert.False(m.Gemeldet);
        Assert.Equal(0, m.NeustartsImAusfall);
        Assert.Single(m.NeustartsUtc); // die Tagesgrenze bleibt
        Assert.Empty(Schritt(e, new ControllerLage(ControllerBefund.Gesund, null, true), m, 23));
    }

    [Fact]
    public void Nicht_pruefbar_aendert_nichts_und_schaltet_nichts()
    {
        var m = new ControllerMerker { AusfallSeitUtc = T0, Gemeldet = true };
        var a = Schritt(Eintrag(), new ControllerLage(ControllerBefund.NichtPruefbar, null, true), m, 300);
        Assert.Empty(a);
        Assert.Equal(T0, m.AusfallSeitUtc);
        Assert.True(m.Gemeldet);
    }

    [Theory]
    [InlineData(0, "0 Min.")]
    [InlineData(25, "25 Min.")]
    [InlineData(228, "3 Std. 48 Min.")]
    public void Dauer_steht_im_Klartext(int minuten, string erwartet)
        => Assert.Equal(erwartet, ControllerWaechterRegeln.Dauer(TimeSpan.FromMinutes(minuten)));
}
