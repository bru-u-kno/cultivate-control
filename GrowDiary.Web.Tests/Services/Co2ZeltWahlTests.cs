using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Welches Zelt gehört dem vorhandenen CO₂-Objektsatz? Einmal entschieden, nie umgehängt — und das Register der CO₂-Zelte folgt den Zelten.
/// </summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class Co2ZeltWahlTests : IDisposable
{
    private readonly string _wurzel = Path.Combine(Path.GetTempPath(), "Co2Wahl_" + Guid.NewGuid().ToString("N"));
    private readonly TentRepository _zelte;
    private readonly SteuerungRepository _repo;
    private readonly Co2Zelte.Stand _vorher = Co2Zelte.Aktuell;
    private readonly int _erstes;

    public Co2ZeltWahlTests()
    {
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(pfade);
        _zelte = new TentRepository(pfade);
        _repo = new SteuerungRepository(pfade);
        _erstes = _zelte.GetTents().First().Id;
        Co2Zelte.Festlegen(null, []);
    }

    public void Dispose()
    {
        Co2Zelte.Festlegen(_vorher.BestandZeltId, _vorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private void Flag(int id, bool an)
    {
        var z = _zelte.GetTent(id)!;
        z.HasCo2Enrichment = an;
        _zelte.UpdateTent(z);
    }

    [Fact]
    public void Bestimmen_ErstesZeltMitCo2WirdBestand_GespeichertesBleibt()
    {
        Assert.Null(Co2ZeltWahl.Bestimmen(null, [1, 2], []).Bestand);
        Assert.Equal(2, Co2ZeltWahl.Bestimmen(null, [1, 2, 3], [3, 2]).Bestand);          // kleinste Id mit CO₂
        Assert.Equal(3, Co2ZeltWahl.Bestimmen(new Co2BestandStand { ZeltId = 3 }, [1, 2, 3], [2, 3]).Bestand); // gespeichert gilt
        Assert.Equal(2, Co2ZeltWahl.Bestimmen(new Co2BestandStand { ZeltId = 9 }, [1, 2], [2]).Bestand);      // gespeichertes Zelt gibt es nicht mehr
    }

    [Fact]
    public void Aktualisieren_OhneCo2Zelt_LaesstDasBestandZeltOffen_ObwohlDerDienstWeiterlaeuft()
    {
        var stand = Co2ZeltWahl.Aktualisieren(_zelte, _repo);

        Assert.Null(stand.BestandZeltId);
        Assert.Empty(stand.ZeltIds);
        Assert.Empty(Co2Zelte.Module);
        Assert.Null(_repo.GetEinstellungen<Co2BestandStand>(Co2ZeltWahl.Modul)); // nichts gespeichert, solange nichts entschieden ist
    }

    [Fact]
    public void Aktualisieren_ErstesCo2ZeltWirdBestandUndGespeichert_ZweitesBekommtEigenesModul()
    {
        Flag(_erstes, true);
        var zweites = _zelte.CreateTent(new Tent { Name = "Zweites", HasCo2Enrichment = true }).Id;

        var stand = Co2ZeltWahl.Aktualisieren(_zelte, _repo);

        Assert.Equal(_erstes, stand.BestandZeltId);
        Assert.Equal([_erstes, zweites], stand.ZeltIds);
        Assert.Equal(["co2", $"co2-z{zweites}"], Co2Zelte.Module);
        Assert.Equal(_erstes, _repo.GetEinstellungen<Co2BestandStand>(Co2ZeltWahl.Modul)!.ZeltId);
    }

    [Fact]
    public void Bestand_WirdNieUmgehaengt_AuchNichtWennDasFlagFaellt()
    {
        Flag(_erstes, true);
        Co2ZeltWahl.Aktualisieren(_zelte, _repo);
        var zweites = _zelte.CreateTent(new Tent { Name = "Zweites", HasCo2Enrichment = true }).Id;
        Flag(_erstes, false);   // der Nutzer nimmt dem alten Zelt das Flag …

        var stand = Co2ZeltWahl.Aktualisieren(_zelte, _repo);

        Assert.Equal(_erstes, stand.BestandZeltId);                 // … und seine Kennungen bleiben seine
        Assert.Contains(_erstes, stand.ZeltIds);                    // die Regelung läuft weiter wie vor der Umstellung
        Assert.Equal($"co2-z{zweites}", Co2Zelte.ModulFuer(zweites));
        Assert.Equal("co2", Co2Zelte.ModulFuer(_erstes));
    }

    [Fact]
    public void ArchiviertesZelt_HatKeineEigeneCo2Steuerung()
    {
        Flag(_erstes, true);
        var zweites = _zelte.CreateTent(new Tent { Name = "Zweites", HasCo2Enrichment = true }).Id;
        _zelte.ArchiveTent(zweites);

        var stand = Co2ZeltWahl.Aktualisieren(_zelte, _repo);

        Assert.Equal([_erstes], stand.ZeltIds);
    }

    [Fact]
    public void Aktualisieren_IstIdempotent()
    {
        Flag(_erstes, true);
        var a = Co2ZeltWahl.Aktualisieren(_zelte, _repo);
        var b = Co2ZeltWahl.Aktualisieren(_zelte, _repo);

        Assert.Equal(a.BestandZeltId, b.BestandZeltId);
        Assert.Equal(a.ZeltIds, b.ZeltIds);
    }
}
