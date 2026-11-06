using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>Die Zuluft am Trockenzelt folgt dem Trocknungsplan (Entscheidung ohne Home Assistant).</summary>
public sealed class TrocknungZuluftTests
{
    private static readonly TrocknungZuluft.Regel Regel = new(
        MindestDifferenzGm3: 1.2, AbschaltAbstandGm3: 0.2, AbstandUnterPlanProzent: 3,
        ZeltTempMinC: 15, AussenTempMinC: 2, StufeMin: 1, Hoechststufe: 4);

    // Zelt feuchter als der Plan (60 %), Außenluft trockener.
    private static TrocknungZuluft.Lage Lage(double zeltTemp = 17.1, double zeltRh = 64, double aussenAbs = 6.7, double aussenTemp = 8, double plan = 60)
        => new(zeltTemp, zeltRh, aussenAbs, aussenTemp, plan);

    [Fact]
    public void SaugtWennDasZeltFeuchterIstAlsDerPlanUndDieAussenluftTrockener()
    {
        var e = TrocknungZuluft.Entscheide(Lage(), Regel, saugteBisher: false);
        Assert.Equal(TrocknungZuluft.ZustandSaugt, e.Zustand);
        Assert.True(e.Saugt);
        Assert.InRange(e.Stufe, 1, 4);
        Assert.InRange(e.Differenz!.Value, 2.5, 2.7);
    }

    [Fact]
    public void AbsoluteFeuchteIstDieMagnusFormelWieInHomeAssistant()
    {
        // 20 °C und 50 % tragen etwa 8,6 g/m³.
        Assert.InRange(TrocknungZuluft.Absolut(20, 50)!.Value, 8.5, 8.8);
        Assert.Null(TrocknungZuluft.Absolut(null, 50));
        Assert.Null(TrocknungZuluft.Absolut(20, double.NaN));
    }

    [Fact]
    public void OhneMesswertBleibtSieAus()
    {
        foreach (var lage in new[]
                 {
                     Lage() with { ZeltTempC = null }, Lage() with { ZeltRhProzent = null }, Lage() with { AussenAbsolutGm3 = null },
                     Lage() with { AussenTempC = null }, Lage() with { PlanRhProzent = null },
                 })
        {
            var e = TrocknungZuluft.Entscheide(lage, Regel, saugteBisher: true);
            Assert.Equal(0, e.Stufe);
            Assert.Equal(TrocknungZuluft.ZustandKeinMesswert, e.Zustand);
        }
    }

    [Fact]
    public void ZuKaltImZeltSperrtMitEinGradHysterese()
    {
        // Unter 15 °C aus; wieder frei erst ab 16 °C.
        Assert.Equal(TrocknungZuluft.ZustandZeltZuKalt, TrocknungZuluft.Entscheide(Lage(zeltTemp: 14.5), Regel, true).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandZeltZuKalt, TrocknungZuluft.Entscheide(Lage(zeltTemp: 15.5), Regel, false).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(zeltTemp: 15.5), Regel, true).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(zeltTemp: 16.2), Regel, false).Zustand);
    }

    [Fact]
    public void FrostDraussenSperrtMitEinGradHysterese()
    {
        Assert.Equal(TrocknungZuluft.ZustandFrost, TrocknungZuluft.Entscheide(Lage(aussenTemp: 1.5), Regel, true).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandFrost, TrocknungZuluft.Entscheide(Lage(aussenTemp: 2.5), Regel, false).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(aussenTemp: 3.5), Regel, false).Zustand);
    }

    [Fact]
    public void ZuTrockenImZeltBleibtSieAus()
    {
        // Plan 60 %, Abstand 3: unter 57 % aus.
        var e = TrocknungZuluft.Entscheide(Lage(zeltRh: 56), Regel, saugteBisher: true);
        Assert.Equal(0, e.Stufe);
        Assert.Equal(TrocknungZuluft.ZustandZeltZuTrocken, e.Zustand);
    }

    [Fact]
    public void SpringtKnappVorDemEntfeuchterAn()
    {
        // Der Entfeuchter schaltet ab 60 %; die Zuluft ab 59 %.
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(zeltRh: 59), Regel, false).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandZeltImPlan, TrocknungZuluft.Entscheide(Lage(zeltRh: 58.5), Regel, false).Zustand);
        // Läuft sie schon, bleibt sie im Haltebereich (58,5 % ist nicht unter 57 %).
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(zeltRh: 58.5), Regel, true).Zustand);
    }

    [Fact]
    public void FeuchtereAussenluftBleibtAusMitHysterese()
    {
        // Zelt 64 % bei 17,1 °C ≈ 9,3 g/m³. Außen 8,5: Differenz ≈ 0,8 — unter der Mindest-Differenz 1,2.
        Assert.Equal(TrocknungZuluft.ZustandAussenFeuchter, TrocknungZuluft.Entscheide(Lage(aussenAbs: 8.5), Regel, false).Zustand);
        // Läuft sie schon, hält sie bis Mindest-Differenz minus Abschalt-Abstand (1,0): Differenz ≈ 1,1 → weiter.
        Assert.Equal(TrocknungZuluft.ZustandSaugt, TrocknungZuluft.Entscheide(Lage(aussenAbs: 8.2), Regel, true).Zustand);
        Assert.Equal(TrocknungZuluft.ZustandAussenFeuchter, TrocknungZuluft.Entscheide(Lage(aussenAbs: 8.4), Regel, true).Zustand);
    }

    [Fact]
    public void DieStufeWaechstMitDerDifferenzUndDeckeltAnDerHoechststufe()
    {
        Assert.Equal(1, TrocknungZuluft.StufeFuer(1.2, Regel));
        Assert.Equal(4, TrocknungZuluft.StufeFuer(4.2, Regel));
        Assert.Equal(4, TrocknungZuluft.StufeFuer(20, Regel));
        Assert.Equal(3, TrocknungZuluft.StufeFuer(2.7, Regel with { Hoechststufe = 5, StufeMin = 1 }));
        // Höchststufe unter Stufe min: die Höchststufe gilt (nie darüber).
        Assert.Equal(2, TrocknungZuluft.StufeFuer(9, Regel with { StufeMin = 3, Hoechststufe = 2 }));
    }
}
