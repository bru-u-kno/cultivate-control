using GrowDiary.Web.Services;
using Xunit;

namespace GrowDiary.Web.Tests.Services;

public sealed class TrocknungSchaltenTests
{
    private static readonly DateTime Jetzt = new(2026, 11, 6, 17, 0, 0, DateTimeKind.Utc);
    private static TrocknungSchalten.Entscheidung E(double? feuchte, string? modus, DateTime? zuletzt = null)
        => TrocknungSchalten.Entscheide(feuchte, modus, 58, 56, zuletzt, Jetzt);

    [Theory]
    [InlineData(58.0, "Off", TrocknungSchalten.Aktion.Einschalten)]   // genau am Ziel: ein
    [InlineData(57.9, "Off", TrocknungSchalten.Aktion.Nichts)]        // knapp darunter: bleibt aus
    [InlineData(57.0, "On", TrocknungSchalten.Aktion.Nichts)]         // in der Hysterese: bleibt an
    [InlineData(56.0, "On", TrocknungSchalten.Aktion.Nichts)]         // genau am Rand: noch an
    [InlineData(55.9, "On", TrocknungSchalten.Aktion.Ausschalten)]    // darunter: aus
    [InlineData(70.0, "On", TrocknungSchalten.Aktion.Nichts)]         // läuft schon
    [InlineData(40.0, "Off", TrocknungSchalten.Aktion.Nichts)]        // steht schon
    public void Hysterese(double feuchte, string modus, TrocknungSchalten.Aktion erwartet)
        => Assert.Equal(erwartet, E(feuchte, modus).Aktion);

    [Theory]
    [InlineData("Auto")]
    [InlineData("unavailable")]
    [InlineData("VPD")]
    [InlineData(null)]
    public void Nur_On_und_Off_werden_geschaltet(string? modus)
    {
        var e = E(70, modus);
        Assert.Equal(TrocknungSchalten.Aktion.Nichts, e.Aktion);
        Assert.Equal(TrocknungSchalten.ZustandPortNichtSchaltbar, e.Zustand);
    }

    [Fact]
    public void Ohne_Messwert_passiert_nichts()
    {
        Assert.Equal(TrocknungSchalten.ZustandKeinMesswert, E(null, "On").Zustand);
        Assert.Equal(TrocknungSchalten.ZustandKeinMesswert, E(double.NaN, "Off").Zustand);
        Assert.Equal(TrocknungSchalten.Aktion.Nichts, E(null, "Off").Aktion);
    }

    [Fact]
    public void Die_Mindestpause_haelt_den_Schaltvorgang_zurueck()
    {
        Assert.Equal(TrocknungSchalten.ZustandWartet, E(70, "Off", Jetzt.AddMinutes(-2)).Zustand);
        Assert.Equal(TrocknungSchalten.Aktion.Nichts, E(70, "Off", Jetzt.AddMinutes(-2)).Aktion);
        Assert.Equal(TrocknungSchalten.Aktion.Einschalten, E(70, "Off", Jetzt.AddMinutes(-4)).Aktion);
    }

    [Fact]
    public void Schwellen_haben_immer_einen_Abstand()
    {
        Assert.Equal((58.0, 56.0), TrocknungSchalten.Schwellen(58, 56));
        Assert.Equal((55.0, 54.0), TrocknungSchalten.Schwellen(55, 55)); // Band ohne Abstand: ein Punkt Hysterese
    }
}
