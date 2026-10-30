using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests;

/// <summary>
/// „Zelt ruhend" (cc.277): die Regeln, die ohne Home Assistant prüfbar sind.
///
/// <para>Der Rest (Automatik aus, Gerät aus, Zustand merken, wieder herstellen) hängt an sechs Steuerungsdiensten und läuft
/// im Demobestand durch <c>e2e/zelt-ruhend.spec.ts</c> — dort ändert der Bestand seine Zustände wirklich mit.</para>
/// </summary>
public sealed class ZeltRuheTests
{
    [Theory]
    // AC Infinity: die Betriebsart ist ein Auswahlfeld; „Off" heißt aus, alles andere läuft.
    [InlineData("select", "Off", "aus", false)]
    [InlineData("select", "off", "aus", false)]
    [InlineData("select", "On", "an", true)]
    [InlineData("select", "Schedule", "Zeitplan", true)]
    [InlineData("switch", "on", "an", true)]
    [InlineData("switch", "off", "aus", false)]
    [InlineData("input_boolean", "ON", "an", true)]
    // Nicht erreichbar ist weder an noch aus: wer das als „aus" liest, schaltet ein totes Gerät „ab".
    [InlineData("select", "unavailable", "nicht erreichbar", null)]
    [InlineData("switch", "unknown", "nicht erreichbar", null)]
    [InlineData("select", "", "nicht erreichbar", null)]
    public void Beschreiben_nennt_den_Zustand_in_Worten(string domain, string zustand, string erwartet, bool? laeuft)
    {
        var (text, l) = ZeltRuheService.Beschreiben(domain, zustand);
        Assert.Equal(erwartet, text);
        Assert.Equal(laeuft, l);
    }

    [Fact]
    public void Beschreiben_ohne_Zustand_ist_nicht_erreichbar()
    {
        var (text, laeuft) = ZeltRuheService.Beschreiben("select", null);
        Assert.Equal("nicht erreichbar", text);
        Assert.Null(laeuft);
    }

    [Fact]
    public void Ausschalten_eines_Auswahlfelds_waehlt_Off()
    {
        var aktion = ZeltRuheService.Ausschalten("select.rdwc_venti_aktiver_modus");
        Assert.NotNull(aktion);
        Assert.Equal(("select", "select_option"), (aktion!.Value.Domain, aktion.Value.Dienst));
        Assert.Equal("Off", aktion.Value.Daten!["option"]);
    }

    [Theory]
    [InlineData("switch.grow_dehumi_tent", "switch")]
    [InlineData("input_boolean.irgendwas", "input_boolean")]
    public void Ausschalten_einer_Steckdose_ist_turn_off(string entity, string domain)
    {
        var aktion = ZeltRuheService.Ausschalten(entity);
        Assert.Equal((domain, "turn_off"), (aktion!.Value.Domain, aktion.Value.Dienst));
        Assert.Null(aktion.Value.Daten);
    }

    [Theory]
    [InlineData("sensor.etwas")]
    [InlineData("number.etwas")]
    [InlineData("")]
    public void Ausschalten_kennt_nur_schaltbare_Arten(string entity)
        => Assert.Null(ZeltRuheService.Ausschalten(entity));

    [Fact]
    public void Wiederherstellen_stellt_genau_den_Zustand_von_vorher_her()
    {
        // Ein Controller, der auf „Schedule" stand, kommt auf „Schedule" zurück — nicht auf „On".
        var auswahl = ZeltRuheService.Wiederherstellen("select.klein_abluft_aktiver_modus", "Schedule");
        Assert.Equal("Schedule", auswahl!.Value.Daten!["option"]);
        Assert.Equal("select_option", auswahl.Value.Dienst);

        // Eine Steckdose, die an war, geht wieder an; eine, die schon aus war, bleibt aus.
        Assert.Equal("turn_on", ZeltRuheService.Wiederherstellen("switch.a", "on")!.Value.Dienst);
        Assert.Equal("turn_off", ZeltRuheService.Wiederherstellen("switch.a", "off")!.Value.Dienst);
    }

    [Theory]
    // Ohne gültigen Zustand von vorher wird nichts geraten.
    [InlineData("unavailable")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Wiederherstellen_ohne_gueltigen_Zustand_schaltet_nichts(string? vorher)
        => Assert.Null(ZeltRuheService.Wiederherstellen("select.x", vorher));

    [Fact]
    public void Standard_schaltet_Entfeuchter_und_Zuluft_nicht_aus()
    {
        var alle = ZeltRuheArten.Alle.Select(a => a.Kennung).ToList();
        var standard = ZeltRuheArten.Standard(alle);

        Assert.DoesNotContain(ZeltRuheArten.Entfeuchter, standard);
        Assert.DoesNotContain(ZeltRuheArten.Zuluft, standard);
        Assert.Contains(ZeltRuheArten.Licht, standard);
        Assert.Contains(ZeltRuheArten.Co2, standard);
    }

    [Fact]
    public void Standard_nennt_nur_vorhandene_Geraete()
    {
        var standard = ZeltRuheArten.Standard([ZeltRuheArten.Licht, ZeltRuheArten.Zuluft]);
        Assert.Equal([ZeltRuheArten.Licht], standard);
    }

    [Fact]
    public void Wirksam_folgt_der_gespeicherten_Auswahl_aber_nur_fuer_vorhandene_Geraete()
    {
        var vorhanden = new[] { ZeltRuheArten.Licht, ZeltRuheArten.Zuluft };

        // Gespeichert: Zuluft ausdrücklich dazu, Abluft gibt es nicht.
        var wirksam = ZeltRuheService.Wirksam([ZeltRuheArten.Zuluft, ZeltRuheArten.Abluft], vorhanden);
        Assert.Equal([ZeltRuheArten.Zuluft], wirksam);

        // Nie gewählt: die Vorgabe.
        Assert.Equal([ZeltRuheArten.Licht], ZeltRuheService.Wirksam(null, vorhanden));

        // Bewusst alles abgehakt: nichts, nicht „dann eben die Vorgabe".
        Assert.Empty(ZeltRuheService.Wirksam([], vorhanden));
    }

    [Fact]
    public void Jede_Art_zeigt_auf_eine_Rolle_die_es_gibt()
    {
        // Zählung über die Grundmenge (CLAUDE.md): eine Art ohne Rolle im Katalog ließe sich nie zuordnen und nie ausschalten.
        Assert.True(ZeltRuheArten.Alle.Count >= 7, "Die Zählung sieht ihre Grundmenge nicht.");
        foreach (var art in ZeltRuheArten.Alle)
        {
            var rolle = SteuerungGeraeteRollen.Alle.SingleOrDefault(r => r.Modul == art.Modul && r.Schluessel == art.Rolle);
            Assert.True(rolle is not null, $"„{art.Titel}“ zeigt auf {art.Modul}/{art.Rolle} — diese Rolle gibt es nicht.");
            Assert.Contains(rolle!.Domains, d => d is "select" or "switch" or "input_boolean");
        }
    }

    [Fact]
    public void Kennungen_sind_eindeutig_und_Finden_unterscheidet_nicht_nach_Gross_und_Klein()
    {
        var kennungen = ZeltRuheArten.Alle.Select(a => a.Kennung).ToList();
        Assert.Equal(kennungen.Count, kennungen.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ZeltRuheArten.Abluft, ZeltRuheArten.Finden("ABLUFT")?.Kennung);
        Assert.Null(ZeltRuheArten.Finden("nichtda"));
    }
}
