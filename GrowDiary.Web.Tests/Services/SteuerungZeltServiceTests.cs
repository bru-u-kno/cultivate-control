using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>Steuerungen je Zelt, Etappe 1: Zu welchem Zelt gehört eine Steuerung — echte Datenbank, kein Mock.</summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class SteuerungZeltServiceTests : IDisposable
{
    private readonly string _wurzel;
    private readonly SteuerungRepository _steuerung;
    private readonly TentRepository _zelte;
    private readonly GrowRepository _grows;
    private readonly SteuerungZeltService _dienst;
    private readonly Co2Zelte.Stand _registerVorher = Co2Zelte.Aktuell;
    private readonly int _zelt1;

    public SteuerungZeltServiceTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "SteuerungZelt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(pfade);
        _zelte = new TentRepository(pfade);
        _steuerung = new SteuerungRepository(pfade);
        _grows = new GrowRepository(pfade);
        _dienst = new SteuerungZeltService(_steuerung, _zelte, _grows);
        _zelt1 = _zelte.GetTents().First().Id;
        Co2Zelte.Festlegen(null, []);
    }

    public void Dispose()
    {
        Co2Zelte.Festlegen(_registerVorher.BestandZeltId, _registerVorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Aufräumen darf scheitern. */ }
    }

    private int ZweitesZelt(string name = "Zelt B", TentType typ = TentType.Production)
        => _zelte.CreateTent(new Tent { Name = name, TentType = typ }).Id;

    [Fact]
    public void MitEinemZeltGehoertAllesDorthin_OhneDassGespeichertWird()
    {
        var z = _dienst.Zuordnung();

        Assert.All(SteuerungZeltService.Zuweisbar, k => Assert.Equal(_zelt1, z[k]));
        Assert.Null(_steuerung.GetEinstellungen<SteuerungZeltZuordnung>(SteuerungZeltService.Modul));
    }

    [Fact]
    public void KommtEinZweitesZeltDazu_WirdDerBestandBeimErstenLesenFestgeschrieben()
    {
        var zelt2 = ZweitesZelt();
        // Der laufende Grow steht im zweiten Zelt: dort liegt heute die Steuerung.
        _grows.CreateGrow(new GrowRun { Name = "Grow B", TentId = zelt2, StartDate = DateTime.Today.AddDays(-5), Status = GrowStatus.Running });

        var erste = _dienst.Zuordnung();
        Assert.All(SteuerungZeltService.Zuweisbar, k => Assert.Equal(zelt2, erste[k]));
        Assert.NotNull(_steuerung.GetEinstellungen<SteuerungZeltZuordnung>(SteuerungZeltService.Modul));

        // Ein neuer Grow im ersten Zelt verschiebt nichts: festgeschrieben ist festgeschrieben.
        _grows.CreateGrow(new GrowRun { Name = "Grow A", TentId = _zelt1, StartDate = DateTime.Today.AddDays(-1), Status = GrowStatus.Running });
        Assert.All(SteuerungZeltService.Zuweisbar, k => Assert.Equal(zelt2, _dienst.Zuordnung()[k]));
    }

    [Fact]
    public void Speichern_AendertNurDieGenanntenSteuerungen()
    {
        var zelt2 = ZweitesZelt();
        var vorher = _dienst.Zuordnung();

        Assert.Null(_dienst.Speichern(new Dictionary<string, int> { ["chiller"] = zelt2 }));

        var nachher = _dienst.Zuordnung();
        Assert.Equal(zelt2, nachher["chiller"]);
        Assert.All(SteuerungZeltService.Zuweisbar.Where(k => k != "chiller"), k => Assert.Equal(vorher[k], nachher[k]));
    }

    [Fact]
    public void Speichern_LehntUnbekannteSteuerungUndUnbekanntesZeltAb_UndSchreibtNichts()
    {
        var zelt2 = ZweitesZelt();
        var vorher = _dienst.Zuordnung().ToDictionary(p => p.Key, p => p.Value);

        Assert.NotNull(_dienst.Speichern(new Dictionary<string, int> { ["co2"] = zelt2 }));         // CO₂ trägt sein Zelt selbst
        Assert.NotNull(_dienst.Speichern(new Dictionary<string, int> { ["chiller"] = 9999 }));
        Assert.NotNull(_dienst.Speichern(new Dictionary<string, int> { ["gibt-es-nicht"] = zelt2 }));

        Assert.Equal(vorher, _dienst.Zuordnung().ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void EinArchiviertesZelt_GiltWieNieGewaehlt()
    {
        var zelt2 = ZweitesZelt();
        _dienst.Zuordnung(); // schreibt fest
        Assert.Null(_dienst.Speichern(new Dictionary<string, int> { ["zuluft"] = zelt2 }));
        Assert.Equal(zelt2, _dienst.ZeltFuer("zuluft"));

        var z = _zelte.GetTents().First(t => t.Id == zelt2);
        z.Status = TentStatus.Archived;
        _zelte.UpdateTent(z);

        Assert.Equal(_zelt1, _dienst.ZeltFuer("zuluft"));
    }

    [Fact]
    public void DasTrockenzeltIstNurDieLetzteWahl_WennSonstNichtsPasst()
    {
        var trocken = ZweitesZelt("Trocknung", TentType.Drying);
        // Kein laufender Grow, kein CO₂-Zelt: Standard ist das erste Zelt, das kein Trockenzelt ist.
        Assert.Equal(_zelt1, _dienst.StandardZelt());
        Assert.NotEqual(trocken, _dienst.StandardZelt());
    }

    [Fact]
    public void DerZusatzEntfeuchterFolgtDemEntfeuchterInsZelt()
    {
        var zelt2 = ZweitesZelt();

        Assert.Null(_dienst.Speichern(new Dictionary<string, int> { ["entfeuchter"] = zelt2 }));

        Assert.Equal(zelt2, _dienst.ZeltFuer("entfeuchter"));
        Assert.Equal(zelt2, _dienst.ZeltFuer("entfeuchter-zusatz"));
    }
}
