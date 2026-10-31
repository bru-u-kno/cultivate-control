using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Fertig-Erkennung der Trocknung (A-029, Etappe 4): Kurve in Prozent, „sinkt / hält sich / Ziel“, nächste Wägung.
/// </summary>
/// <remarks>
/// Das Frischgewicht der ersten Charge ist nicht immer bekannt. Deshalb ist der Startwert (erste Wägung) der Normalfall; die
/// Zielmarke gibt es nur bei der ganzen Charge mit bekanntem Frischgewicht — eine Probe am Frischgewicht der ganzen Ernte
/// zu messen wäre falsch.
/// </remarks>
public sealed class FertigErkennungTests
{
    private static readonly DateTime Ernte = new(2026, 10, 30);

    /// <summary>Eine Wägung am Mittag des Tages <paramref name="tageNachErnte"/> (Ortszeit, damit keine Zeitzone kippt).</summary>
    private static TrocknungWaegung W(double tageNachErnte, double gramm, bool ganzeCharge = false, int id = 0)
        => new()
        {
            Id = id,
            GewichtG = gramm,
            GanzeCharge = ganzeCharge,
            ZeitpunktUtc = DateTime.SpecifyKind(Ernte.AddDays(tageNachErnte).AddHours(12), DateTimeKind.Local).ToUniversalTime(),
        };

    [Fact]
    public void Ohne_Waegung_gibt_es_nichts_zu_berechnen()
    {
        var e = FertigErkennung.Berechne([], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.KeineWaegung, e.Stufe);
        Assert.Empty(e.Punkte);
        Assert.Null(e.NaechsteAm);
    }

    [Fact]
    public void Die_erste_Waegung_ist_der_Startwert_und_die_naechste_kommt_nach_zwei_Tagen()
    {
        var e = FertigErkennung.Berechne([W(1, 75.6)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.ErsteWaegung, e.Stufe);
        Assert.False(e.BezugFrischgewicht);
        Assert.Equal(100, e.Punkte[0].Prozent, 6);
        Assert.Equal(2, e.Punkte[0].Tag); // Erntetag + 1 → Trocknungstag 2, wie DryingWindow
        Assert.Equal(Ernte.AddDays(3), e.NaechsteAm);
    }

    [Fact]
    public void Das_Gewicht_sinkt_noch_solange_es_deutlich_faellt()
    {
        var e = FertigErkennung.Berechne([W(1, 100), W(3, 81), W(5, 66), W(7, 55)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, e.Stufe);
        Assert.Equal(55, e.Punkte[^1].Prozent, 6);
        Assert.Equal(-11, e.AenderungPunkte!.Value, 6);
    }

    [Fact]
    public void Es_haelt_sich_erst_wenn_zwei_aufeinanderfolgende_Wagetage_unter_einem_Prozent_liegen()
    {
        var eins = FertigErkennung.Berechne([W(1, 100), W(3, 81), W(5, 66), W(7, 55), W(9, 54.6)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, eins.Stufe); // nur ein ruhiges Intervall

        var zwei = FertigErkennung.Berechne([W(1, 100), W(3, 81), W(5, 66), W(7, 55), W(9, 54.6), W(11, 54.3)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.HaeltSich, zwei.Stufe);
    }

    [Fact]
    public void Zwei_Waegungen_am_selben_Tag_ergeben_keinen_Stillstand()
    {
        // Vormittag und Abend, fast gleich — das ist Waagen-Rauschen, kein Trend.
        var e = FertigErkennung.Berechne([W(1, 100), W(3, 70), W(5, 60, id: 1), W(5.2, 59.9, id: 2)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, e.Stufe);
    }

    [Fact]
    public void Ganze_Charge_mit_Frischgewicht_rechnet_gegen_das_Frischgewicht_und_kennt_ein_Ziel()
    {
        var e = FertigErkennung.Berechne([W(1, 620, true), W(3, 410, true)], 1000, Ernte);
        Assert.True(e.BezugFrischgewicht);
        Assert.Equal(1000, e.ReferenzG);
        Assert.Equal(62, e.Punkte[0].Prozent, 6);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, e.Stufe);

        var ziel = FertigErkennung.Berechne([W(1, 620, true), W(3, 410, true), W(9, 215, true)], 1000, Ernte);
        Assert.Equal(FertigErkennung.Stufe.ZielErreicht, ziel.Stufe);
    }

    [Fact]
    public void Eine_Probe_bleibt_beim_Startwert_auch_wenn_das_Frischgewicht_bekannt_ist()
    {
        var e = FertigErkennung.Berechne([W(1, 75.6), W(3, 61.2)], 1240, Ernte);
        Assert.False(e.BezugFrischgewicht);
        Assert.Equal(75.6, e.ReferenzG);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, e.Stufe);
    }

    [Fact]
    public void Zwei_Waegungen_am_selben_Tag_sind_erst_eine_Wägung_ueber_die_Zeit()
    {
        var e = FertigErkennung.Berechne([W(1, 75.6, id: 1), W(1.1, 75.6, id: 2)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.ErsteWaegung, e.Stufe);
        Assert.Null(e.AenderungPunkte);
    }

    [Fact]
    public void Wird_es_schwerer_ist_die_Aenderung_positiv_und_die_Stufe_nicht_Hält_sich()
    {
        var e = FertigErkennung.Berechne([W(1, 100), W(3, 80), W(5, 82)], null, Ernte);
        Assert.Equal(FertigErkennung.Stufe.Sinkt, e.Stufe);
        Assert.Equal(2, e.AenderungPunkte!.Value, 6);
    }

    [Fact]
    public void Ein_Erntedatum_nach_der_Waegung_ergibt_hoechstens_Tag_eins_statt_eines_negativen_Tages()
    {
        var e = FertigErkennung.Berechne([W(-2, 80)], null, Ernte);
        Assert.Equal(1, e.Punkte[0].Tag);
    }
}
