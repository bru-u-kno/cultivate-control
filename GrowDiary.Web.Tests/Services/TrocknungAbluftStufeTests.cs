using GrowDiary.Web.Services;
using Xunit;

namespace GrowDiary.Web.Tests.Services;

public sealed class TrocknungAbluftStufeTests
{
    [Theory]
    [InlineData(1.0, TrocknungAbluftStufe.Aktion.Anheben, TrocknungAbluftStufe.ZustandHebtAn)]   // darunter: anheben
    [InlineData(0.0, TrocknungAbluftStufe.Aktion.Anheben, TrocknungAbluftStufe.ZustandHebtAn)]
    [InlineData(2.0, TrocknungAbluftStufe.Aktion.Nichts, TrocknungAbluftStufe.ZustandHaelt)]     // genau auf der Mindeststufe
    [InlineData(3.0, TrocknungAbluftStufe.Aktion.Nichts, TrocknungAbluftStufe.ZustandHaelt)]     // darüber: bleibt
    [InlineData(10.0, TrocknungAbluftStufe.Aktion.Nichts, TrocknungAbluftStufe.ZustandHaelt)]
    public void Anheben_nur_unter_der_Mindeststufe(double stufe, TrocknungAbluftStufe.Aktion erwartet, string zustand)
    {
        var e = TrocknungAbluftStufe.Entscheide(2, "On", stufe);
        Assert.Equal(erwartet, e.Aktion);
        Assert.Equal(zustand, e.Zustand);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Auto")]
    [InlineData("unavailable")]
    [InlineData(null)]
    public void Nur_im_Modus_On_wird_eine_Stufe_gesichert(string? modus)
    {
        var e = TrocknungAbluftStufe.Entscheide(2, modus, 0);
        Assert.Equal(TrocknungAbluftStufe.Aktion.Nichts, e.Aktion);
        Assert.Equal(TrocknungAbluftStufe.ZustandPortNichtOn, e.Zustand);
    }

    [Fact]
    public void Ohne_lesbare_Stufe_passiert_nichts()
    {
        Assert.Equal(TrocknungAbluftStufe.ZustandKeinWert, TrocknungAbluftStufe.Entscheide(2, "On", null).Zustand);
        Assert.Equal(TrocknungAbluftStufe.ZustandKeinWert, TrocknungAbluftStufe.Entscheide(2, "On", double.NaN).Zustand);
        Assert.Equal(TrocknungAbluftStufe.Aktion.Nichts, TrocknungAbluftStufe.Entscheide(2, "On", null).Aktion);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    public void Gueltig_ist_0_bis_10(int stufe, bool erwartet) => Assert.Equal(erwartet, TrocknungAbluftStufe.Gueltig(stufe));
}
