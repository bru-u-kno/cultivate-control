using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Volumen, Grundfläche und Luftwechsel eines Zeltes kommen aus EINER Rechnung (<see cref="ZeltMasse"/>).
/// </summary>
public sealed class ZeltMasseTests
{
    [Theory]
    [InlineData(240, 120, 200, 5.76)]
    [InlineData(120, 120, 200, 2.88)]
    [InlineData(80, 80, 180, 1.15)]    // 1,152 m³ → zwei Stellen
    [InlineData(60, 60, 160, 0.58)]    // unter der Helfer-Spanne: gerechnet wird trotzdem
    public void Volumen_AusBreiteTiefeHoehe(int b, int t, int h, double erwartet)
        => Assert.Equal(erwartet, ZeltMasse.VolumenM3(b, t, h));

    [Theory]
    [InlineData(null, 120, 200)]
    [InlineData(240, null, 200)]
    [InlineData(240, 120, null)]
    [InlineData(0, 120, 200)]
    [InlineData(240, -1, 200)]
    public void Volumen_FehltEinMass_IstNull(int? b, int? t, int? h)
        => Assert.Null(ZeltMasse.VolumenM3(b, t, h));

    [Fact]
    public void Grundflaeche_IstUngerundet_DamitFlaecheJePflanzeNichtAnDerRundungHaengt()
    {
        Assert.Equal(2.88, ZeltMasse.GrundflaecheM2(240, 120));
        Assert.Equal(1.0625, ZeltMasse.GrundflaecheM2(125, 85)); // zwei Stellen wären 1,06
        Assert.Null(ZeltMasse.GrundflaecheM2(240, null));
    }

    [Theory]
    [InlineData(5.76, 683, 119)]
    [InlineData(2.88, 348, 121)]
    public void Luftwechsel_IstAbluftDurchVolumen(double volumen, int abluft, double erwartet)
        => Assert.Equal(erwartet, ZeltMasse.LuftwechselProStunde(volumen, abluft));

    [Fact]
    public void Luftwechsel_OhneVolumenOderAbluft_IstNull()
    {
        Assert.Null(ZeltMasse.LuftwechselProStunde(null, 683));
        Assert.Null(ZeltMasse.LuftwechselProStunde(5.76, null));
        Assert.Null(ZeltMasse.LuftwechselProStunde(5.76, 0));
        Assert.Null(ZeltMasse.LuftwechselProStunde(0, 683));
    }
}
