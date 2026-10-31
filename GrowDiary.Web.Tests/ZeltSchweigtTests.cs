using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests;

/// <summary>
/// Wann ein Zelt keine Alarme meldet (31.10.2026): nach der Ernte feuerte der Feuchte-Alarm weiter aufs Handy,
/// 77 % im leeren Zelt. Die Alarm-Regeln gehören dem Zelt, nicht dem Grow — und ein Zelt ohne Pflanzen hat nichts zu melden.
/// Dasselbe Zelt, in dem die Ernte hängt, meldet aber weiter: dort sind Feuchte und Temperatur genau das, worauf es ankommt.
/// </summary>
public sealed class ZeltSchweigtTests
{
    [Theory]
    // (von Hand ruhend, Zweck, laufende Grows, im Trocknungsfenster, aktive Pflanzen) → schweigt?
    [InlineData(true, TentType.Production, 0, false, false, true)]      // von Hand ruhend, nichts läuft darin
    [InlineData(true, TentType.Production, 0, false, true, true)]       // von Hand ruhend: gilt auch mit einer „aktiven“ Pflanze (die Ernte steht noch so)
    [InlineData(true, TentType.Mother, 0, false, false, true)]          // ruhend gilt für jeden Zweck, wenn kein Grow darin läuft
    [InlineData(true, TentType.Production, 1, false, true, false)]      // ruhend, aber ein Grow läuft: meldet — ein wachsender Grow darf nie am Alarm vorbei laufen
    [InlineData(false, TentType.Production, 0, false, false, true)]     // leeres Produktionszelt
    [InlineData(false, TentType.MultiPurpose, 0, false, false, true)]   // leeres Mehrzweckzelt (ein RDWC-Zelt nach der Ernte)
    [InlineData(false, TentType.Production, 1, false, false, false)]    // läuft ein Grow: meldet
    [InlineData(false, TentType.Production, 0, true, false, false)]     // trocknet im eigenen Zelt: meldet
    [InlineData(false, TentType.Production, 0, false, true, false)]     // aktive Pflanzen ohne Grow: meldet
    [InlineData(false, TentType.Drying, 0, false, false, false)]        // Trockenzelt: meldet
    [InlineData(false, TentType.Mother, 0, false, false, false)]        // Mutterzelt: dort stehen Pflanzen ohne Grow
    [InlineData(false, TentType.Propagation, 0, false, false, false)]
    [InlineData(false, TentType.Quarantine, 0, false, false, false)]
    public void SchweigtZelt_folgt_der_Regel(bool ruhend, TentType zweck, int grows, bool trocknung, bool pflanzen, bool erwartet)
        => Assert.Equal(erwartet, AlertEvaluationService.SchweigtZelt(ruhend, zweck, grows, trocknung, pflanzen));

    [Fact]
    public void Die_Regel_kennt_jeden_Zweck()
    {
        // Zählung über die Grundmenge: kommt ein neuer Zweck dazu, muss jemand entscheiden, ob sein leeres Zelt schweigt.
        var zwecke = Enum.GetValues<TentType>();
        Assert.True(zwecke.Length >= 6, "Die Zählung sieht ihre Grundmenge nicht.");
        var schweigenVonSelbst = zwecke.Where(z => AlertEvaluationService.SchweigtZelt(false, z, 0, false, false)).ToList();
        Assert.Equal([TentType.Production, TentType.MultiPurpose], schweigenVonSelbst);
    }
}
