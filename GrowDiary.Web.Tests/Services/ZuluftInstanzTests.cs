using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Zuluft-Instanzen (Etappe 2, Release 7): Der Zuluft-Dienst arbeitet mit den Kennungen seiner Instanz, „Zelt ruhend“ kennt jede
/// Instanz als eigenes Gerät, der Start-Weg und die Schnittstelle nehmen sie an. Die erste Instanz bleibt, wie sie war.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class ZuluftInstanzTests : IDisposable
{
    public ZuluftInstanzTests() => SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "zuluft", Nummer = 2, Name = "Keller 2" }]);

    public void Dispose() => SteuerungInstanzen.Festlegen([]);

    private static List<string> Konstanten()
        => typeof(ZuluftSteuerungService.Entitaeten).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).ToList();

    [Fact]
    public void JedeKennungDesDienstesStehtImKatalogDerInstanzOderIstGemeinsam()
    {
        var konstanten = Konstanten();
        Assert.True(konstanten.Count >= 15, $"Gefunden: {konstanten.Count} Kennungen.");
        var erste = SteuerungBauteile.FuerModul("zuluft").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var zweite = SteuerungBauteile.FuerModul("zuluft-i2").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imKatalog = konstanten.Where(erste.Contains).ToList();
        Assert.True(imKatalog.Count >= 12, "Zu wenige Kennungen des Dienstes stehen im Katalog.");
        foreach (var kennung in imKatalog)
        {
            var instanz = ZuluftSteuerungService.Kennung(kennung, 2);
            Assert.Contains(instanz, zweite);
            Assert.Equal(kennung, ZuluftSteuerungService.Kennung(kennung, null));
            // Die Feuchte draußen und im Keller ist für alle dieselbe; alles andere trägt die Endung.
            if (InstanzModul.Gemeinsam.Contains(kennung)) Assert.Equal(kennung, instanz);
            else Assert.Equal(kennung + "_i2", instanz);
        }
        Assert.Equal("sensor.absolute_feuchte_draussen", ZuluftSteuerungService.Kennung(ZuluftSteuerungService.Entitaeten.AbsolutDraussen, 2));
    }

    [Fact]
    public void DieRegelungHeisstInDerInstanzWieDerDienstSieSucht()
    {
        var regelung = SteuerungBauteile.FuerModul("zuluft-i2").Single(b => b.VorlagenDatei == "regelung");
        Assert.Equal("automation.zuluft_keller_regelung_i2", regelung.EntityId);
        Assert.Equal(regelung.EntityId, ZuluftSteuerungService.Kennung(ZuluftSteuerungService.Entitaeten.Automatik, 2));
    }

    [Fact]
    public void EinstellungenUndSchreibelisteGehoerenDerInstanz()
    {
        var zustaende = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input_number.zuluft_mindest_differenz"] = "1.2",          // erste Instanz — darf die zweite nicht beeinflussen
            ["input_number.zuluft_mindest_differenz_i2"] = "2.5",
            ["input_number.zuluft_stufe_max_i2"] = "6",
            ["automation.zuluft_keller_regelung_i2"] = "on",
        };

        var zweite = ZuluftSteuerungService.AusHomeAssistant(zustaende, 2);
        var erste = ZuluftSteuerungService.AusHomeAssistant(zustaende);
        Assert.Equal(2.5, zweite.MindestDifferenzGm3);
        Assert.Equal(6, zweite.StufeMax);
        Assert.True(zweite.AutomatikAktiv);
        Assert.Equal(1.2, erste.MindestDifferenzGm3);

        var liste = ZuluftSteuerungService.Schreibliste(zweite, 2).Select(z => z.Entity).ToList();
        Assert.True(liste.Count >= 7);
        Assert.All(liste, id => Assert.EndsWith("_i2", id));
        Assert.All(ZuluftSteuerungService.Schreibliste(erste).Select(z => z.Entity), id => Assert.DoesNotContain("_i2", id));
    }

    [Fact]
    public void DerDienstEinerInstanzHatSeinModulUndSpeichertGetrennt()
    {
        using var p = new ZusatzPruefstand();
        var erste = p.Zuluft();
        var zweite = erste.Fuer("zuluft-i2");

        Assert.Equal("zuluft", erste.ModulName);
        Assert.Null(erste.Nummer);
        Assert.Equal("zuluft-i2", zweite.ModulName);
        Assert.Equal(2, zweite.Nummer);
        Assert.Null(erste.Fuer("chiller-i2").Nummer);
        Assert.Equal("zuluft", zweite.Bestand().ModulName);

        p.Repo.SetEinstellungen("zuluft-i2", new ZuluftEinstellungen { MindestDifferenzGm3 = 2.5, ZeltTemperaturMinC = 20 });
        Assert.Equal(2.5, zweite.Gespeichert!.MindestDifferenzGm3);
        Assert.Null(erste.Gespeichert);
    }

    [Fact]
    public async Task DasSpeichernEinerInstanzSchreibtNurIhreHelfer()
    {
        using var p = new ZusatzPruefstand();
        var zweite = p.Zuluft().Fuer("zuluft-i2");
        p.Vergessen();

        var (gespeichert, fehler, _) = await zweite.SpeichernAsync(new ZuluftEinstellungen { ZeltTemperaturMinC = 21 }, CancellationToken.None);

        Assert.NotNull(gespeichert);
        Assert.Empty(fehler);
        var geschrieben = p.Schreibaufrufe().Select(w => w.Entitaet).ToList();
        Assert.True(geschrieben.Count >= 7, $"Geschrieben: {geschrieben.Count}.");
        Assert.All(geschrieben, id => Assert.EndsWith("_i2", id));
    }

    [Fact]
    public async Task DieZuluftEinerInstanzLiestIhreEigenenRollen()
    {
        using var p = new ZusatzPruefstand();
        p.Setze("binary_sensor.zuluft_bedarf", "on");                      // die erste Instanz
        p.Setze("binary_sensor.zuluft_bedarf_i2", "off");
        p.Setze("sensor.zuluft_differenz_i2", "0.4");
        p.Setze("sensor.zelt2_aussen_temp", "12");
        p.Repo.SetGeraet("zuluft-i2", "aussen_temp", "sensor.zelt2_aussen_temp");
        var zweite = p.Zuluft().Fuer("zuluft-i2");

        var live = await zweite.LiveAsync(CancellationToken.None);

        Assert.False(live.Bedarf);
        Assert.Equal(0.4, live.DifferenzGm3);
        Assert.Equal(12, live.AussenTempC);
        Assert.True((await p.Zuluft().LiveAsync(CancellationToken.None)).Bedarf);
    }

    [Theory]
    [InlineData("zuluft-i2", true)]
    [InlineData("zuluft-i17", true)]
    [InlineData("licht-i2", false)]
    public void ImStartwertWegSindZuluftInstanzen(string modul, bool kann)
        => Assert.Equal(kann, SteuerungStartwerteService.Kann(modul));

    [Fact]
    public void FuerEineInstanzWaehltDieStartwertWahlDenDienstMitIhremModul()
    {
        using var p = new ZusatzPruefstand();
        var dienste = new Dictionary<string, IStartwertDienst>(StringComparer.OrdinalIgnoreCase) { ["zuluft"] = p.Zuluft() };

        var gewaehlt = SteuerungStartwerteService.Waehlen("zuluft-i2", dienste);

        Assert.Equal("zuluft-i2", gewaehlt!.StartModul);
        Assert.Equal("zuluft", ((IStartwertDienst)dienste["zuluft"]).StartModul);
    }

    [Fact]
    public void ZeltRuhendKenntJedeWeitereZuluftOhneVorauswahl()
    {
        var art = ZeltRuheArten.Finden("zuluft-i2");

        Assert.NotNull(art);
        Assert.Equal("Zuluft Keller 2", art!.Titel);
        Assert.Equal("zuluft-i2", art.Modul);
        Assert.Equal("port_schalter", art.Rolle);
        Assert.False(art.Standard);
        Assert.True(ZeltRuheArten.IstZuluftInstanz("zuluft-i2"));
        Assert.False(ZeltRuheArten.IstZuluftInstanz("zuluft"));
        Assert.DoesNotContain("zuluft-i2", ZeltRuheArten.Standard(["zuluft", "zuluft-i2"]));
    }

    [Fact]
    public void DieUebersichtskachelTraegtKennungUndNamenDerInstanz()
    {
        var live = new ZuluftLive(true, 1, null, null, null, null, null, null, true, null, null, null, null, true, null, null, null, null, null, null);
        var erste = GrowDiary.Web.Api.Controllers.ZuluftKachel.Modul(live, "1,5 g/m³");
        var zweite = GrowDiary.Web.Api.Controllers.ZuluftKachel.Modul(live, "2,5 g/m³", "zuluft-i2", "Keller 2");

        Assert.Equal(("zuluft", "Zuluft"), (erste.Kennung, erste.Titel));
        Assert.Equal(("zuluft-i2", "Keller 2"), (zweite.Kennung, zweite.Titel));
        Assert.Contains("2,5 g/m³", zweite.Kurz);
    }
}
