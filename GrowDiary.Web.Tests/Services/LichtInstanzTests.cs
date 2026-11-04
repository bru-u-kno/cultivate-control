using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Lampen-Instanzen (Etappe 2, Release 8): Der Licht-Dienst arbeitet mit Einstellungen und Geräten seiner Instanz, „Zelt ruhend“
/// kennt jede Lampe als eigenes Gerät. Die erste Lampe bleibt, wie sie war. Die Lampe hat keine Home-Assistant-Bausteine.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class LichtInstanzTests : IDisposable
{
    private readonly string _wurzel = Path.Combine(Path.GetTempPath(), "LichtInst_" + Guid.NewGuid().ToString("N"));
    private readonly SteuerungRepository _repo;

    public LichtInstanzTests()
    {
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.Initialize(pfade);
        _repo = new SteuerungRepository(pfade);
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "licht", Nummer = 2, Name = "Lampe links" }]);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private LichtSteuerungService Dienst()
        => new(_repo, null!, null!, null!, new HomeAssistantSettingsRepository(new AppPaths(_wurzel)),
            new SteuerungGeraeteService(_repo), NullLogger<LichtSteuerungService>.Instance);

    [Fact]
    public void DerDienstEinerInstanzHatSeinModulUndSpeichertGetrennt()
    {
        var erste = Dienst();
        var zweite = erste.Fuer("licht-i2");

        Assert.Equal("licht", erste.ModulName);
        Assert.Null(erste.Nummer);
        Assert.Equal("licht-i2", zweite.ModulName);
        Assert.Equal(2, zweite.Nummer);
        Assert.Null(erste.Fuer("zuluft-i2").Nummer);
        Assert.Null(erste.Fuer("licht").Nummer);
        Assert.Equal("licht", zweite.Bestand().ModulName);

        _repo.SetEinstellungen("licht-i2", new LichtEinstellungen { Stufe = 7, BlueteEin = "07:30" });
        Assert.Equal(7, zweite.Einstellungen.Stufe);
        Assert.Equal("07:30", zweite.Einstellungen.BlueteEin);
        Assert.Equal(new LichtEinstellungen().Stufe, erste.Einstellungen.Stufe);
        Assert.NotEqual("07:30", erste.Einstellungen.BlueteEin);
    }

    [Fact]
    public void DieGeraeteEinerInstanzLiegenUnterIhremModul()
    {
        _repo.SetGeraet("licht", "licht_modus", "select.lampe_eins_modus");
        _repo.SetGeraet("licht-i2", "licht_modus", "select.lampe_zwei_modus");
        var geraete = new SteuerungGeraeteService(_repo);

        Assert.Equal("select.lampe_eins_modus", geraete.EntitiesFuerModul("licht")["licht_modus"]);
        Assert.Equal("select.lampe_zwei_modus", geraete.EntitiesFuerModul("licht-i2")["licht_modus"]);
    }

    [Fact]
    public void ZeltRuhendKenntJedeWeitereLampeAlsGeraetDasAusgeht()
    {
        var art = ZeltRuheArten.Finden("licht-i2");

        Assert.NotNull(art);
        Assert.Equal("Licht Lampe links", art!.Titel);
        Assert.Equal("licht-i2", art.Modul);
        Assert.Equal("licht_modus", art.Rolle);
        Assert.True(art.Standard);
        Assert.True(ZeltRuheArten.IstLichtInstanz("licht-i2"));
        Assert.False(ZeltRuheArten.IstLichtInstanz("licht"));
        Assert.Contains("licht-i2", ZeltRuheArten.Standard(["licht", "licht-i2"]));
    }

    [Fact]
    public void DerPlanZumEntfernenEinerLampeIstLeerWeilSieKeineHaBausteineHat()
        => Assert.Empty(SteuerungInstanzAufraeumenService.Planen("licht-i2"));

    [Fact]
    public void DieUebersichtskachelTraegtKennungUndNamenDerInstanz()
    {
        var live = new LichtLive(true, "Schedule", 5, "08:00", "20:00", true, true, null, null, Array.Empty<string>(), Array.Empty<string>(), DateTime.UtcNow);
        var zeile = GrowDiary.Web.Api.Controllers.SteuerungApiController.LichtZeile(live, "licht-i2", "Lampe links");

        Assert.Equal(("licht-i2", "Lampe links"), (zeile.Kennung, zeile.Titel));
        Assert.Equal("an", zeile.Status);
    }
}
