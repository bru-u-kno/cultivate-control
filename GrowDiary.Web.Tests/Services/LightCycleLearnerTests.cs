using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Den Lichtzyklus aus den beobachteten Schaltvorgängen lesen.
/// </summary>
/// <remarks>
/// Cultivate Control zeichnet jede An/Aus-Flanke ohnehin auf — daraus ergibt sich der
/// Zyklus von selbst. Beobachtete Flanken sind dabei die bessere Uhr als jeder
/// eingetragene Plan: eine falsche Zeitzone geht daneben, ein gemessener
/// Einschaltzeitpunkt nie.
/// </remarks>
public sealed class LightCycleLearnerTests
{
    private static readonly DateTime Tag0 = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Ein Zyklus über mehrere Tage: an um <paramref name="anStunde"/>, so viele Stunden lang.</summary>
    private static List<LightTransitionEvent> Zyklus(int tage, double anStunde, double stundenAn)
    {
        var events = new List<LightTransitionEvent>();
        for (var tag = 0; tag < tage; tag++)
        {
            var an = Tag0.AddDays(tag).AddHours(anStunde);
            events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOn, OccurredAtUtc = an });
            events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOff, OccurredAtUtc = an.AddHours(stundenAn) });
        }
        return events;
    }

    [Fact]
    public void AVegCycleIsRecognisedAsEighteenSix()
    {
        var cycle = LightCycleLearner.Learn(Zyklus(4, 6, 18), TimeSpan.Zero)!;

        Assert.Equal(18, cycle.HoursOn);
        Assert.Equal("18/6", cycle.Label);
        Assert.Equal(new TimeOnly(6, 0), cycle.OnAt);
        Assert.True(cycle.LooksLikeVeg);
        Assert.False(cycle.LooksLikeFlower);
    }

    [Fact]
    public void AFlowerCycleIsRecognisedAsTwelveTwelve()
    {
        var cycle = LightCycleLearner.Learn(Zyklus(4, 8, 12), TimeSpan.Zero)!;

        Assert.Equal("12/12", cycle.Label);
        Assert.True(cycle.LooksLikeFlower);
    }

    [Fact]
    public void ASingleLongPhaseDoesNotShiftTheResult()
    {
        // Jemand hat zum Giessen das Licht angelassen. Der Median haelt dagegen,
        // ein Mittelwert nicht.
        var events = Zyklus(4, 6, 18);
        events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOn, OccurredAtUtc = Tag0.AddDays(5).AddHours(6) });
        events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOff, OccurredAtUtc = Tag0.AddDays(5).AddHours(28) });

        Assert.Equal("18/6", LightCycleLearner.Learn(events, TimeSpan.Zero)!.Label);
    }

    [Fact]
    public void OneDayIsNotEnoughToClaimACycle()
    {
        // Nach dem Mappen dauert es ein paar Tage — und bis dahin wird nichts
        // behauptet.
        Assert.Null(LightCycleLearner.Learn(Zyklus(1, 6, 18), TimeSpan.Zero));
        Assert.Null(LightCycleLearner.Learn([], TimeSpan.Zero));
    }

    [Fact]
    public void FlickeringIsNotACycle()
    {
        // Unter einer Stunde ist ein Schaltflattern oder jemand, der kurz
        // nachgesehen hat.
        Assert.Null(LightCycleLearner.Learn(Zyklus(4, 6, 0.5), TimeSpan.Zero));
    }

    [Fact]
    public void TheLocalOffsetOnlyMovesTheClockNotTheDuration()
    {
        var utc = LightCycleLearner.Learn(Zyklus(3, 6, 18), TimeSpan.Zero)!;
        var lokal = LightCycleLearner.Learn(Zyklus(3, 6, 18), TimeSpan.FromHours(2))!;

        Assert.Equal(utc.HoursOn, lokal.HoursOn);
        Assert.Equal(new TimeOnly(8, 0), lokal.OnAt);
    }

    // ---------- Der eigentliche Nutzen: der Abgleich ----------

    [Fact]
    public void TwelveTwelveInVegMeansTheFlipWasNotRecorded()
    {
        var cycle = LightCycleLearner.Learn(Zyklus(3, 8, 12), TimeSpan.Zero)!;

        var hinweis = LightCycleLearner.Mismatch(cycle, GrowStage.Veg, SeedType.Feminized);

        Assert.NotNull(hinweis);
        Assert.Contains("Flip", hinweis);
    }

    [Fact]
    public void EighteenSixInFlowerIsAControllerProblem()
    {
        // Das kostet die Ernte: 18 h Licht verhindert die Bluete.
        var cycle = LightCycleLearner.Learn(Zyklus(3, 6, 18), TimeSpan.Zero)!;

        var hinweis = LightCycleLearner.Mismatch(cycle, GrowStage.Flower, SeedType.Feminized);

        Assert.NotNull(hinweis);
        Assert.Contains("verhindert die Blüte", hinweis);
    }

    [Fact]
    public void AMatchingCycleSaysNothing()
    {
        var veg = LightCycleLearner.Learn(Zyklus(3, 6, 18), TimeSpan.Zero)!;
        var bluete = LightCycleLearner.Learn(Zyklus(3, 8, 12), TimeSpan.Zero)!;

        Assert.Null(LightCycleLearner.Mismatch(veg, GrowStage.Veg, SeedType.Feminized));
        Assert.Null(LightCycleLearner.Mismatch(bluete, GrowStage.Flower, SeedType.Feminized));
    }

    [Fact]
    public void AnAutoflowerIsNeverMismatched()
    {
        // Eine Autoflower blueht bei jedem Zyklus — 18/6 in der Bluete ist dort
        // voellig normal und darf keinen Hinweis ausloesen.
        var cycle = LightCycleLearner.Learn(Zyklus(3, 6, 18), TimeSpan.Zero)!;

        Assert.Null(LightCycleLearner.Mismatch(cycle, GrowStage.Flower, SeedType.Autoflower));
    }

    /// <summary>
    /// Zeitumstellung (25.10.2026): Flanken vor und nach der Umstellung liegen in UTC eine Stunde auseinander,
    /// zeigen aber dieselbe Uhrzeit im Zelt. Mit dem heutigen Versatz für alle kam „07:00“ statt „08:00“.
    /// </summary>
    [Fact]
    public void DieUhrzeitBleibtBeiDerZeitumstellungStehen()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var events = new List<LightTransitionEvent>();
        for (var tag = 21; tag <= 26; tag++)
        {
            // Im Zelt immer 08:00 bis 20:00 Ortszeit; Umstellung in der Nacht auf den 25.10.
            var an = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, tag, 8, 0, 0), berlin);
            var aus = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, tag, 20, 0, 0), berlin);
            events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOn, OccurredAtUtc = an });
            events.Add(new LightTransitionEvent { Kind = LightTransitionKind.LightOff, OccurredAtUtc = aus });
        }

        var heute = berlin.GetUtcOffset(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
        var alt = LightCycleLearner.Learn(events, heute)!;
        var neu = LightCycleLearner.Learn(events, utc => berlin.GetUtcOffset(utc))!;

        Assert.NotEqual(new TimeOnly(8, 0), alt.OnAt); // die alte Rechnung lag daneben: der Test beißt
        Assert.Equal(new TimeOnly(8, 0), neu.OnAt);
        Assert.Equal(new TimeOnly(20, 0), neu.OffAt);
    }
}
