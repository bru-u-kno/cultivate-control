using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Trocknungs-Ziel je Zelt (A-029, Etappe 3, Teil A): Ports des Controllers erkennen und den Plan gegen das Gerät halten.
/// </summary>
/// <remarks>
/// Die Kennungen haben die Form, die der AC-Infinity-Controller in Home Assistant vergibt
/// (<c>ac_infinity_&lt;Controller&gt;_port_5_atType</c> …); Controller, Namen und Entity-Ids sind Beispielwerte — keine Daten einer
/// echten Anlage. Die Erkennung hängt an der Form der Kennung, nicht am Anzeigenamen.
/// </remarks>
public sealed class TrocknungZielTests
{
    private const string Controller = "AABBCCDDEEFF";

    private static HerkunftEintrag E(string entityId, string deviceId, string? name, string schluessel, int port, string praefix = "ac_infinity")
        => new(entityId, deviceId, name, $"{praefix}_{Controller}_port_{port}_{schluessel}");

    /// <summary>Ein Entfeuchter-Port (Nr. 5) und ein Kühlungs-Port (Nr. 4) in der Form des Controllers, dazu fremdes Zeug.</summary>
    private static List<HerkunftEintrag> Register() =>
    [
        E("select.testport_entfeuchter_aktiver_modus", "dev-dehumi", "Testport Entfeuchter", "atType", 5),
        E("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "dev-dehumi", "Testport Entfeuchter", "devHh", 5),
        E("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "dev-dehumi", "Testport Entfeuchter", "activeHh", 5),
        E("number.testport_entfeuchter_temperatur_hoher_ausloser", "dev-dehumi", "Testport Entfeuchter", "devHt", 5),
        E("switch.testport_entfeuchter_temperatur_hoher_ausloser_aktiviert", "dev-dehumi", "Testport Entfeuchter", "activeHt", 5),
        E("select.testport_kuehlung_aktiver_modus", "dev-clima", "Testport Kühlung", "atType", 4),
        E("number.testport_kuehlung_temperatur_hoher_ausloser", "dev-clima", "Testport Kühlung", "devHt", 4),
        E("switch.testport_kuehlung_temperatur_hoher_ausloser_aktiviert", "dev-clima", "Testport Kühlung", "activeHt", 4),
        // Ein Gerät mit AC-Infinity-Kennung, aber ohne Modus: kein Port, der sich vergleichen ließe.
        E("sensor.testcontroller_temperatur", "dev-controller", "Testcontroller", "temperature", 0),
        // Fremde Integration mit ähnlicher Endung: darf nicht erkannt werden.
        new HerkunftEintrag("select.fremd_modus", "dev-fremd", "Fremdgerät", "andere_integration_AB12_port_1_atType"),
        new HerkunftEintrag("sensor.ohne_kennung", "dev-x", "Ohne", null),
    ];

    private static HomeAssistantState S(string entityId, string state, string? einheit = null)
        => new() { EntityId = entityId, State = state, UnitOfMeasurement = einheit };

    private static TrocknungZiel.Port Dehumi() => TrocknungZiel.Erkennen(Register()).Single(p => p.Name == "Testport Entfeuchter");
    private static TrocknungZiel.Port Clima() => TrocknungZiel.Erkennen(Register()).Single(p => p.Name == "Testport Kühlung");

    private static Func<string, HomeAssistantState?> Zustaende(params HomeAssistantState[] zustaende)
    {
        var map = zustaende.ToDictionary(z => z.EntityId, StringComparer.OrdinalIgnoreCase);
        return id => map.GetValueOrDefault(id);
    }

    // ---- Erkennung --------------------------------------------------------------------------

    [Fact]
    public void Ports_werden_an_der_stabilen_Kennung_erkannt_nicht_am_Namen()
    {
        var ports = TrocknungZiel.Erkennen(Register());

        Assert.Equal(["Testport Entfeuchter", "Testport Kühlung"], ports.Select(p => p.Name).ToArray());
        var dehumi = Dehumi();
        Assert.Equal(5, dehumi.Nummer);
        Assert.Equal(Controller, dehumi.Controller);
        Assert.Equal("select.testport_entfeuchter_aktiver_modus", dehumi.ModusEntity);
        Assert.Equal("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", dehumi.FeuchteWertEntity);
        Assert.Equal("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", dehumi.FeuchteAktivEntity);
        Assert.Null(Clima().FeuchteWertEntity); // der Clima-Port hat im Register keine Feuchte-Obergrenze
        Assert.Equal("number.testport_kuehlung_temperatur_hoher_ausloser", Clima().TempWertEntity);
    }

    [Fact]
    public void Ein_leeres_Register_ergibt_keine_Ports()
        => Assert.Empty(TrocknungZiel.Erkennen([]));

    // ---- Vergleich --------------------------------------------------------------------------

    [Fact]
    public void Der_Dehumi_mit_50_Prozent_weicht_vom_Plan_62_ab()
    {
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "50", "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"));

        var v = TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), z, 62);

        Assert.Equal(TrocknungZiel.Urteil.WeichtAb, v.Urteil);
        Assert.Equal(50, v.WertAmGeraet);
        Assert.Equal(62, v.PlanWert);
        Assert.Equal("Testport Entfeuchter", v.PortName);
    }

    [Fact]
    public void Stimmt_der_Ausloeser_im_Rahmen_der_Toleranz_passt_es()
    {
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "61.5", "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"));
        Assert.Equal(TrocknungZiel.Urteil.Passt, TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), z, 62).Urteil);
    }

    [Fact]
    public void Ein_Port_auf_Aus_ist_Geraet_aus_und_ein_Port_im_Dauerbetrieb_hat_keine_Automatik()
    {
        var aus = Zustaende(S("select.testport_kuehlung_aktiver_modus", "Off"));
        Assert.Equal(TrocknungZiel.Urteil.GeraetAus, TrocknungZiel.Vergleiche(TrocknungZiel.RolleKuehlung, Clima(), aus, 21).Urteil);

        var an = Zustaende(S("select.testport_kuehlung_aktiver_modus", "On"));
        Assert.Equal(TrocknungZiel.Urteil.OhneAutomatik, TrocknungZiel.Vergleiche(TrocknungZiel.RolleKuehlung, Clima(), an, 21).Urteil);
    }

    [Fact]
    public void Ein_ausgeschalteter_Ausloeser_ist_AusloeserAus_auch_wenn_der_Wert_stimmt()
    {
        var z = Zustaende(
            S("select.testport_kuehlung_aktiver_modus", "Auto"),
            S("number.testport_kuehlung_temperatur_hoher_ausloser", "21", "°C"),
            S("switch.testport_kuehlung_temperatur_hoher_ausloser_aktiviert", "off"));
        Assert.Equal(TrocknungZiel.Urteil.AusloeserAus, TrocknungZiel.Vergleiche(TrocknungZiel.RolleKuehlung, Clima(), z, 21).Urteil);
    }

    [Fact]
    public void Nicht_erreichbar_ist_unbekannt_nicht_Abweichung_und_ohne_Port_ist_nicht_zugeordnet()
    {
        var weg = Zustaende(S("select.testport_entfeuchter_aktiver_modus", "unavailable"));
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), weg, 62).Urteil);
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), _ => null, 62).Urteil);
        Assert.Equal(TrocknungZiel.Urteil.NichtZugeordnet, TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, null, _ => null, 62).Urteil);
    }

    [Fact]
    public void Fahrenheit_ergibt_unbekannt_statt_einer_falschen_Abweichung()
    {
        var z = Zustaende(
            S("select.testport_kuehlung_aktiver_modus", "Auto"),
            S("number.testport_kuehlung_temperatur_hoher_ausloser", "70", "°F"),
            S("switch.testport_kuehlung_temperatur_hoher_ausloser_aktiviert", "on"));
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, TrocknungZiel.Vergleiche(TrocknungZiel.RolleKuehlung, Clima(), z, 21).Urteil);
    }

    // ---- Dienst: nur lesend, mit Ports und Zuordnung --------------------------------------------

    [Fact]
    public async Task Der_Dienst_vergleicht_beide_Rollen_und_unbekannte_Zuordnung_ist_unbekannt()
    {
        var ports = TrocknungZiel.Erkennen(Register());
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "50", "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"),
            S("select.testport_kuehlung_aktiver_modus", "Off"));
        var dienst = new TrocknungZielService(_ => Task.FromResult<IReadOnlyList<TrocknungZiel.Port>?>(ports), (id, _) => Task.FromResult(z(id)));

        var (erreichbar, vergleiche) = await dienst.VergleicheAsync(
            new Dictionary<string, string> { [TrocknungZiel.RolleEntfeuchter] = "dev-dehumi", [TrocknungZiel.RolleKuehlung] = "dev-clima" }, 62, 21, CancellationToken.None);
        Assert.True(erreichbar);
        Assert.Equal(TrocknungZiel.Urteil.WeichtAb, vergleiche.Single(v => v.Rolle == TrocknungZiel.RolleEntfeuchter).Urteil);
        Assert.Equal(TrocknungZiel.Urteil.GeraetAus, vergleiche.Single(v => v.Rolle == TrocknungZiel.RolleKuehlung).Urteil);

        var (_, ohne) = await dienst.VergleicheAsync(
            new Dictionary<string, string> { [TrocknungZiel.RolleEntfeuchter] = "gibt-es-nicht" }, 62, 21, CancellationToken.None);
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, ohne.Single(v => v.Rolle == TrocknungZiel.RolleEntfeuchter).Urteil);
        Assert.Equal(TrocknungZiel.Urteil.NichtZugeordnet, ohne.Single(v => v.Rolle == TrocknungZiel.RolleKuehlung).Urteil);
    }

    [Fact]
    public async Task Ist_Home_Assistant_nicht_erreichbar_meldet_der_Dienst_es_und_die_Portliste_wird_kurz_gemerkt()
    {
        var abrufe = 0;
        var dienst = new TrocknungZielService(_ => { abrufe++; return Task.FromResult<IReadOnlyList<TrocknungZiel.Port>?>(null); }, (_, _) => Task.FromResult<HomeAssistantState?>(null));

        var zugeordnet = new Dictionary<string, string> { [TrocknungZiel.RolleEntfeuchter] = "dev-dehumi" };
        var (erreichbar, _) = await dienst.VergleicheAsync(zugeordnet, 62, 21, CancellationToken.None);
        await dienst.VergleicheAsync(zugeordnet, 62, 21, CancellationToken.None);
        Assert.False(erreichbar);
        Assert.Equal(1, abrufe); // zweiter Aufruf aus dem Gedächtnis

        dienst.Vergessen();
        await dienst.PortsAsync(frisch: false, CancellationToken.None);
        Assert.Equal(2, abrufe);
    }

    [Fact]
    public void Ohne_Plan_wird_der_Wert_gezeigt_aber_kein_Urteil_gefaellt()
    {
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "50", "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"));
        var v = TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), z, null);
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, v.Urteil);
        Assert.Equal(50, v.WertAmGeraet);
        Assert.Null(v.PlanWert);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("abc")]
    public void Eine_Zahl_die_keine_endliche_Zahl_ist_ergibt_unbekannt(string text)
    {
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", text, "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"));
        Assert.Equal(TrocknungZiel.Urteil.Unbekannt, TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), z, 62).Urteil);
    }

    [Fact]
    public void Der_Modus_wird_am_Optionsschluessel_gelesen_VPD_und_Timer_sind_keine_Automatik()
    {
        foreach (var modus in new[] { "VPD", "Timer to On", "Cycle", "Schedule", "CO2" })
        {
            var z = Zustaende(S("select.testport_kuehlung_aktiver_modus", modus));
            Assert.Equal(TrocknungZiel.Urteil.OhneAutomatik, TrocknungZiel.Vergleiche(TrocknungZiel.RolleKuehlung, Clima(), z, 21).Urteil);
        }
    }

    [Fact]
    public async Task Ohne_Zuordnung_fragt_der_Dienst_Home_Assistant_gar_nicht_erst()
    {
        var abrufe = 0;
        var dienst = new TrocknungZielService(_ => { abrufe++; return Task.FromResult<IReadOnlyList<TrocknungZiel.Port>?>(null); }, (_, _) => Task.FromResult<HomeAssistantState?>(null));
        var (erreichbar, vergleiche) = await dienst.VergleicheAsync(new Dictionary<string, string>(), 62, 21, CancellationToken.None);
        Assert.Equal(0, abrufe);
        Assert.True(erreichbar);
        Assert.All(vergleiche, v => Assert.Equal(TrocknungZiel.Urteil.NichtZugeordnet, v.Urteil));
    }

    [Fact]
    public void Ein_Komma_als_Dezimalzeichen_wird_gelesen_wie_ueberall_in_der_App()
    {
        var z = Zustaende(
            S("select.testport_entfeuchter_aktiver_modus", "Auto"),
            S("number.testport_entfeuchter_feuchtigkeit_hoher_ausloser", "61,5", "%"),
            S("switch.testport_entfeuchter_feuchtigkeit_hoher_ausloser_aktiviert", "on"));
        var v = TrocknungZiel.Vergleiche(TrocknungZiel.RolleEntfeuchter, Dehumi(), z, 62);
        Assert.Equal(61.5, v.WertAmGeraet);
        Assert.Equal(TrocknungZiel.Urteil.Passt, v.Urteil);
    }
}
