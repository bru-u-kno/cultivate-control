using GrowDiary.Web.Models;

namespace GrowDiary.Web.Tests.Models;

/// <summary>Namenslogik der CO₂-Steuerung je Zelt: das Bestand-Zelt behält alles, weitere Zelte bekommen eigene Kennungen.</summary>
public sealed class Co2ModulTests
{
    [Fact]
    public void BestandZelt_BehaeltDasBasisModul_JedesAndereBekommtEigenes()
    {
        Assert.Equal("co2", Co2Modul.Fuer(1, bestandZeltId: 1));
        Assert.Equal("co2-z2", Co2Modul.Fuer(2, bestandZeltId: 1));
        Assert.Equal("co2-z7", Co2Modul.Fuer(7, bestandZeltId: null));
    }

    [Theory]
    [InlineData("co2", true)]
    [InlineData("co2-z2", true)]
    [InlineData("co2-z17", true)]
    [InlineData("co2-z0", false)]
    [InlineData("co2-z", false)]
    [InlineData("co2-zx", false)]
    [InlineData("co2-z2-extra", false)]
    [InlineData("entfeuchter", false)]
    [InlineData(null, false)]
    public void IstCo2_ErkenntNurBasisUndZeltModule(string? modul, bool erwartet)
        => Assert.Equal(erwartet, Co2Modul.IstCo2(modul));

    [Fact]
    public void ZeltId_UndBasisModul_LesenDasZeltAusDemModul()
    {
        Assert.Equal(2, Co2Modul.ZeltId("co2-z2"));
        Assert.Null(Co2Modul.ZeltId("co2"));
        Assert.Null(Co2Modul.ZeltId("zuluft"));
        Assert.Equal("co2", Co2Modul.BasisModul("co2-z9"));
        Assert.Equal("co2", Co2Modul.BasisModul("co2"));
        Assert.Equal("zuluft", Co2Modul.BasisModul("zuluft"));
    }

    [Theory]
    [InlineData("input_number.co2_zielwert", "input_number.co2_z2_zielwert")]
    [InlineData("automation.co2_wachter", "automation.co2_z2_wachter")]
    [InlineData("binary_sensor.co2_klima_ok", "binary_sensor.co2_z2_klima_ok")]
    [InlineData("counter.co2_impulse_heute", "counter.co2_z2_impulse_heute")]
    public void Id_SetztDieZeltKennungHinterDasPraefix(string bestand, string erwartet)
        => Assert.Equal(erwartet, Co2Modul.Id(bestand, 2));

    [Fact]
    public void Id_OhneZelt_BleibtDieBestandKennung_UndFremdeKennungenLehnenSieAb()
    {
        Assert.Equal("input_number.co2_zielwert", Co2Modul.Id("input_number.co2_zielwert", null));
        Assert.Throws<ArgumentException>(() => Co2Modul.Id("sensor.big_co2_light_sensor_co2", 2));
        Assert.Throws<ArgumentException>(() => Co2Modul.Id("co2_zielwert", 2));
    }

    [Theory]
    [InlineData("CO2 Zielwert", "CO2 Z3 Zielwert")]
    [InlineData("CO2 Wächter", "CO2 Z3 Wächter")]
    public void Name_PasstZurKennung(string bestand, string erwartet)
        => Assert.Equal(erwartet, Co2Modul.Name(bestand, 3));

    [Fact]
    public void Name_UndKennung_ErgebenNachHomeAssistantRegelDieselbeObjektKennung()
    {
        // Home Assistant leitet die Objektkennung aus dem Namen ab: klein, Nicht-Alphanumerisches zu Unterstrich, ä → a.
        static string Slug(string name) => System.Text.RegularExpressions.Regex.Replace(
            name.ToLowerInvariant().Replace("ä", "a"), "[^a-z0-9]+", "_").Trim('_');
        foreach (var (id, name) in new[]
        {
            ("input_number.co2_ziel_mittel_25_bis_27_c", "CO2 Ziel mittel 25 bis 27 C"),
            ("automation.co2_wachter", "CO2 Wächter"),
            ("input_number.co2_g_s_letzte_messung", "CO2 g/s letzte Messung"),
            ("automation.co2_abluft_drosselung", "CO2 Abluft-Drosselung"),
        })
        {
            Assert.Equal(id.Split('.')[1], Slug(name)); // Bestand: stimmt schon
            Assert.Equal(Co2Modul.Id(id, 4).Split('.')[1], Slug(Co2Modul.Name(name, 4)));
        }
    }

    [Fact]
    public void Text_StelltAlleCo2KennungenUm_RollenPlatzhalterUndFremdesBleiben()
    {
        const string vorlage = "{{ states('input_number.co2_zielwert') }} {{ states('[[co2_sensor]]') }} "
            + "{{ is_state('binary_sensor.co2_bedarf','on') }} {{ states('sensor.big_co2_light_sensor_co2') }} "
            + "service: automation.turn_on target: automation.co2_dosierung";

        var z = Co2Modul.Text(vorlage, 2);

        Assert.Contains("input_number.co2_z2_zielwert", z);
        Assert.Contains("binary_sensor.co2_z2_bedarf", z);
        Assert.Contains("automation.co2_z2_dosierung", z);
        Assert.Contains("[[co2_sensor]]", z);                       // Rolle des Nutzers
        Assert.Contains("sensor.big_co2_light_sensor_co2", z);      // fremde Kennung
        Assert.DoesNotContain("co2_zielwert'", z.Replace("co2_z2_zielwert", ""));
        Assert.Equal(vorlage, Co2Modul.Text(vorlage, null));        // Bestand unverändert
    }
}
