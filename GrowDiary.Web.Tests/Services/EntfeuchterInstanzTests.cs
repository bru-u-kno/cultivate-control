using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Entfeuchter-Instanzen (Etappe 2, Release 6a): Der Entfeuchter-Dienst arbeitet mit den Kennungen seiner Instanz — im NEUEN
/// Namensraum plus <c>_i{n}</c>, auch in einer Anlage mit alten Namen — und liest das CO₂ und die Zuluft seines EIGENEN Zeltes.
/// Die erste Instanz bleibt, wie sie war.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class EntfeuchterInstanzTests : IDisposable
{
    private readonly Co2Zelte.Stand _co2Vorher = Co2Zelte.Aktuell;

    public EntfeuchterInstanzTests()
    {
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "entfeuchter", Nummer = 2, Name = "Zelt B" }]);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        Co2Zelte.Festlegen(_co2Vorher.BestandZeltId, _co2Vorher.ZeltIds);
    }

    /// <summary>Die Kennungen des Dienstes, die der Katalog des Entfeuchters führt (Eigenschaften und Konstanten).</summary>
    private static List<string> Kennungen()
    {
        var typ = typeof(EntfeuchterSteuerungService.Entitaeten);
        var werte = typ.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => (string)p.GetValue(null)!)
            .Concat(typ.GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!));
        var katalog = SteuerungBauteile.FuerModul("entfeuchter").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return werte.Where(katalog.Contains).ToList();
    }

    // ------------------------------------------------------------------ Kennungen

    [Fact]
    public void JedeKennungDesDienstesStehtImKatalogDerInstanz()
    {
        var kennungen = Kennungen();
        Assert.True(kennungen.Count >= 18, $"Gefunden: {kennungen.Count} Kennungen.");

        var zweite = SteuerungBauteile.FuerModul("entfeuchter-i2").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var kennung in kennungen)
        {
            Assert.Equal(kennung + "_i2", EntfeuchterSteuerungService.Kennung(kennung, 2));
            Assert.Contains(EntfeuchterSteuerungService.Kennung(kennung, 2), zweite);
            Assert.Equal(kennung, EntfeuchterSteuerungService.Kennung(kennung, null));
        }
    }

    [Fact]
    public void DieRegelungHeisstInDerInstanzWieDerDienstSieSucht()
    {
        var regelung = SteuerungBauteile.FuerModul("entfeuchter-i2").Single(b => b.VorlagenDatei == "regelung");
        Assert.Equal("automation.entfeuchter_regelung_tag_nacht_i2", regelung.EntityId);
        Assert.Equal(regelung.EntityId, EntfeuchterSteuerungService.Kennung(EntfeuchterSteuerungService.Entitaeten.Automatik, 2));
    }

    [Fact]
    public void CO2UndZuluftGehoerenNichtZumKatalogDerInstanz()
    {
        // Diese Kennungen gehören anderen Steuerungen: die Instanz bekommt dafür nie eigene Bausteine.
        var zweite = SteuerungBauteile.FuerModul("entfeuchter-i2").Select(b => b.EntityId).ToList();
        Assert.DoesNotContain(zweite, id => id.Contains("co2_", StringComparison.Ordinal) || id.Contains("zuluft", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ Einstellungen

    [Fact]
    public void EinstellungenWerdenAusDenHelfernDerInstanzGelesen()
    {
        var zustaende = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["input_number.fork_entfeuchter_hysterese"] = "6",          // erste Instanz — darf die zweite nicht beeinflussen
            ["input_number.fork_entfeuchter_hysterese_i2"] = "3",
            ["input_number.fork_entfeuchter_mindestlaufzeit_i2"] = "12",
            ["input_boolean.fork_entfeuchter_vpd_regelung_i2"] = "on",
            ["automation.entfeuchter_regelung_tag_nacht_i2"] = "on",
        };

        var zweite = EntfeuchterSteuerungService.AusHomeAssistant(zustaende, null, 2);
        var erste = EntfeuchterSteuerungService.AusHomeAssistant(zustaende);

        Assert.Equal(3, zweite.HystereseProzent);
        Assert.Equal(12, zweite.MindestlaufzeitMin);
        Assert.True(zweite.VpdRegelung);
        Assert.True(zweite.AutomatikAktiv);
        Assert.Equal(6, erste.HystereseProzent);
        Assert.False(erste.VpdRegelung);
    }

    [Fact]
    public void DerSchreibumfangDerInstanzTrifftNurIhreSchalter()
    {
        var neu = new HashSet<string> { "input_boolean.fork_entfeuchter_vpd_regelung_i2" };

        var zweite = EntfeuchterSteuerungService.Schreibumfang([], neu, 2);
        var erste = EntfeuchterSteuerungService.Schreibumfang([], neu);

        Assert.True(zweite.VpdRegelung);
        Assert.False(zweite.Tagbetrieb);
        Assert.False(erste.VpdRegelung);
    }

    [Fact]
    public void DieBeschriftungKenntAuchDieHelferDerInstanz()
    {
        Assert.Equal(("Hysterese", "%"), EntfeuchterSteuerungService.BeschriftungFuer("input_number.fork_entfeuchter_hysterese_i2", 2));
        // Ohne die Nummer ist es kein Helfer der ersten Instanz.
        Assert.Equal("input_number.fork_entfeuchter_hysterese_i2", EntfeuchterSteuerungService.BeschriftungFuer("input_number.fork_entfeuchter_hysterese_i2").Name);
    }

    // ------------------------------------------------------------------ Dienst

    [Fact]
    public void DerDienstEinerInstanzHatSeinModulUndSpeichertGetrennt()
    {
        using var p = new ZusatzPruefstand();
        var erste = p.Entfeuchter();
        var zweite = erste.Fuer("entfeuchter-i2");

        Assert.Equal("entfeuchter", erste.ModulName);
        Assert.Null(erste.Nummer);
        Assert.Equal("entfeuchter-i2", zweite.ModulName);
        Assert.Equal(2, zweite.Nummer);

        // Ein Modul, das keine Entfeuchter-Instanz ist, bekommt die erste Instanz (auch die Zusatz-Art „entfeuchter-zusatz-i2“).
        Assert.Null(erste.Fuer("chiller-i2").Nummer);
        Assert.Null(erste.Fuer("entfeuchter-zusatz-i2").Nummer);
        Assert.Null(zweite.Fuer("zuluft-i2").Nummer);
        Assert.Equal("entfeuchter", zweite.Bestand().ModulName);

        p.Repo.SetEinstellungen("entfeuchter-i2", new EntfeuchterEinstellungen { HystereseProzent = 3 });
        Assert.Equal(3, zweite.Gespeichert!.HystereseProzent);
        Assert.Null(erste.Gespeichert);
    }

    [Fact]
    public void DieKopieDerInstanzVeraendertDenDienstDerErstenInstanzNicht()
    {
        using var p = new ZusatzPruefstand();
        var erste = p.Entfeuchter();
        _ = erste.Fuer("entfeuchter-i2");
        Assert.Equal("entfeuchter", erste.ModulName);
        Assert.Null(erste.Nummer);
    }

    // ------------------------------------------------------------------ Startwerte

    [Theory]
    [InlineData("entfeuchter-i2", true)]
    [InlineData("entfeuchter-i17", true)]
    [InlineData("chiller-i2", true)]
    [InlineData("entfeuchter-zusatz-i2", true)]
    [InlineData("zuluft-i2", true)]
    [InlineData("licht-i2", false)]
    public void ImStartwertWegSindEntfeuchterUndKuehlerInstanzen(string modul, bool kann)
        => Assert.Equal(kann, SteuerungStartwerteService.Kann(modul));

    [Fact]
    public void FuerEineInstanzWaehltDieStartwertWahlDenDienstMitIhremModul()
    {
        using var p = new ZusatzPruefstand();
        var erste = p.Entfeuchter();
        var dienste = new Dictionary<string, IStartwertDienst>(StringComparer.OrdinalIgnoreCase) { ["entfeuchter"] = erste };

        var gewaehlt = SteuerungStartwerteService.Waehlen("entfeuchter-i2", dienste);

        Assert.NotNull(gewaehlt);
        Assert.Equal("entfeuchter-i2", gewaehlt!.StartModul);
        Assert.Equal("entfeuchter", ((IStartwertDienst)erste).StartModul);
        Assert.Throws<InvalidOperationException>(() => SteuerungStartwerteService.Waehlen("entfeuchter-i2", new Dictionary<string, IStartwertDienst>()));
    }

    // ------------------------------------------------------------------ Live: CO₂ und Zuluft des eigenen Zeltes

    private sealed record Aufbau(ZusatzPruefstand Pruefstand, int Zelt1, int Zelt2, EntfeuchterSteuerungService Erste, EntfeuchterSteuerungService Zweite);

    private static Aufbau Zelte(bool zweitesZeltMitCo2)
    {
        var p = new ZusatzPruefstand();
        var tents = new TentRepository(p.Pfade);
        var zelt1 = new TentRepository(p.Pfade).GetTents().First().Id;
        var zelt2 = tents.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        var zelte = new SteuerungZeltService(p.Repo, tents, new GrowRepository(p.Pfade));
        Assert.Null(zelte.Speichern(new Dictionary<string, int> { ["entfeuchter"] = zelt1, ["entfeuchter-i2"] = zelt2 }));
        Co2Zelte.Festlegen(zelt1, zweitesZeltMitCo2 ? [zelt1, zelt2] : [zelt1]);
        var erste = p.Entfeuchter(zelte);
        return new Aufbau(p, zelt1, zelt2, erste, erste.Fuer("entfeuchter-i2"));
    }

    [Fact]
    public async Task EineInstanzLiestDasCo2IhresEigenenZeltes()
    {
        var a = Zelte(zweitesZeltMitCo2: true);
        using var p = a.Pruefstand;
        p.Setze("input_number.co2_rh_obergrenze", "70");                 // Zelt 1
        p.Setze("input_number.co2_klima_hysterese", "4");
        p.Setze($"input_number.co2_z{a.Zelt2}_rh_obergrenze", "58");      // Zelt 2
        p.Setze($"input_number.co2_z{a.Zelt2}_klima_hysterese", "2");

        var erste = await a.Erste.LiveAsync(CancellationToken.None);
        var zweite = await a.Zweite.LiveAsync(CancellationToken.None);

        Assert.Equal(70, erste.RhObergrenzeProzent);
        Assert.Equal(66, erste.DeckelProzent);
        Assert.Equal(58, zweite.RhObergrenzeProzent);
        Assert.Equal(56, zweite.DeckelProzent);
    }

    [Fact]
    public async Task OhneCo2ImEigenenZeltLiestDieInstanzNieDasCo2EinesAnderenZeltes()
    {
        var a = Zelte(zweitesZeltMitCo2: false);
        using var p = a.Pruefstand;
        p.Setze("input_number.co2_rh_obergrenze", "70");
        p.Setze("input_number.co2_klima_hysterese", "4");

        var zweite = await a.Zweite.LiveAsync(CancellationToken.None);

        // Kein Helfer im eigenen Zelt und kein Plan: keine Grenze und kein Deckel — nicht die 70 des ersten Zeltes.
        Assert.Null(zweite.RhObergrenzeProzent);
        Assert.Null(zweite.DeckelProzent);
        Assert.Equal(70, (await a.Erste.LiveAsync(CancellationToken.None)).RhObergrenzeProzent);
    }

    [Fact]
    public async Task DieZuluftEinerInstanzKommtAusIhrenEigenenRollen()
    {
        var a = Zelte(zweitesZeltMitCo2: false);
        using var p = a.Pruefstand;
        p.Setze("binary_sensor.zuluft_bedarf", "on");                     // die Zuluft des ersten Zeltes
        p.Setze("binary_sensor.zelt_b_zuluft_bedarf", "off");
        p.Repo.SetGeraet("entfeuchter-i2", "zuluft_bedarf", "binary_sensor.zelt_b_zuluft_bedarf");

        Assert.False((await a.Zweite.LiveAsync(CancellationToken.None)).ZuluftVorrang);

        p.Setze("binary_sensor.zelt_b_zuluft_bedarf", "on");
        p.Setze("sensor.zelt_b_zuluft_stufe", "0");
        p.Repo.SetGeraet("entfeuchter-i2", "zuluft_stufe_ist", "sensor.zelt_b_zuluft_stufe");
        Assert.False((await a.Zweite.LiveAsync(CancellationToken.None)).ZuluftVorrang);   // Bedarf, aber der Lüfter steht

        p.Setze("sensor.zelt_b_zuluft_stufe", "2");
        Assert.True((await a.Zweite.LiveAsync(CancellationToken.None)).ZuluftVorrang);

        // Ohne eigene Rolle gibt es keinen Vorrang — auch wenn die Zuluft des ersten Zeltes meldet.
        p.Repo.SetGeraet("entfeuchter-i2", "zuluft_bedarf", null);
        Assert.Null((await a.Zweite.LiveAsync(CancellationToken.None)).ZuluftVorrang);
    }

    [Fact]
    public async Task DasSpeichernEinerInstanzSchreibtNurIhreHelfer()
    {
        var a = Zelte(zweitesZeltMitCo2: false);
        using var p = a.Pruefstand;
        foreach (var kennung in Kennungen().Where(k => k.StartsWith("input_", StringComparison.Ordinal)))
        {
            p.Setze(EntfeuchterSteuerungService.Kennung(kennung, 2), "10");
        }
        p.Vergessen();

        var (gespeichert, fehler, _) = await a.Zweite.SpeichernAsync(new EntfeuchterEinstellungen(), CancellationToken.None);

        Assert.NotNull(gespeichert);
        Assert.Empty(fehler);
        var geschrieben = p.Schreibaufrufe().Select(w => w.Entitaet).ToList();
        Assert.True(geschrieben.Count >= 10, $"Geschrieben: {geschrieben.Count}.");
        Assert.All(geschrieben, id => Assert.EndsWith("_i2", id));
        Assert.NotNull(a.Zweite.Gespeichert);
        Assert.Null(a.Erste.Gespeichert);
    }
}

/// <summary>Eine Anlage mit den alten Kennungen (<c>trotec_*</c>): die erste Instanz behält sie, eine weitere trägt nur die neuen.</summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class EntfeuchterInstanzImBestandRaumTests : IDisposable
{
    public EntfeuchterInstanzImBestandRaumTests()
    {
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "entfeuchter", Nummer = 2, Name = "Zelt B" }]);
        Kennungen.Festlegen(KennungsRaum.Bestand);
    }

    public void Dispose()
    {
        Kennungen.Festlegen(KennungsRaum.Neu);
        SteuerungInstanzen.Festlegen([]);
    }

    [Fact]
    public void DieErsteInstanzBehaeltDieAltenKennungenUndDieWeitereTraegtNurNeue()
    {
        // Erste Instanz: Bestand, unverändert.
        Assert.Equal("input_number.trotec_hysterese", EntfeuchterSteuerungService.Entitaeten.Hysterese);
        Assert.Equal("input_number.trotec_hysterese", EntfeuchterSteuerungService.Kennung(EntfeuchterSteuerungService.Entitaeten.Hysterese, null));
        // Weitere Instanz: neuer Namensraum plus Endung — nie „trotec_hysterese_i2“.
        Assert.Equal("input_number.fork_entfeuchter_hysterese_i2", EntfeuchterSteuerungService.Kennung(EntfeuchterSteuerungService.Entitaeten.Hysterese, 2));
        Assert.Equal("automation.entfeuchter_regelung_tag_nacht_i2", EntfeuchterSteuerungService.Kennung(EntfeuchterSteuerungService.Entitaeten.Automatik, 2));
    }

    [Fact]
    public void JedeKennungDerInstanzStehtImKatalogDerInstanz()
    {
        var zweite = SteuerungBauteile.FuerModul("entfeuchter-i2").Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var eigene = new[]
        {
            EntfeuchterSteuerungService.Entitaeten.VpdRegelung, EntfeuchterSteuerungService.Entitaeten.Hysterese,
            EntfeuchterSteuerungService.Entitaeten.Mindestlaufzeit, EntfeuchterSteuerungService.Entitaeten.Tagbetrieb,
            EntfeuchterSteuerungService.Entitaeten.TempMaxTag, EntfeuchterSteuerungService.Entitaeten.FeuchteEinNacht,
            EntfeuchterSteuerungService.Entitaeten.EinAktiv, EntfeuchterSteuerungService.Entitaeten.AusAktiv,
            EntfeuchterSteuerungService.Entitaeten.Automatik, EntfeuchterSteuerungService.Entitaeten.VpdUnten,
        };
        Assert.Equal(10, eigene.Length);
        foreach (var kennung in eigene)
        {
            Assert.Contains(EntfeuchterSteuerungService.Kennung(kennung, 2), zweite);
        }
        // Keine alte Kennung im Katalog der Instanz.
        Assert.DoesNotContain(zweite, id => id.Contains("trotec", StringComparison.Ordinal) || id.Contains("rdwc", StringComparison.Ordinal));
    }

    [Fact]
    public void DieVorlagenDerInstanzVerweisenNurAufNeueKennungen()
    {
        var rechenwerte = SteuerungBauteile.FuerModul("entfeuchter-i2").Where(b => b.Vorlage is not null).ToList();
        Assert.True(rechenwerte.Count >= 4, $"Gefunden: {rechenwerte.Count} Rechenwerte.");
        Assert.All(rechenwerte, b => Assert.DoesNotContain("trotec", b.Vorlage!, StringComparison.Ordinal));
        Assert.Contains("sensor.fork_entfeuchter_feuchte_ein_aktiv_i2", rechenwerte.Single(b => b.EntityId == "binary_sensor.fork_entfeuchter_feuchte_ueber_ein_i2").Vorlage);
    }
}
