using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;

namespace GrowDiary.Web.Tests.Infrastructure;

/// <summary>
/// CO₂-Tage je Zelt: das Bestand-Zelt bleibt in der alten Tabelle, weitere Zelte haben eine eigene — am selben Datum ohne Kollision.
/// </summary>
public sealed class SteuerungRepositoryCo2ZeltTests : IDisposable
{
    private readonly string _wurzel = Path.Combine(Path.GetTempPath(), "Co2TageZelt_" + Guid.NewGuid().ToString("N"));
    private readonly SteuerungRepository _repo;

    public SteuerungRepositoryCo2ZeltTests()
    {
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(pfade);
        _repo = new SteuerungRepository(pfade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private static Co2Tag Tag(string datum, int impulse, int? zelt = null)
        => new() { Datum = datum, Impulse = impulse, ZeltId = zelt, FlascheStartKg = 8 };

    [Fact]
    public void ZweiZelteAmSelbenTag_KollidierenNicht_UndSehenNurIhreEigenen()
    {
        _repo.CreateCo2Tag(Tag("2026-11-02", 5));
        _repo.CreateCo2Tag(Tag("2026-11-02", 9, zelt: 2));
        _repo.CreateCo2Tag(Tag("2026-11-02", 1, zelt: 3));

        Assert.Equal(5, _repo.GetCo2Tag("2026-11-02")!.Impulse);
        Assert.Null(_repo.GetCo2Tag("2026-11-02")!.ZeltId);
        Assert.Equal(9, _repo.GetCo2Tag("2026-11-02", 2)!.Impulse);
        Assert.Equal(2, _repo.GetCo2Tag("2026-11-02", 2)!.ZeltId);
        Assert.Equal(1, _repo.GetCo2Tag("2026-11-02", 3)!.Impulse);
        Assert.Null(_repo.GetCo2Tag("2026-11-02", 4));

        // Jede Tabelle zählt ihre Ids für sich: gültig ist (Zelt, Id), deshalb ruft der Dienst immer mit dem Zelt auf.
        Assert.Single(_repo.GetCo2Tage(10));
        Assert.Single(_repo.GetCo2Tage(10, 2));
    }

    [Fact]
    public void Aendern_TrifftNurDieZeileDesZeltes()
    {
        _repo.CreateCo2Tag(Tag("2026-11-02", 5));
        var z = Tag("2026-11-02", 9, zelt: 2); z.Id = _repo.CreateCo2Tag(z);

        z.Impulse = 12; z.Abgeschlossen = true;
        _repo.UpdateCo2Tag(z);

        Assert.Equal(12, _repo.GetCo2Tag("2026-11-02", 2)!.Impulse);
        Assert.Equal(5, _repo.GetCo2Tag("2026-11-02")!.Impulse);
        Assert.False(_repo.GetCo2Tag("2026-11-02")!.Abgeschlossen);
    }

    [Fact]
    public void OffenerTag_UndLetzteTage_GeltenJeZelt()
    {
        _repo.CreateCo2Tag(Tag("2026-11-01", 3));
        _repo.CreateCo2Tag(Tag("2026-11-02", 4, zelt: 2));

        Assert.Equal("2026-11-01", _repo.GetOffenerCo2Tag()!.Datum);
        Assert.Equal("2026-11-02", _repo.GetOffenerCo2Tag(2)!.Datum);
        Assert.Null(_repo.GetOffenerCo2Tag(5));
        Assert.Equal(["2026-11-02"], _repo.GetCo2Tage(10, 2).Select(t => t.Datum));
        Assert.Equal(["2026-11-01"], _repo.GetCo2Tage(10).Select(t => t.Datum));
    }
}
