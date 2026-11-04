using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Weitere Instanzen über den Controller (Etappe 2, Release 5a): Anlegen mit Zelt, Umbenennen, Entfernen — und was abgewiesen wird.
/// Der Controller bekommt keine Abhängigkeiten: die Instanz-Routen nutzen nur ihre eigenen.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class SteuerungInstanzApiTests : IDisposable
{
    private readonly string _wurzel;
    private readonly SteuerungRepository _repo;
    private readonly SteuerungInstanzService _instanzen;
    private readonly SteuerungZeltService _zelte;
    private readonly SteuerungApiController _controller;
    private readonly int _zelt1;
    private readonly int _zelt2;

    public SteuerungInstanzApiTests()
    {
        SteuerungInstanzen.Festlegen([]);
        _wurzel = Path.Combine(Path.GetTempPath(), "SteuerungInstanzApi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        var zelt = TestDatabase.InitializeWithDefaultTent(pfade);
        var tents = new TentRepository(pfade);
        _zelt1 = zelt.Id;
        _zelt2 = tents.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        _repo = new SteuerungRepository(pfade);
        _instanzen = new SteuerungInstanzService(_repo);
        _zelte = new SteuerungZeltService(_repo, tents, new GrowRepository(pfade));
        _controller = new SteuerungApiController(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private static T Wert<T>(ActionResult<T> ergebnis) where T : class
        => (ergebnis.Result as ObjectResult)?.Value as T ?? ergebnis.Value ?? throw new InvalidOperationException("Keine Antwort: " + ergebnis.Result);

    private ActionResult<SteuerungInstanzenDto> Anlegen(string art, string name, int? zeltId)
        => _controller.InstanzAnlegen(_instanzen, _zelte, new SteuerungInstanzAnlegen { Art = art, Name = name, ZeltId = zeltId });

    [Fact]
    public void Anlegen_nimmt_die_naechste_Nummer_weist_das_Zelt_zu_und_macht_die_Instanz_im_Register_sichtbar()
    {
        var liste = Wert(Anlegen("chiller", "Reserve", _zelt2)).Instanzen;

        var instanz = Assert.Single(liste);
        Assert.Equal("chiller-i2", instanz.Modul);
        Assert.Equal(2, instanz.Nummer);
        Assert.Equal("Reserve", instanz.Name);
        Assert.Equal(_zelt2, instanz.ZeltId);
        Assert.Contains("chiller-i2", SteuerungInstanzen.Module);
        Assert.Equal(_zelt2, _zelte.ZeltFuer("chiller-i2"));
        // Die erste Instanz steht nicht darin und bleibt unberührt.
        Assert.DoesNotContain(liste, i => i.Modul == "chiller");

        var zweite = Wert(Anlegen("chiller", "Noch eine", _zelt1)).Instanzen;
        Assert.Equal(["chiller-i2", "chiller-i3"], zweite.Select(i => i.Modul).ToList());
    }

    [Fact]
    public void Anlegen_weist_Unsinn_ab_und_hinterlaesst_nichts()
    {
        Assert.IsType<BadRequestObjectResult>(Anlegen("chiller", "", _zelt1).Result);                // kein Name
        Assert.IsType<BadRequestObjectResult>(Anlegen("zuluft", "Zweite Zuluft", _zelt1).Result);    // Dienst kann noch nicht
        Assert.IsType<BadRequestObjectResult>(Anlegen("gibt-es-nicht", "x", _zelt1).Result);
        Assert.IsType<BadRequestObjectResult>(Anlegen("chiller", "Reserve", 99999).Result);          // Zelt gibt es nicht
        Assert.IsType<BadRequestObjectResult>(_controller.InstanzAnlegen(_instanzen, _zelte, null!).Result);

        Assert.Empty(_instanzen.Liste());
        Assert.Empty(SteuerungInstanzen.Module);
        // Nummer 2 ist noch frei: die abgewiesenen Versuche haben keine verbraucht.
        Assert.Equal("chiller-i2", Wert(Anlegen("chiller", "Reserve", _zelt1)).Instanzen.Single().Modul);
    }

    [Fact]
    public void Ein_Name_gilt_je_Art_nur_einmal()
    {
        Assert.NotNull(Wert(Anlegen("chiller", "Reserve", _zelt1)));
        Assert.IsType<BadRequestObjectResult>(Anlegen("chiller", "reserve", _zelt2).Result);
        Assert.Single(_instanzen.Liste());
    }

    [Fact]
    public void Umbenennen_aendert_nur_den_Namen()
    {
        Anlegen("chiller", "Reserve", _zelt2);

        var liste = Wert(_controller.InstanzUmbenennen("chiller-i2", _instanzen, _zelte, new SteuerungInstanzUmbenennen { Name = "Kühler links" })).Instanzen;

        var instanz = Assert.Single(liste);
        Assert.Equal("chiller-i2", instanz.Modul);
        Assert.Equal("Kühler links", instanz.Name);
        Assert.Equal(_zelt2, instanz.ZeltId);
        // Auch im Register (Ruhemodus und Übersicht lesen den Namen von dort).
        Assert.Equal("Wasserkühler Kühler links", ZeltRuheArten.Finden("chiller-i2")!.Titel);

        Assert.IsType<BadRequestObjectResult>(_controller.InstanzUmbenennen("chiller-i2", _instanzen, _zelte, new SteuerungInstanzUmbenennen { Name = " " }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.InstanzUmbenennen("chiller-i9", _instanzen, _zelte, new SteuerungInstanzUmbenennen { Name = "Egal" }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.InstanzUmbenennen("chiller", _instanzen, _zelte, new SteuerungInstanzUmbenennen { Name = "Erste" }).Result);
        Assert.Equal("Kühler links", _instanzen.Liste().Single().Name);
    }

    [Fact]
    public async Task Entfernen_nimmt_die_Instanz_aus_dem_Fork_und_vergibt_die_Nummer_nie_wieder()
    {
        Anlegen("chiller", "Reserve", _zelt1);

        Assert.Empty(Wert(await _controller.InstanzEntfernen("chiller-i2", _instanzen, _zelte, null!, CancellationToken.None)).Instanzen);
        Assert.Empty(SteuerungInstanzen.Module);
        Assert.Null(ZeltRuheArten.Finden("chiller-i2"));

        Assert.IsType<NotFoundObjectResult>((await _controller.InstanzEntfernen("chiller-i2", _instanzen, _zelte, null!, CancellationToken.None)).Result);
        // Die erste Instanz ist nicht löschbar: sie steht nicht im Register.
        Assert.IsType<NotFoundObjectResult>((await _controller.InstanzEntfernen("chiller", _instanzen, _zelte, null!, CancellationToken.None)).Result);

        Assert.Equal("chiller-i3", Wert(Anlegen("chiller", "Neu", _zelt1)).Instanzen.Single().Modul);
    }

    [Fact]
    public void Liste_zeigt_Instanzen_mit_ihrem_Zelt()
    {
        Assert.Empty(Wert(_controller.Instanzen(_instanzen, _zelte)).Instanzen);
        Anlegen("chiller", "Reserve", _zelt2);

        var instanz = Assert.Single(Wert(_controller.Instanzen(_instanzen, _zelte)).Instanzen);
        Assert.Equal(_zelt2, instanz.ZeltId);
    }
}
