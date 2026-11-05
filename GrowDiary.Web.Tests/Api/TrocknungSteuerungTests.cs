using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Geräte am Trockenzelt folgen dem Plan (A-029, Etappe 3, Teil B): was geschrieben wird, was nie, und wie es zurückgeht.
/// Home Assistant ist ein Wörterbuch; das Setzen merkt sich jeden Aufruf.
/// </summary>
public sealed class TrocknungSteuerungTests : IDisposable
{
    private readonly string _wurzel;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPortRepository _ports;
    private readonly TrocknungSteuerungRepository _steuerung;
    private readonly Tent _trockenZelt;
    private readonly int _growId;
    private readonly AppPaths _paths;
    private readonly Dictionary<string, HomeAssistantState> _ha = [];
    private readonly List<(string Entity, double Wert)> _gesetzt = [];
    private bool _setzenGelingt = true;
    private Action? _beimSetzen;

    private static readonly IReadOnlyList<TrocknungZiel.Port> Ports =
    [
        new("dev-dehumi", "Testport Entfeuchter", "C", 5, "select.dehumi_modus", "number.dehumi_feuchte", "switch.dehumi_feuchte_an", null, null),
        new("dev-clima", "Testport Kühlung", "C", 4, "select.clima_modus", null, null, "number.clima_temp", "switch.clima_temp_an"),
        new("dev-abluft", "Testport Abluft", "C", 1, "select.abluft_modus", "number.abluft_feuchte", "switch.abluft_feuchte_an", "number.abluft_temp", "switch.abluft_temp_an"),
    ];

    public TrocknungSteuerungTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrocknungSteuerung_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        _paths = new AppPaths(_wurzel);
        var bluete = TestDatabase.InitializeWithDefaultTent(_paths);
        _grows = new GrowRepository(_paths);
        _ernten = new HarvestRepository(_paths);
        _ports = new TrocknungPortRepository(_paths);
        _steuerung = new TrocknungSteuerungRepository(_paths);
        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
        _growId = _grows.CreateGrow(new GrowRun { Name = "Charge A", TentId = bluete.Id, StartDate = DateTime.Today.AddDays(-90), Status = GrowStatus.Running });
        _ports.Set(_trockenZelt.Id, "entfeuchter", "dev-dehumi");
        _ports.Set(_trockenZelt.Id, "kuehlung", "dev-clima");
        ZustandSetzen("select.dehumi_modus", "Auto");
        ZustandSetzen("number.dehumi_feuchte", "50", "%");
        ZustandSetzen("switch.dehumi_feuchte_an", "on");
        ZustandSetzen("select.clima_modus", "Off");
        ZustandSetzen("number.clima_temp", "24", "°C");
        ZustandSetzen("switch.clima_temp_an", "on");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private void ZustandSetzen(string id, string wert, string? einheit = null)
        => _ha[id] = new HomeAssistantState { EntityId = id, State = wert, UnitOfMeasurement = einheit };

    private TrocknungSteuerungService Dienst()
    {
        var ziel = new TrocknungZielService(
            _ => Task.FromResult<IReadOnlyList<TrocknungZiel.Port>?>(Ports),
            (id, _) => Task.FromResult(_ha.GetValueOrDefault(id)),
            (id, wert, _) =>
            {
                _gesetzt.Add((id, wert));
                _beimSetzen?.Invoke();
                if (_setzenGelingt) ZustandSetzen(id, wert.ToString(System.Globalization.CultureInfo.InvariantCulture), _ha[id].UnitOfMeasurement);
                return Task.FromResult(_setzenGelingt);
            });
        return new TrocknungSteuerungService(_grows, _ernten, new TrocknungPlanQuelle(new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), new TrocknungPlanRepository(_paths)), _ports, _steuerung, ziel, NullLogger<TrocknungSteuerungService>.Instance);
    }

    private TrocknungSteuerungApiController Controller() => new(_grows, _ports, _steuerung, Dienst());

    private static T Wert<T>(ActionResult<T> ergebnis) where T : class
        => (ergebnis.Result as ObjectResult)?.Value as T ?? ergebnis.Value ?? throw new InvalidOperationException("Keine Antwort: " + ergebnis.Result);

    private void ErnteImTrockenzelt()
        => _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-1), DryingTentId = _trockenZelt.Id });

    private static TrocknungSteuerungRequest Anfrage(string betrieb, bool entfeuchter, bool kuehlung)
        => new() { Betrieb = betrieb, EntfeuchterFolgt = entfeuchter, KuehlungFolgt = kuehlung };

    [Fact]
    public async Task Nur_melden_schreibt_nichts_und_zeigt_was_geschrieben_wuerde()
    {
        ErnteImTrockenzelt();
        var c = Controller();

        var stand = Wert(await c.Setzen(_trockenZelt.Id, Anfrage("melden", true, true), default));

        Assert.Empty(_gesetzt);
        Assert.Equal("melden", stand.Betrieb);
        var kuehlung = stand.Geraete.Single(g => g.Rolle == "kuehlung");
        Assert.Equal("weichtAb", kuehlung.Urteil);
        Assert.Equal(24, kuehlung.WertAmGeraet);
        Assert.Equal(21, kuehlung.PlanWert);          // Band oben: 20 + 1
        Assert.Equal("Off", kuehlung.Modus);          // der Modus ist sichtbar, wird aber nie geändert
        Assert.Equal(62, stand.Geraete.Single(g => g.Rolle == "entfeuchter").PlanWert);
    }

    [Fact]
    public async Task Regeln_setzt_nur_die_Grenzen_der_eingeschalteten_Geraete_und_nur_number_Entitaeten()
    {
        ErnteImTrockenzelt();
        var c = Controller();

        // Nur die Kühlung folgt: der Entfeuchter (der Wert des Nutzers, 50 %) bleibt unangetastet.
        var stand = Wert(await c.Setzen(_trockenZelt.Id, Anfrage("regeln", false, true), default));

        Assert.Equal([("number.clima_temp", 21.0)], _gesetzt);
        Assert.Equal("50", _ha["number.dehumi_feuchte"].State);
        Assert.Equal("Off", _ha["select.clima_modus"].State);   // Modus unverändert
        Assert.Equal("on", _ha["switch.clima_temp_an"].State);  // Schalter unverändert
        var kuehlung = stand.Geraete.Single(g => g.Rolle == "kuehlung");
        Assert.Equal("passt", kuehlung.Urteil);
        Assert.Equal(24, kuehlung.Vorher);
        Assert.NotNull(kuehlung.ZuletztGesetzt);
        Assert.All(_gesetzt, g => Assert.StartsWith("number.", g.Entity));
    }

    [Fact]
    public async Task Ein_zweiter_Durchlauf_schreibt_nichts_und_der_Wert_davor_bleibt_beim_ersten()
    {
        ErnteImTrockenzelt();
        var c = Controller();
        await c.Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default);
        var nachErstem = _gesetzt.Count;
        Assert.Equal(2, nachErstem);

        Assert.Equal(0, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);
        Assert.Equal(nachErstem, _gesetzt.Count);

        // Nutzer verstellt von Hand: der nächste Lauf zieht nach, der Wert davor bleibt der allererste (24 bzw. 50).
        ZustandSetzen("number.clima_temp", "23", "°C");
        Assert.Equal(1, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);
        Assert.Equal(24, _steuerung.Merkers(_trockenZelt.Id)["kuehlung"].Vorher);
        Assert.Equal(50, _steuerung.Merkers(_trockenZelt.Id)["entfeuchter"].Vorher);
    }

    [Fact]
    public async Task Ein_Etappenwechsel_wird_beim_naechsten_Durchlauf_nachgezogen()
    {
        ErnteImTrockenzelt(); // Tag 2: Etappe 1 „Anfang“, oben 21 °C und 62 %
        await Controller().Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default);
        Assert.Equal("21", _ha["number.clima_temp"].State);
        Assert.Equal("62", _ha["number.dehumi_feuchte"].State);

        // Tag 5 = Etappe 2 „Hauptphase“ (19 °C, 58 %): oben 20 °C und 60 %.
        var ernte = _ernten.GetForGrow(_growId)!;
        ernte.HarvestedAt = DateTime.Today.AddDays(-4);
        _ernten.Update(ernte);
        Assert.Equal(2, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);

        Assert.Equal("20", _ha["number.clima_temp"].State);
        Assert.Equal("60", _ha["number.dehumi_feuchte"].State);
        Assert.Equal(24, _steuerung.Merkers(_trockenZelt.Id)["kuehlung"].Vorher); // Wert des Nutzers, nicht der der Etappe davor
    }

    [Fact]
    public async Task Zuruecksetzen_stellt_die_alten_Werte_wieder_ein_und_geht_auf_Nur_melden()
    {
        ErnteImTrockenzelt();
        var c = Controller();
        await c.Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default);
        Assert.Equal("62", _ha["number.dehumi_feuchte"].State);

        var zurueck = Wert(await c.Zuruecksetzen(_trockenZelt.Id, default));

        Assert.Equal(2, zurueck.Zurueck);
        Assert.Equal("50", _ha["number.dehumi_feuchte"].State);
        Assert.Equal("24", _ha["number.clima_temp"].State);
        Assert.Equal("melden", zurueck.Stand.Betrieb);
        Assert.All(zurueck.Stand.Geraete, g => Assert.False(g.Folgt));
        Assert.Empty(_steuerung.Merkers(_trockenZelt.Id));
        // Danach schreibt der Hintergrundlauf nichts mehr.
        var n = _gesetzt.Count;
        await Dienst().AbgleichenAsync(_trockenZelt.Id, default);
        Assert.Equal(n, _gesetzt.Count);
    }

    [Fact]
    public async Task Scheitert_das_Setzen_bleibt_kein_falscher_Merker_und_Zuruecksetzen_behaelt_ihn()
    {
        ErnteImTrockenzelt();
        _setzenGelingt = false;
        var c = Controller();
        await c.Setzen(_trockenZelt.Id, Anfrage("regeln", false, true), default);
        Assert.Empty(_steuerung.Merkers(_trockenZelt.Id)); // nichts gesetzt → nichts gemerkt

        _setzenGelingt = true;
        await Dienst().AbgleichenAsync(_trockenZelt.Id, default);
        Assert.Single(_steuerung.Merkers(_trockenZelt.Id));

        _setzenGelingt = false;
        var zurueck = Wert(await c.Zuruecksetzen(_trockenZelt.Id, default));
        Assert.Equal(1, zurueck.Gescheitert);
        Assert.Single(_steuerung.Merkers(_trockenZelt.Id)); // zweiter Versuch bleibt möglich
        Assert.Equal("melden", zurueck.Stand.Betrieb);       // der Fork fasst trotzdem nichts mehr an
    }

    [Fact]
    public async Task Steht_ein_Port_auf_Aus_und_meldet_die_Grenze_nicht_verfuegbar_ist_es_Geraet_aus_und_es_wird_nichts_geschrieben()
    {
        // So verhält sich der echte Controller: Modus Aus → die Entität der Temperatur-Obergrenze ist „unavailable“.
        ZustandSetzen("number.clima_temp", "unavailable");
        ErnteImTrockenzelt();
        var c = Controller();

        var stand = Wert(await c.Setzen(_trockenZelt.Id, Anfrage("regeln", false, true), default));

        Assert.Empty(_gesetzt);
        Assert.Equal("geraetAus", stand.Geraete.Single(g => g.Rolle == "kuehlung").Urteil);
        // Sobald der Port auf Auto steht, zieht der nächste Durchlauf nach.
        ZustandSetzen("select.clima_modus", "Auto");
        ZustandSetzen("number.clima_temp", "24", "°C");
        Assert.Equal(1, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);
        Assert.Equal("21", _ha["number.clima_temp"].State);
    }

    [Fact]
    public async Task Ohne_Trocknung_ohne_Port_und_bei_falschen_Angaben_wird_nichts_geschrieben_oder_abgewiesen()
    {
        var c = Controller();
        // Es trocknet nichts: Regeln einschalten ist erlaubt, geschrieben wird nichts.
        Wert(await c.Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default));
        Assert.Empty(_gesetzt);

        Assert.IsType<BadRequestObjectResult>((await c.Setzen(_trockenZelt.Id, Anfrage("1", false, false), default)).Result);
        var gewoehnlich = _grows.CreateTent(new Tent { Name = "Gewöhnlich", TentType = TentType.Production });
        Assert.IsType<BadRequestObjectResult>((await c.Setzen(gewoehnlich.Id, Anfrage("melden", false, false), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await c.Setzen(99999, Anfrage("melden", false, false), default)).Result);

        _ports.Set(_trockenZelt.Id, "kuehlung", null);
        Assert.IsType<BadRequestObjectResult>((await c.Setzen(_trockenZelt.Id, Anfrage("melden", false, true), default)).Result);
    }

    [Fact]
    public void Entscheide_rundet_haelt_die_Spanne_ein_und_lehnt_Fahrenheit_ab()
    {
        var port = Ports[1];
        HomeAssistantState Z(string wert, string? einheit = "°C", double? min = null, double? max = null)
            => new() { EntityId = "x", State = wert, UnitOfMeasurement = einheit, AttributMin = min, AttributMax = max };
        HomeAssistantState? Modus(string id) => new() { EntityId = id, State = "Auto" };

        var w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("24") : Modus(id), 20.5);
        Assert.Equal(21, w.Plan);            // 20,5 → 21 (von Null weg)
        Assert.True(w.Setzen);

        w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("24", max: 20) : Modus(id), 21);
        Assert.Equal(20, w.Plan);            // die Spanne der Entität gilt

        w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("75", "°F") : Modus(id), 21);
        Assert.Equal("unbekannt", w.Urteil); // Fahrenheit: lieber nichts schreiben als falsch

        w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("21") : Modus(id), 21);
        Assert.Equal("passt", w.Urteil);
        Assert.False(w.Setzen);

        w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("nan") : Modus(id), 21);
        Assert.Equal("unbekannt", w.Urteil);

        w = TrocknungZiel.Entscheide("kuehlung", port, id => id == "number.clima_temp" ? Z("24") : Modus(id), null);
        Assert.Equal("keinPlan", w.Urteil);
        Assert.False(w.Setzen);
    }

    [Fact]
    public async Task Der_Worker_geht_alle_Zelte_auf_Regeln_durch()
    {
        ErnteImTrockenzelt();
        var dienst = Dienst();
        var provider = new ServiceCollection()
            .AddSingleton(_steuerung).AddSingleton(dienst).BuildServiceProvider();
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("melden", false, true));
        var worker = new TrocknungSteuerungWorker(provider, NullLogger<TrocknungSteuerungWorker>.Instance);
        Assert.Equal(0, await worker.EinmalAsync(default)); // Nur melden: kein Zelt im Betrieb

        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", false, true));
        Assert.Equal(1, await worker.EinmalAsync(default));
        Assert.Equal("21", _ha["number.clima_temp"].State);
    }

    private void AbluftEinrichten(string modus = "Auto")
    {
        _ports.Set(_trockenZelt.Id, "abluft", "dev-abluft");
        ZustandSetzen("select.abluft_modus", modus);
        var bereit = modus == "Auto";
        ZustandSetzen("number.abluft_feuchte", bereit ? "55" : "unavailable", bereit ? "%" : null);
        ZustandSetzen("switch.abluft_feuchte_an", "on");
        ZustandSetzen("number.abluft_temp", bereit ? "24" : "unavailable", bereit ? "°C" : null);
        ZustandSetzen("switch.abluft_temp_an", "on");
    }

    [Fact]
    public async Task Die_Abluft_folgt_mit_einem_Schalter_ihren_beiden_Grenzen_und_der_Modus_bleibt()
    {
        AbluftEinrichten();
        ErnteImTrockenzelt();
        var c = Controller();

        var stand = Wert(await c.Setzen(_trockenZelt.Id,
            new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftFolgt = true }, default));

        Assert.Contains(("number.abluft_feuchte", 62.0), _gesetzt);   // Band oben: 62 % wie beim Entfeuchter
        Assert.Contains(("number.abluft_temp", 21.0), _gesetzt);      // Band oben: 21 °C wie bei der Kühlung
        Assert.Equal(2, _gesetzt.Count);                              // Entfeuchter und Kühlung folgen nicht
        Assert.Equal("Auto", _ha["select.abluft_modus"].State);       // Modus unverändert
        Assert.Equal("on", _ha["switch.abluft_feuchte_an"].State);    // Schalter unverändert
        var feuchte = stand.Geraete.Single(g => g.Rolle == "abluft-feuchte");
        var temp = stand.Geraete.Single(g => g.Rolle == "abluft-temp");
        Assert.Equal((55, 24), (feuchte.Vorher, temp.Vorher));        // beide Werte davor gemerkt
        Assert.True(feuchte.Folgt && temp.Folgt);
        Assert.Equal("Testport Abluft", feuchte.PortName);
    }

    [Fact]
    public async Task Steht_die_Abluft_auf_On_gibt_es_keine_Grenzen_und_es_wird_nichts_geschrieben()
    {
        // Echter Controller: Modus On → die Auslöser-Entitäten sind „unavailable“.
        AbluftEinrichten("On");
        ErnteImTrockenzelt();
        var c = Controller();

        var stand = Wert(await c.Setzen(_trockenZelt.Id,
            new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftFolgt = true }, default));

        Assert.Empty(_gesetzt);
        Assert.All(stand.Geraete.Where(g => g.Rolle.StartsWith("abluft")), g => Assert.Equal("ohneAutomatik", g.Urteil));
        Assert.Equal("On", _ha["select.abluft_modus"].State);
    }

    [Fact]
    public async Task Ein_Aufruf_ohne_abluftFolgt_laesst_den_Schalter_der_Abluft_und_ohne_Port_wird_abgewiesen()
    {
        var c = Controller();
        Assert.IsType<BadRequestObjectResult>((await c.Setzen(_trockenZelt.Id,
            new TrocknungSteuerungRequest { Betrieb = "melden", AbluftFolgt = true }, default)).Result);

        AbluftEinrichten();
        Wert(await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "melden", AbluftFolgt = true }, default));
        // Älterer Client: kennt „abluftFolgt“ nicht.
        var stand = Wert(await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "melden", EntfeuchterFolgt = true }, default));

        Assert.True(stand.Geraete.Single(g => g.Rolle == "abluft-feuchte").Folgt);
        Assert.True(_steuerung.Get(_trockenZelt.Id).AbluftFolgt);
    }

    [Fact]
    public async Task Alte_Werte_zurueck_setzt_auch_die_Grenzen_der_Abluft_zurueck()
    {
        AbluftEinrichten();
        ErnteImTrockenzelt();
        var c = Controller();
        await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftFolgt = true }, default);

        var zurueck = Wert(await c.Zuruecksetzen(_trockenZelt.Id, default));

        Assert.Equal(2, zurueck.Zurueck);
        Assert.Equal("55", _ha["number.abluft_feuchte"].State);
        Assert.Equal("24", _ha["number.abluft_temp"].State);
        Assert.False(_steuerung.Get(_trockenZelt.Id).AbluftFolgt);
        Assert.Empty(_steuerung.Merkers(_trockenZelt.Id));
    }

    [Fact]
    public async Task Waehrend_des_Speicherns_meldet_der_Stand_den_Vorgang_mit_Phase_und_die_Antwort_meldet_ihn_nicht()
    {
        ErnteImTrockenzelt();
        var dienst = Dienst();
        var c = new TrocknungSteuerungApiController(_grows, _ports, _steuerung, dienst);
        TrocknungSteuerungService.Arbeit? mittendrin = null;
        // Ein anderer Benutzer liest den Stand, während der Fork gerade an den Controller schreibt.
        _beimSetzen = () => mittendrin ??= dienst.StandAsync(_trockenZelt.Id, default).GetAwaiter().GetResult().InArbeit;

        var anfrage = Anfrage("regeln", true, false);
        anfrage.ClientId = "seite-a";
        var stand = Wert(await c.Setzen(_trockenZelt.Id, anfrage, default));

        Assert.NotNull(mittendrin);
        Assert.Equal(TrocknungSteuerungService.PhaseAbgleichen, mittendrin!.Phase);
        Assert.Equal("seite-a", mittendrin.ClientId);
        Assert.Null(stand.InArbeit);                                                        // die Antwort sagt „fertig“
        Assert.Null((await dienst.StandAsync(_trockenZelt.Id, default)).InArbeit);          // und nichts bleibt hängen
    }

    [Fact]
    public async Task Scheitert_der_Vorgang_mit_einem_Fehler_bleibt_keine_Arbeitsmeldung_stehen()
    {
        ErnteImTrockenzelt();
        var dienst = Dienst();
        var c = new TrocknungSteuerungApiController(_grows, _ports, _steuerung, dienst);
        _beimSetzen = () => throw new InvalidOperationException("Controller antwortet nicht");

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Setzen(_trockenZelt.Id, Anfrage("regeln", true, false), default));

        Assert.Null((await dienst.StandAsync(_trockenZelt.Id, default)).InArbeit);
    }

    [Fact]
    public async Task Ein_zweiter_Vorgang_desselben_Zelts_behaelt_seine_Meldung_und_seine_Phase_wenn_der_erste_endet_oder_weiterschaltet()
    {
        var dienst = Dienst();
        var erster = dienst.ArbeitBeginnen(_trockenZelt.Id, "a");
        await Task.Delay(5);
        var zweiter = dienst.ArbeitBeginnen(_trockenZelt.Id, "b");

        erster.Phase(TrocknungSteuerungService.PhasePruefen);   // gehört dem ersten, der Eintrag ist aber der des zweiten
        erster.Dispose();

        var arbeit = (await dienst.StandAsync(_trockenZelt.Id, default)).InArbeit;
        Assert.Equal("b", arbeit?.ClientId);
        Assert.Equal(TrocknungSteuerungService.PhaseSpeichern, arbeit?.Phase);
        zweiter.Phase(TrocknungSteuerungService.PhaseAbgleichen);
        Assert.Equal(TrocknungSteuerungService.PhaseAbgleichen, (await dienst.StandAsync(_trockenZelt.Id, default)).InArbeit?.Phase);
        zweiter.Dispose();
        Assert.Null((await dienst.StandAsync(_trockenZelt.Id, default)).InArbeit);
    }

    [Theory]
    [InlineData("s-abc123", "s-abc123")]
    [InlineData("Seite_7", "Seite_7")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("mit Leerzeichen", null)]
    [InlineData("<script>", null)]
    [InlineData("ä-ö", null)]
    public void Die_Kennung_der_Seite_ist_kurz_und_aus_einfachen_Zeichen_sonst_wird_sie_verworfen(string? eingabe, string? erwartet)
        => Assert.Equal(erwartet, TrocknungSteuerungService.ClientIdPruefen(eingabe));

    [Fact]
    public void Eine_zu_lange_Kennung_wird_verworfen()
        => Assert.Null(TrocknungSteuerungService.ClientIdPruefen(new string('a', 41)));

    [Fact]
    public void Eine_Datenbank_aus_der_Zeit_vor_der_Abluft_bekommt_die_Spalte_nachgeruestet()
    {
        var wurzel = Path.Combine(Path.GetTempPath(), "TrocknungAlt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wurzel);
        try
        {
            var paths = new AppPaths(wurzel);
            using (var verbindung = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.DatabasePath}"))
            {
                verbindung.Open();
                using var befehl = verbindung.CreateCommand();
                befehl.CommandText = """
                    CREATE TABLE ForkTrocknungSteuerung (TentId INTEGER NOT NULL PRIMARY KEY, Betrieb TEXT NOT NULL,
                        EntfeuchterFolgt INTEGER NOT NULL, KuehlungFolgt INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL);
                    INSERT INTO ForkTrocknungSteuerung VALUES (7, 'regeln', 1, 0, '2026-11-01T00:00:00Z');
                    """;
                befehl.ExecuteNonQuery();
            }

            var repo = new TrocknungSteuerungRepository(paths);
            var alt = repo.Get(7);
            Assert.Equal(new TrocknungSteuerungRepository.Einstellung("regeln", true, false, false), alt);
            repo.Set(7, alt with { AbluftFolgt = true });
            Assert.True(repo.Get(7).AbluftFolgt);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(wurzel, recursive: true); } catch { }
        }
    }
}
