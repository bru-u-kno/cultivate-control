using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// cc.281: Eine Ernte kann in einem Trockenzelt (Zweck „Trocknung") hängen. Dann trocknet dieses Zelt sie — und das
/// Zelt des Grows ist ab der Ernte frei.
/// </summary>
public sealed class TrockenzeltErnteTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _paths;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly Tent _blueteZelt;
    private readonly Tent _trockenZelt;
    private static readonly DateTime Heute = new(2026, 10, 30);

    public TrockenzeltErnteTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrockenzeltErnte_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        _paths = new AppPaths(_wurzel);
        _blueteZelt = TestDatabase.InitializeWithDefaultTent(_paths);
        _grows = new GrowRepository(_paths);
        _ernten = new HarvestRepository(_paths);
        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private (int GrowId, int ErnteId) GeerntetVor(int tage, int? trockenzeltId)
    {
        var growId = _grows.CreateGrow(new GrowRun
        {
            Name = "Charge A", TentId = _blueteZelt.Id, StartDate = Heute.AddDays(-90),
            EndDate = Heute.AddDays(-tage), Status = GrowStatus.Completed,
        });
        var ernteId = _ernten.Create(new HarvestEntry { GrowId = growId, HarvestedAt = Heute.AddDays(-tage), WetWeightG = 500, DryingTentId = trockenzeltId });
        return (growId, ernteId);
    }

    [Fact]
    public void Das_Trockenzelt_zaehlt_ab_dem_Erntedatum_auch_ohne_Enddatum_und_bei_laufendem_Grow()
    {
        // Grow ohne Enddatum, abgeschlossen auf anderem Weg als über die Ernte.
        var ohneEnde = _grows.CreateGrow(new GrowRun { Name = "Ohne Ende", TentId = _blueteZelt.Id, StartDate = Heute.AddDays(-90), Status = GrowStatus.Completed });
        _ernten.Create(new HarvestEntry { GrowId = ohneEnde, HarvestedAt = Heute.AddDays(-1), DryingTentId = _trockenZelt.Id });
        Assert.Equal(2, DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute)!.Tag);

        // Teilernte: Der Grow läuft noch, die Ernte trocknet trotzdem im Trockenzelt.
        var teilTelt = _grows.CreateTent(new Tent { Name = "Trockenzelt 2", TentType = TentType.Drying });
        var laufend = _grows.CreateGrow(new GrowRun { Name = "Läuft", TentId = _blueteZelt.Id, StartDate = Heute.AddDays(-60), Status = GrowStatus.Running });
        _ernten.Create(new HarvestEntry { GrowId = laufend, HarvestedAt = Heute.AddDays(-4), DryingTentId = teilTelt.Id });
        var stand = DryingWindow.StandFor(_grows, _ernten, teilTelt.Id, Heute);
        Assert.NotNull(stand);
        Assert.Equal(laufend, stand!.Grow.Id);
        Assert.Equal(5, stand.Tag);
    }

    [Fact]
    public void Das_Trockenzelt_trocknet_die_Ernte_des_fremden_Grows()
    {
        var (growId, _) = GeerntetVor(3, _trockenZelt.Id);

        var stand = DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute);

        Assert.NotNull(stand);
        Assert.Equal(growId, stand!.Grow.Id);
        Assert.Equal(4, stand.Tag); // Erntetag + 3 → Trocknungstag 4
    }

    [Fact]
    public void Das_Zelt_des_Grows_ist_frei_sobald_die_Ernte_im_Trockenzelt_haengt()
    {
        GeerntetVor(3, _trockenZelt.Id);

        Assert.Null(DryingWindow.StandFor(_grows, _ernten, _blueteZelt.Id, Heute));
    }

    [Fact]
    public void Ohne_Trockenzelt_trocknet_die_Ernte_wie_bisher_im_Zelt_des_Grows()
    {
        GeerntetVor(3, null);

        Assert.NotNull(DryingWindow.StandFor(_grows, _ernten, _blueteZelt.Id, Heute));
        Assert.Null(DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute));
    }

    [Fact]
    public void Das_Trockenzelt_ist_fertig_mit_dem_Trockengewicht_und_nach_dem_Fenster()
    {
        var (growId, ernteId) = GeerntetVor(3, _trockenZelt.Id);
        Assert.NotNull(DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute));

        _ernten.Update(new HarvestEntry { Id = ernteId, GrowId = growId, WetWeightG = 500, DryWeightG = 110, DryingTentId = _trockenZelt.Id });
        Assert.Null(DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute));

        // Und nach dem Fenster (länger als die Trocknungsfrist) ebenfalls.
        var (_, spaeteErnte) = GeerntetVor(MoldGuard.DryingWindowDays + 5, _trockenZelt.Id);
        Assert.NotEqual(0, spaeteErnte);
        Assert.Null(DryingWindow.StandFor(_grows, _ernten, _trockenZelt.Id, Heute));
    }

    [Fact]
    public void Die_Ernte_merkt_sich_das_Trockenzelt_und_aendert_es()
    {
        var (growId, ernteId) = GeerntetVor(1, _trockenZelt.Id);
        Assert.Equal(_trockenZelt.Id, _ernten.GetForGrow(growId)!.DryingTentId);

        _ernten.Update(new HarvestEntry { Id = ernteId, GrowId = growId, WetWeightG = 500, DryingTentId = null });
        Assert.Null(_ernten.GetForGrow(growId)!.DryingTentId);
    }
}
