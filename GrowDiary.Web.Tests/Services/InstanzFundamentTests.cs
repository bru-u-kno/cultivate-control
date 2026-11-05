using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>Das Register der Instanzen ist prozessweit: Tests, die es setzen, laufen nacheinander.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstanzRegisterCollection
{
    public const string Name = "InstanzRegister";
}

/// <summary>
/// Weitere Instanzen einer Steuerung (Etappe 2, Release 3, Fundament): Namenslogik, Katalog, Register. Noch nichts davon ist
/// von außen erreichbar — der Bestand bleibt, wie er ist.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class InstanzFundamentTests : IDisposable
{
    public InstanzFundamentTests() => SteuerungInstanzen.Festlegen([]);

    public void Dispose() => SteuerungInstanzen.Festlegen([]);

    // ------------------------------------------------------------------ Namenslogik

    [Theory]
    [InlineData("chiller-i2", "chiller", 2)]
    [InlineData("entfeuchter-i3", "entfeuchter", 3)]
    [InlineData("entfeuchter-zusatz-i2", "entfeuchter-zusatz", 2)]
    [InlineData("zuluft-i10", "zuluft", 10)]
    [InlineData("licht-i2", "licht", 2)]
    public void Modul_wird_in_Art_und_Nummer_zerlegt(string modul, string art, int nummer)
    {
        Assert.True(InstanzModul.IstInstanz(modul));
        Assert.Equal(art, InstanzModul.Art(modul));
        Assert.Equal(nummer, InstanzModul.Nummer(modul));
        Assert.Equal(modul, InstanzModul.Fuer(art, nummer));
        Assert.Equal(art, InstanzModul.BasisModul(modul));
    }

    [Theory]
    [InlineData("chiller")]
    [InlineData("chiller-i1")]
    [InlineData("chiller-i0")]
    [InlineData("chiller-i02")]
    [InlineData("co2-z2")]
    [InlineData("co2")]
    [InlineData("raum-i2")]
    [InlineData("entfeuchter-zusatz")]
    [InlineData("")]
    public void Die_erste_Instanz_und_fremde_Module_sind_keine_weitere_Instanz(string modul)
    {
        Assert.False(InstanzModul.IstInstanz(modul));
        Assert.Null(InstanzModul.Art(modul));
        Assert.Null(InstanzModul.Nummer(modul));
    }

    [Fact]
    public void Die_erste_Instanz_heisst_wie_die_Art_und_unbekannte_Arten_werden_abgelehnt()
    {
        Assert.Equal("chiller", InstanzModul.Fuer("chiller", 1));
        Assert.Throws<ArgumentException>(() => InstanzModul.Fuer("co2", 2));
        Assert.Throws<ArgumentException>(() => InstanzModul.Fuer("raum", 2));
    }

    [Fact]
    public void BasisModul_unterscheidet_Instanz_CO2_und_alles_andere()
    {
        Assert.Equal("co2", InstanzModul.BasisModul("co2-z3"));
        Assert.Equal("co2", InstanzModul.BasisModul("co2"));
        Assert.Equal("zuluft", InstanzModul.BasisModul("zuluft"));
        Assert.Equal("entfeuchter-zusatz", InstanzModul.BasisModul("entfeuchter-zusatz-i4"));
    }

    [Fact]
    public void Id_und_Name_bekommen_die_Endung_und_passen_zusammen()
    {
        Assert.Equal("input_number.chiller_zieltemperatur_tag_i2", InstanzModul.Id("input_number.chiller_zieltemperatur_tag", 2));
        Assert.Equal("Chiller Zieltemperatur Tag I2", InstanzModul.Name("Chiller Zieltemperatur Tag", 2));
        // Home Assistant leitet die Kennung aus dem Namen ab — beides muss zusammenpassen.
        var ausName = "input_number." + Regex.Replace("Chiller Zieltemperatur Tag I2".ToLowerInvariant(), @"\s+", "_");
        Assert.Equal(InstanzModul.Id("input_number.chiller_zieltemperatur_tag", 2), ausName);
        Assert.Throws<ArgumentException>(() => InstanzModul.Id("chiller_ohne_domaene", 2));
    }

    [Fact]
    public void Gemeinsame_Bausteine_behalten_Kennung_und_Namen()
    {
        Assert.Equal("sensor.absolute_feuchte_draussen", InstanzModul.Id("sensor.absolute_feuchte_draussen", 2));
        Assert.Equal("Absolute Feuchte draussen", InstanzModul.Name("Absolute Feuchte draussen", 2, "sensor.absolute_feuchte_draussen"));
    }

    [Fact]
    public void Text_ersetzt_nur_ganze_Kennungen_des_Katalogs()
    {
        string[] ids = ["input_number.chiller_hysterese", "sensor.chiller_zieltemperatur_aktiv", "sensor.absolute_feuchte_keller"];
        var vorlage = "{{ states('input_number.chiller_hysterese') }} {{ states('input_number.chiller_hysterese_extra') }} "
                      + "{{ states('[[wasser_temp]]') }} {{ states('sensor.chiller_zieltemperatur_aktiv') }} {{ states('sensor.absolute_feuchte_keller') }} "
                      + "{{ states('input_number.fremd') }}";

        var ergebnis = InstanzModul.Text(vorlage, 2, ids);

        Assert.Contains("states('input_number.chiller_hysterese_i2')", ergebnis);
        Assert.Contains("input_number.chiller_hysterese_extra')", ergebnis); // anderes Wort: unberührt
        Assert.DoesNotContain("input_number.chiller_hysterese_extra_i2", ergebnis);
        Assert.Contains("sensor.chiller_zieltemperatur_aktiv_i2", ergebnis);
        Assert.Contains("[[wasser_temp]]", ergebnis); // Rollen gehören dem Nutzer
        Assert.Contains("sensor.absolute_feuchte_keller')", ergebnis); // gemeinsam
        Assert.Contains("input_number.fremd')", ergebnis); // nicht im Katalog der Art
    }

    // ------------------------------------------------------------------ Katalog

    public static TheoryData<string, int> ArtenUndNummern()
    {
        var daten = new TheoryData<string, int>();
        foreach (var art in InstanzModul.Arten) foreach (var n in new[] { 2, 3 }) daten.Add(art, n);
        return daten;
    }

    [Fact]
    public void Der_Bestand_ist_ohne_Instanz_unveraendert()
    {
        foreach (var art in InstanzModul.Arten)
        {
            var erwartet = SteuerungBauteile.Alle.Where(b => b.Modul == art).Select(b => b.EntityId).ToList();
            Assert.Equal(erwartet, SteuerungBauteile.FuerModul(art).Select(b => b.EntityId).ToList());
        }
        Assert.Equal(["entfeuchter", "entfeuchter-zusatz", "zuluft", "chiller", "licht"], SteuerungZeltService.Zuweisbar);
        Assert.Empty(SteuerungInstanzen.Module);
    }

    [Theory]
    [MemberData(nameof(ArtenUndNummern))]
    public void Der_Katalog_einer_Instanz_hat_dieselben_Bauteile_mit_eigenen_Kennungen(string art, int nummer)
    {
        var basis = SteuerungBauteile.FuerModul(art);
        var modul = InstanzModul.Fuer(art, nummer);
        var instanz = SteuerungBauteile.FuerModul(modul);

        Assert.Equal(basis.Count, instanz.Count);
        Assert.All(instanz, b => Assert.Equal(modul, b.Modul));
        for (var i = 0; i < basis.Count; i++)
        {
            var gemeinsam = InstanzModul.Gemeinsam.Contains(basis[i].EntityId);
            Assert.Equal(gemeinsam ? basis[i].EntityId : basis[i].EntityId + "_i" + nummer, instanz[i].EntityId);
            Assert.Equal(basis[i].Domaene, instanz[i].Domaene);
            Assert.Equal(basis[i].Art, instanz[i].Art);
            Assert.Equal(gemeinsam ? basis[i].Name : basis[i].Name + " I" + nummer, instanz[i].Name);
        }
        Assert.Equal(instanz.Count, instanz.Select(b => b.EntityId).Distinct(StringComparer.OrdinalIgnoreCase).Count()); // keine Doppelten
    }

    [Theory]
    [MemberData(nameof(ArtenUndNummern))]
    public void Rechenvorschriften_einer_Instanz_nennen_keine_Kennung_der_ersten(string art, int nummer)
    {
        var basis = SteuerungBauteile.FuerModul(art);
        var eigene = basis.Select(b => b.EntityId).Where(k => !InstanzModul.Gemeinsam.Contains(k)).ToList();
        foreach (var b in SteuerungBauteile.FuerModul(InstanzModul.Fuer(art, nummer)))
        {
            foreach (var text in new[] { b.Vorlage, b.Verfuegbarkeit }.Where(t => t is not null))
                AssertOhneFremdeKennung(text!, eigene, nummer, $"{b.EntityId}");
        }
    }

    [Theory]
    [MemberData(nameof(ArtenUndNummern))]
    public void Jede_mitgelieferte_Automationsvorlage_wird_ohne_Rest_der_ersten_Instanz_umgestellt(string art, int nummer)
    {
        var ordner = Path.Combine(ProjektWurzel(), "GrowDiary.Web", "Vorlagen", art);
        Assert.True(Directory.Exists(ordner) || art == "licht", $"Kein Vorlagenordner für {art}");
        if (!Directory.Exists(ordner)) return;
        var eigene = SteuerungBauteile.FuerModul(art).Select(b => b.EntityId).Where(k => !InstanzModul.Gemeinsam.Contains(k)).ToList();
        var modul = InstanzModul.Fuer(art, nummer);

        foreach (var datei in Directory.EnumerateFiles(ordner, "*.json"))
        {
            var original = (JsonObject)JsonNode.Parse(File.ReadAllText(datei))!;
            var vorher = original.ToJsonString();
            var ergebnis = InstanzModul.Vorlage(original, modul, eigene);

            Assert.Equal(vorher, original.ToJsonString()); // die Eingabe bleibt unverändert
            var alias = ergebnis["alias"]!.GetValue<string>();
            Assert.EndsWith($" I{nummer}", alias);
            Assert.Equal(original["alias"]!.GetValue<string>() + $" I{nummer}", alias);
            AssertOhneFremdeKennung(ergebnis.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), eigene, nummer, Path.GetFileName(datei));
            // Rollen-Platzhalter bleiben, wie sie sind.
            Assert.Equal(Regex.Matches(vorher, @"\[\[[a-z_0-9]+\]\]").Count, Regex.Matches(ergebnis.ToJsonString(), @"\[\[[a-z_0-9]+\]\]").Count);
        }
    }

    [Fact]
    public void Die_Vorlage_der_ersten_Instanz_bleibt_unveraendert()
    {
        var vorlage = new JsonObject { ["alias"] = "Water Chiller Regelung", ["x"] = "input_number.chiller_hysterese" };
        var gleich = InstanzModul.Vorlage(vorlage, "chiller", ["input_number.chiller_hysterese"]);
        Assert.Same(vorlage, gleich);
    }

    [Fact]
    public void Rollen_einer_Instanz_sind_die_der_Art()
    {
        foreach (var art in InstanzModul.Arten.Where(a => SteuerungGeraeteRollen.FuerModul(a).Count > 0))
        {
            Assert.Equal(
                SteuerungGeraeteRollen.FuerModul(art).Select(r => r.Schluessel).ToList(),
                SteuerungGeraeteRollen.FuerModul(InstanzModul.Fuer(art, 2)).Select(r => r.Schluessel).ToList());
        }
    }

    [Fact]
    public void Bauteile_weiterer_Instanzen_sind_ueber_die_Kennung_auffindbar()
    {
        Assert.Null(SteuerungBauteile.FindenNachId("input_number.chiller_hysterese_i2"));
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "chiller", Nummer = 2, Name = "Reserve" }]);
        var b = SteuerungBauteile.FindenNachId("input_number.chiller_hysterese_i2");
        Assert.NotNull(b);
        Assert.Equal("chiller-i2", b!.Modul);
        Assert.Equal("fork_ai_chiller-i2_regelung", SteuerungBauteile.AutomationsKennung(b.Modul, "regelung"));
    }

    // ------------------------------------------------------------------ Register

    private static (SteuerungInstanzService Dienst, SteuerungRepository Repo, string Wurzel) Dienst()
    {
        var wurzel = Path.Combine(Path.GetTempPath(), "Instanzen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wurzel);
        var pfade = new AppPaths(wurzel);
        TestDatabase.InitializeWithDefaultTent(pfade);
        var repo = new SteuerungRepository(pfade);
        return (new SteuerungInstanzService(repo), repo, wurzel);
    }

    [Fact]
    public void Nummern_zaehlen_je_Art_ab_2_und_werden_nie_wiederverwendet()
    {
        var (dienst, repo, wurzel) = Dienst();
        try
        {
            var a = dienst.Anlegen("chiller", "Reserve").Instanz!;
            var b = dienst.Anlegen("chiller", "Zelt B").Instanz!;
            var c = dienst.Anlegen("entfeuchter", "Links").Instanz!;
            Assert.Equal([2, 3, 2], [a.Nummer, b.Nummer, c.Nummer]);
            Assert.Equal("chiller-i3", b.Modul);

            Assert.Null(dienst.Entfernen("chiller-i3"));
            var d = dienst.Anlegen("chiller", "Noch einer").Instanz!;
            Assert.Equal(4, d.Nummer); // 3 war vergeben, auch wenn sie entfernt wurde

            // Überlebt einen Neustart: aus dem Gespeicherten wird das Register gesetzt.
            SteuerungInstanzen.Festlegen([]);
            SteuerungInstanzService.Aktualisieren(repo);
            Assert.Equal(["chiller-i2", "chiller-i4"], SteuerungInstanzen.ModuleVon("chiller"));
            Assert.Equal(["entfeuchter-i2"], SteuerungInstanzen.ModuleVon("entfeuchter"));
        }
        finally { try { Directory.Delete(wurzel, true); } catch { /* Windows haelt manchmal fest */ } }
    }

    [Fact]
    public void Anlegen_prueft_Art_und_Namen()
    {
        var (dienst, _, wurzel) = Dienst();
        try
        {
            Assert.NotNull(dienst.Anlegen("co2", "Flasche").Fehler);
            Assert.NotNull(dienst.Anlegen("chiller", "  ").Fehler);
            Assert.NotNull(dienst.Anlegen("chiller", new string('x', SteuerungInstanzService.NameMax + 1)).Fehler);
            Assert.Null(dienst.Anlegen("chiller", "Reserve").Fehler);
            Assert.NotNull(dienst.Anlegen("chiller", "reserve").Fehler); // gleicher Name in derselben Art
            Assert.Null(dienst.Anlegen("zuluft", "Reserve").Fehler);      // anderer Art: erlaubt
            Assert.NotNull(dienst.Umbenennen("chiller-i9", "X"));
            Assert.Null(dienst.Umbenennen("chiller-i2", "Haupttank 2"));
            Assert.Equal("Haupttank 2", dienst.Liste().Single(i => i.Modul == "chiller-i2").Name);
        }
        finally { try { Directory.Delete(wurzel, true); } catch { /* Windows haelt manchmal fest */ } }
    }

    [Fact]
    public void Das_Register_speist_Zuweisung_und_Auswahl()
    {
        var (dienst, _, wurzel) = Dienst();
        try
        {
            dienst.Anlegen("chiller", "Reserve");
            Assert.Contains("chiller-i2", SteuerungZeltService.Zuweisbar);
            var art = SteuerungAuswahlService.Finden("chiller")!;
            Assert.Equal(["chiller", "chiller-i2"], SteuerungAuswahlService.ModuleVon(art));
            Assert.Contains("chiller-i2", SteuerungAuswahlService.Bausteinmodule(art));
            // Die Auswahl „entfeuchter“ umfasst Entfeuchter UND Zusatz — Instanzen kommen hinter ihre Art.
            dienst.Anlegen("entfeuchter", "Entfeuchter 2");
            dienst.AnlegenZusatz("entfeuchter-i2", "Zusatz 2");
            var e = SteuerungAuswahlService.Finden("entfeuchter")!;
            Assert.Equal(["entfeuchter", "entfeuchter-i2", "entfeuchter-zusatz", "entfeuchter-zusatz-i2"], SteuerungAuswahlService.ModuleVon(e));
        }
        finally { try { Directory.Delete(wurzel, true); } catch { /* Windows haelt manchmal fest */ } }
    }

    // ------------------------------------------------------------------ Hilfen

    private static void AssertOhneFremdeKennung(string text, IReadOnlyList<string> eigene, int nummer, string wo)
    {
        foreach (var k in eigene)
        {
            var roh = new Regex(@"(?<![A-Za-z0-9_.])" + Regex.Escape(k) + @"(?![A-Za-z0-9_])");
            Assert.False(roh.IsMatch(text), $"{wo}: „{k}“ steht noch ohne Endung _i{nummer} da.");
        }
    }

    private static string ProjektWurzel()
    {
        var ordner = new DirectoryInfo(AppContext.BaseDirectory);
        while (ordner is not null && !Directory.Exists(Path.Combine(ordner.FullName, "GrowDiary.Web", "wwwroot"))) ordner = ordner.Parent;
        return ordner?.FullName ?? throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }
}
