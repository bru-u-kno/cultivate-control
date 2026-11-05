using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Zusatz-Entfeuchter-Instanzen (Etappe 2, Release 9): Ein weiterer Zusatz gehört zum Entfeuchter mit derselben Nummer. Er arbeitet mit
/// den Kennungen seiner Instanz, liest dessen Fühler und Höchsttemperatur, und seine Vorlagen zeigen auf die Kennungen dieses
/// Entfeuchters. Der erste Zusatz bleibt, wie er war.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class ZusatzInstanzTests : IDisposable
{
    public ZusatzInstanzTests() => SteuerungInstanzen.Festlegen(
    [
        new SteuerungInstanz { Art = "entfeuchter", Nummer = 2, Name = "Entfeuchter B" },
        new SteuerungInstanz { Art = "entfeuchter-zusatz", Nummer = 2, Name = "Zusatz B" },
    ]);

    public void Dispose() => SteuerungInstanzen.Festlegen([]);

    private static List<string> Konstanten()
        => typeof(EntfeuchterZusatzSteuerungService.Entitaeten).GetProperties()
            .Select(p => (string)p.GetValue(null)!).ToList();

    [Fact]
    public void JedeKennungDesDienstesStehtImKatalogDerInstanz()
    {
        var kennungen = Konstanten();
        Assert.True(kennungen.Count >= 12, $"Gefunden: {kennungen.Count} Kennungen.");
        var zweite = SteuerungBauteile.FuerModul("entfeuchter-zusatz-i2").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var kennung in kennungen)
        {
            Assert.Equal(kennung, EntfeuchterZusatzSteuerungService.Kennung(kennung, null));
            Assert.Contains(EntfeuchterZusatzSteuerungService.Kennung(kennung, 2), zweite);
            Assert.EndsWith("_i2", EntfeuchterZusatzSteuerungService.Kennung(kennung, 2));
        }
    }

    [Fact]
    public void DieVorlagenDerInstanzLesenDenEntfeuchterMitDerselbenNummer()
    {
        var haupt = Kennungen.NeueId("input_number.vpd_ziel_unten");
        var schwelle = SteuerungBauteile.FuerModul("entfeuchter-zusatz-i2").Single(b => b.EntityId.Contains("vpd_ein_schwelle"));

        // Der Rechenwert des Zusatzes liest das VPD-Ziel SEINES Entfeuchters — nicht das des ersten.
        Assert.Contains(haupt + "_i2", schwelle.Vorlage);
        Assert.DoesNotContain(haupt + "'", schwelle.Vorlage);
        var erste = SteuerungBauteile.FuerModul("entfeuchter-zusatz").Single(b => b.EntityId.Contains("vpd_ein_schwelle"));
        Assert.DoesNotContain("_i2", erste.Vorlage);
    }

    [Fact]
    public void FuehlerUndLichtKommenVomEntfeuchterDerselbenNummer()
    {
        var erste = SteuerungGeraeteRollen.MitbenutztVon("entfeuchter-zusatz").Select(m => m.Quelle).Distinct().ToList();
        var zweite = SteuerungGeraeteRollen.MitbenutztVon("entfeuchter-zusatz-i2").Select(m => m.Quelle).Distinct().ToList();

        Assert.Equal(["entfeuchter"], erste);
        Assert.Equal(["entfeuchter-i2"], zweite);
        Assert.Equal(4, SteuerungGeraeteRollen.MitbenutztVon("entfeuchter-zusatz-i2").Count);
        Assert.Equal(SteuerungGeraeteRollen.FuerModulMitMitbenutzten("entfeuchter-zusatz").Count,
            SteuerungGeraeteRollen.FuerModulMitMitbenutzten("entfeuchter-zusatz-i2").Count);
    }

    [Fact]
    public void DerDienstEinerInstanzHatSeinModulUndSpeichertGetrennt()
    {
        using var p = new ZusatzPruefstand();
        var erste = p.Zusatz();
        var zweite = erste.Fuer("entfeuchter-zusatz-i2");

        Assert.Equal("entfeuchter-zusatz", erste.ModulName);
        Assert.Null(erste.Nummer);
        Assert.Equal("entfeuchter-zusatz-i2", zweite.ModulName);
        Assert.Equal(2, zweite.Nummer);
        Assert.Null(erste.Fuer("entfeuchter-i2").Nummer);
        Assert.Equal("entfeuchter-zusatz", zweite.Bestand().ModulName);

        p.Repo.SetEinstellungen("entfeuchter-zusatz-i2", new EntfeuchterZusatzEinstellungen { VpdHystereseKpa = 0.4 });
        Assert.Equal(0.4, zweite.Gespeichert!.VpdHystereseKpa);
        Assert.NotEqual(0.4, erste.Gespeichert?.VpdHystereseKpa);
    }

    [Fact]
    public void EinstellungenUndSchreibelisteGehoerenDerInstanz()
    {
        var zustaende = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [EntfeuchterZusatzSteuerungService.Entitaeten.VpdHysterese] = "0.1",                                       // erster Zusatz
            [EntfeuchterZusatzSteuerungService.Kennung(EntfeuchterZusatzSteuerungService.Entitaeten.VpdHysterese, 2)] = "0.4",
            [EntfeuchterZusatzSteuerungService.Kennung(EntfeuchterZusatzSteuerungService.Entitaeten.Mindestpause, 2)] = "7",
        };

        var zweite = EntfeuchterZusatzSteuerungService.AusHomeAssistant(zustaende, null, 2);
        var erste = EntfeuchterZusatzSteuerungService.AusHomeAssistant(zustaende);
        Assert.Equal(0.4, zweite.VpdHystereseKpa);
        Assert.Equal(7, zweite.MindestpauseMin);
        Assert.Equal(0.1, erste.VpdHystereseKpa);

        var liste = EntfeuchterZusatzSteuerungService.Startwerteliste(zweite, null, 2).Select(z => z.Entitaet).ToList();
        Assert.True(liste.Count >= 10, $"Geschrieben: {liste.Count}.");
        Assert.All(liste, id => Assert.EndsWith("_i2", id));
        Assert.All(EntfeuchterZusatzSteuerungService.Startwerteliste(erste).Select(z => z.Entitaet), id => Assert.DoesNotContain("_i2", id));
    }

    [Fact]
    public void DieGemeinsameHoechsttemperaturLandetBeimEntfeuchterDerInstanz()
    {
        var a = new EntfeuchterZusatzAenderung { TempMaxTagModus = "fest", TempMaxTagFestC = 27 };
        var liste = EntfeuchterZusatzSteuerungService.TempMaxSchreibliste(new EntfeuchterEinstellungen { TempMaxTagModus = "fest", TempMaxTagFestC = 27 }, a, null, 2);

        var eintrag = Assert.Single(liste);
        Assert.Equal(EntfeuchterSteuerungService.Kennung(EntfeuchterSteuerungService.Entitaeten.TempMaxTag, 2), eintrag.Entitaet);
        Assert.EndsWith("_i2", eintrag.Entitaet);
    }

    [Fact]
    public void ZeltRuhendKenntJedenWeiterenZusatzAlsGeraetDasAusgeht()
    {
        var art = ZeltRuheArten.Finden("entfeuchter-zusatz-i2");

        Assert.NotNull(art);
        Assert.Equal("Zusatz-Entfeuchter Zusatz B", art!.Titel);
        Assert.Equal("zusatz_schalter", art.Rolle);
        Assert.True(art.Standard);
        Assert.True(ZeltRuheArten.IstZusatzInstanz("entfeuchter-zusatz-i2"));
        Assert.False(ZeltRuheArten.IstZusatzInstanz("entfeuchter-zusatz"));
        Assert.False(ZeltRuheArten.IstEntfeuchterInstanz("entfeuchter-zusatz-i2"));
    }

    [Theory]
    [InlineData("regelung")]
    [InlineData("meldung")]
    public void DieAutomationDerInstanzNenntKeineKennungDesErstenGeraets(string datei)
    {
        var pfad = Path.Combine(AppContext.BaseDirectory, "Vorlagen", "entfeuchter-zusatz", datei + ".json");
        var vorlage = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pfad))!.AsObject();

        var zweite = InstanzModul.Vorlage(vorlage, "entfeuchter-zusatz-i2", SteuerungBauteile.UmstellKennungen("entfeuchter-zusatz")).ToJsonString();

        // Jede Kennung des Zusatzes UND seines Entfeuchters, die in der Vorlage vorkommt, trägt die Endung _i2 — keine steht mehr ohne sie da.
        var vorkommend = SteuerungBauteile.FuerModul("entfeuchter-zusatz").Concat(SteuerungBauteile.FuerModul("entfeuchter")).Select(x => x.EntityId).Where(k => vorlage.ToJsonString().Contains(k, StringComparison.Ordinal)).Distinct().ToList();
        Assert.True(vorkommend.Count >= (datei == "regelung" ? 10 : 4), $"Zu wenige Kennungen in {datei}: {vorkommend.Count}.");
        foreach (var kennung in vorkommend)
        {
            Assert.Contains(kennung + "_i2", zweite);
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(kennung) + @"(?![A-Za-z0-9_])"), zweite);
        }
        // Die erste Instanz bekommt dieselbe Vorlage unverändert.
        Assert.Same(vorlage, InstanzModul.Vorlage(vorlage, "entfeuchter-zusatz", SteuerungBauteile.UmstellKennungen("entfeuchter-zusatz")));
    }
}
