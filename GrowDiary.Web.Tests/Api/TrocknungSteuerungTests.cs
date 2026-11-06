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
    private readonly List<(string Entity, string Option)> _modusGesetzt = [];
    private bool _schaltenGelingt = true;
    private Action? _beimSetzen;

    private static readonly IReadOnlyList<TrocknungZiel.Port> Ports =
    [
        new("dev-dehumi", "Testport Entfeuchter", "C", 5, "select.dehumi_modus", "number.dehumi_feuchte", "switch.dehumi_feuchte_an", null, null),
        new("dev-clima", "Testport Kühlung", "C", 4, "select.clima_modus", null, null, "number.clima_temp", "switch.clima_temp_an"),
        new("dev-abluft", "Testport Abluft", "C", 1, "select.abluft_modus", "number.abluft_feuchte", "switch.abluft_feuchte_an", "number.abluft_temp", "switch.abluft_temp_an", "number.abluft_stufe"),
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
            },
            (id, option, _) =>
            {
                _modusGesetzt.Add((id, option));
                if (_schaltenGelingt) ZustandSetzen(id, option);
                return Task.FromResult(_schaltenGelingt);
            });
        return new TrocknungSteuerungService(_grows, _ernten, new TrocknungPlanQuelle(new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), new TrocknungPlanRepository(_paths)), _ports, _steuerung, ziel, NullLogger<TrocknungSteuerungService>.Instance);
    }

    private TrocknungSteuerungApiController Controller() => new(_grows, _ports, _steuerung, Dienst(), ZuluftDienst(), Geraete());

    private sealed class LeererHaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private SteuerungGeraeteService Geraete() => new(new SteuerungRepository(_paths));

    /// <summary>Der Dienst der Zuluft im Trocknungsbetrieb ohne erreichbares Home Assistant (nicht eingerichtet): er liest nichts und schaltet nichts.</summary>
    private TrocknungZuluftService ZuluftDienst()
    {
        var ha = new HomeAssistantService(new GrowDiary.Web.Tests.TestFakes.StubHttpClientFactory(new LeererHaHandler()), NullLogger<HomeAssistantService>.Instance);
        var einstellungen = new HomeAssistantSettingsRepository(_paths);
        var steuerung = new SteuerungRepository(_paths);
        var geraete = new SteuerungGeraeteService(steuerung);
        var zuluft = new ZuluftSteuerungService(steuerung, ha, einstellungen, geraete, new HelferBereichService(NullLogger<HelferBereichService>.Instance), NullLogger<ZuluftSteuerungService>.Instance);
        var bereiche = new ServiceCollection().AddSingleton(zuluft).AddSingleton(geraete).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new(_grows, _ernten, new TrocknungPlanQuelle(new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), new TrocknungPlanRepository(_paths)),
            _steuerung, ha, einstellungen, bereiche, new ZuluftFuehrung(), NullLogger<TrocknungZuluftService>.Instance);
    }

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
        Assert.Equal(1, nachErstem); // nur die Kühlung: der Entfeuchter wird geschaltet, nicht über eine Grenze geführt

        Assert.Equal(0, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);
        Assert.Equal(nachErstem, _gesetzt.Count);

        // Nutzer verstellt von Hand: der nächste Lauf zieht nach, der Wert davor bleibt der allererste (24).
        ZustandSetzen("number.clima_temp", "23", "°C");
        Assert.Equal(1, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);
        Assert.Equal(24, _steuerung.Merkers(_trockenZelt.Id)["kuehlung"].Vorher);
        Assert.False(_steuerung.Merkers(_trockenZelt.Id).ContainsKey("entfeuchter")); // keine Grenze am Entfeuchter, kein Merker
    }

    [Fact]
    public async Task Ein_Etappenwechsel_wird_beim_naechsten_Durchlauf_nachgezogen()
    {
        ErnteImTrockenzelt(); // Tag 2: Etappe 1 „Anfang“, oben 21 °C und 62 %
        await Controller().Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default);
        Assert.Equal("21", _ha["number.clima_temp"].State);
        Assert.Equal("50", _ha["number.dehumi_feuchte"].State); // der Entfeuchter bekommt keinen Grenzwert mehr

        // Tag 5 = Etappe 2 „Hauptphase“ (19 °C, 58 %): oben 20 °C und 60 %.
        var ernte = _ernten.GetForGrow(_growId)!;
        ernte.HarvestedAt = DateTime.Today.AddDays(-4);
        _ernten.Update(ernte);
        Assert.Equal(1, (await Dienst().AbgleichenAsync(_trockenZelt.Id, default)).Geschrieben);

        Assert.Equal("20", _ha["number.clima_temp"].State);
        Assert.Equal("50", _ha["number.dehumi_feuchte"].State);
        Assert.Equal(24, _steuerung.Merkers(_trockenZelt.Id)["kuehlung"].Vorher); // Wert des Nutzers, nicht der der Etappe davor
    }

    [Fact]
    public async Task Zuruecksetzen_stellt_die_alten_Werte_wieder_ein_und_geht_auf_Nur_melden()
    {
        ErnteImTrockenzelt();
        var c = Controller();
        await c.Setzen(_trockenZelt.Id, Anfrage("regeln", true, true), default);
        Assert.Equal("21", _ha["number.clima_temp"].State);

        var zurueck = Wert(await c.Zuruecksetzen(_trockenZelt.Id, default));

        Assert.Equal(1, zurueck.Zurueck);
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
            .AddSingleton(_steuerung).AddSingleton(dienst).AddSingleton(ZuluftDienst()).BuildServiceProvider();
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("melden", false, true));
        var worker = new TrocknungSteuerungWorker(provider, NullLogger<TrocknungSteuerungWorker>.Instance);
        Assert.Equal(0, await worker.EinmalAsync(default)); // Nur melden: kein Zelt im Betrieb

        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", false, true));
        Assert.Equal(1, await worker.EinmalAsync(default));
        Assert.Equal("21", _ha["number.clima_temp"].State);
    }

    // ---------------------------------------------------------------- Entfeuchter schalten (06.11.2026)

    private void FeuchteSensor(string wert)
    {
        if (_grows.GetTent(_trockenZelt.Id)!.Sensors.All(x => x.MetricType != SensorMetricType.Humidity))
            _grows.AddTentSensor(new TentSensor { TentId = _trockenZelt.Id, MetricType = SensorMetricType.Humidity, HaEntityId = "sensor.zelt_feuchte", IsActive = true });
        ZustandSetzen("sensor.zelt_feuchte", wert, "%");
    }

    private async Task<bool> Schalten(string betrieb = "regeln")
    {
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung(betrieb, true, false));
        return await Dienst().SchaltenAsync(_trockenZelt.Id, default);
    }

    [Fact]
    public async Task Der_Fork_schaltet_den_Entfeuchter_ein_ab_dem_Ziel_und_aus_unter_dem_Band_und_schreibt_nie_eine_Grenze()
    {
        ErnteImTrockenzelt(); // Tag 2: Etappe 1, Ziel 60 %, Band 58–62
        ZustandSetzen("select.dehumi_modus", "Off");
        FeuchteSensor("61");

        Assert.True(await Schalten());
        Assert.Equal([("select.dehumi_modus", "On")], _modusGesetzt);
        Assert.Empty(_gesetzt); // keine Zahl, kein Grenzwert

        // Innerhalb der Hysterese (58–60) bleibt der Entfeuchter an.
        FeuchteSensor("59");
        Assert.False(await Dienst().SchaltenAsync(_trockenZelt.Id, default));
        Assert.Single(_modusGesetzt);
    }

    [Fact]
    public async Task Unter_dem_Band_schaltet_der_Fork_aus_aber_erst_nach_der_Mindestpause()
    {
        ErnteImTrockenzelt();
        ZustandSetzen("select.dehumi_modus", "Off");
        FeuchteSensor("61");
        var dienst = Dienst();
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", true, false));
        Assert.True(await dienst.SchaltenAsync(_trockenZelt.Id, default)); // ein

        FeuchteSensor("55");                                                // sofort darunter: Pause läuft noch
        Assert.False(await dienst.SchaltenAsync(_trockenZelt.Id, default));
        Assert.Equal("wartet", (await dienst.StandAsync(_trockenZelt.Id, default)).Geraete.Single(g => g.Rolle == "entfeuchter").Schalten!.Zustand);
        Assert.Single(_modusGesetzt);
    }

    [Fact]
    public async Task Steht_der_Port_auf_Auto_schaltet_der_Fork_nicht_und_der_Stand_sagt_es()
    {
        ErnteImTrockenzelt();
        FeuchteSensor("65"); // weit über dem Ziel, Port steht auf Auto (so ist das Zelt im Fixture)

        Assert.False(await Schalten());
        Assert.Empty(_modusGesetzt);
        Assert.Equal("Auto", _ha["select.dehumi_modus"].State); // Auto wird nie überschrieben
        var g = Wert(await Controller().Lesen(_trockenZelt.Id, default)).Geraete.Single(x => x.Rolle == "entfeuchter");
        Assert.Equal("portNichtSchaltbar", g.Urteil);
        Assert.Equal("portNichtSchaltbar", g.Schalten!.Zustand);
    }

    [Fact]
    public async Task Ohne_Messwert_bleibt_der_Entfeuchter_wie_er_ist()
    {
        ErnteImTrockenzelt();
        ZustandSetzen("select.dehumi_modus", "On");
        FeuchteSensor("unavailable");

        Assert.False(await Schalten());
        Assert.Empty(_modusGesetzt);
        var g = Wert(await Controller().Lesen(_trockenZelt.Id, default)).Geraete.Single(x => x.Rolle == "entfeuchter");
        Assert.Equal("keinMesswert", g.Urteil);
    }

    [Fact]
    public async Task Lehnt_das_Geraet_den_Schaltbefehl_ab_meldet_der_Stand_schaltetNicht_und_der_naechste_Lauf_versucht_es_wieder()
    {
        ErnteImTrockenzelt();
        ZustandSetzen("select.dehumi_modus", "Off");
        FeuchteSensor("62");
        _schaltenGelingt = false;
        var dienst = Dienst(); // der Dienst ist im Betrieb ein Einzelstück und merkt sich den Fehlschlag
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", true, false));

        Assert.False(await dienst.SchaltenAsync(_trockenZelt.Id, default));
        Assert.Equal("schaltetNicht", (await dienst.StandAsync(_trockenZelt.Id, default)).Geraete.Single(x => x.Rolle == "entfeuchter").Urteil);

        _schaltenGelingt = true;
        Assert.True(await dienst.SchaltenAsync(_trockenZelt.Id, default));
        Assert.Equal("On", _ha["select.dehumi_modus"].State);
    }

    [Fact]
    public async Task Nur_melden_oder_nicht_eingebunden_schaltet_nichts()
    {
        ErnteImTrockenzelt();
        ZustandSetzen("select.dehumi_modus", "Off");
        FeuchteSensor("70");

        Assert.False(await Schalten("melden"));
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", false, false));
        Assert.False(await Dienst().SchaltenAsync(_trockenZelt.Id, default));
        Assert.Empty(_modusGesetzt);
    }

    [Fact]
    public async Task Eine_Stoerung_von_zehn_Laeufen_geht_einmal_als_Push_raus()
    {
        ErnteImTrockenzelt();
        FeuchteSensor("65"); // Port auf Auto: Störung
        var dienst = Dienst();
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", true, false));
        for (var i = 0; i < TrocknungSteuerungService.ProblemBisPush + 3; i++) await dienst.SchaltenAsync(_trockenZelt.Id, default);
        // Ohne NotificationService im Test kein Push; die Zählung selbst bleibt stumm und wirft nicht.
        Assert.Empty(_modusGesetzt);
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

    // ---------------------------------------------------------------- Mindeststufe der Abluft (07.11.2026)

    private void AbluftStufe(string modus, string stufe)
    {
        AbluftEinrichten(modus);
        ZustandSetzen("number.abluft_stufe", stufe);
    }

    private async Task<TrocknungSteuerungService> AbluftLaeuft(int minStufe, bool folgt = true, string betrieb = "regeln")
    {
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung(betrieb, false, false, folgt, minStufe));
        await Task.CompletedTask;
        return Dienst();
    }

    [Fact]
    public async Task Liegt_die_Abluft_unter_der_Mindeststufe_hebt_der_Fork_sie_an_und_nie_darueber()
    {
        ErnteImTrockenzelt();
        AbluftStufe("On", "1");
        var dienst = await AbluftLaeuft(2);

        Assert.True(await dienst.AbluftStufeAsync(_trockenZelt.Id, default));
        Assert.Equal([("number.abluft_stufe", 2.0)], _gesetzt);
        Assert.Equal("On", _ha["select.abluft_modus"].State); // der Modus bleibt

        // Läuft sie schon höher, bleibt sie dort: der Fork nimmt eine Stufe nie zurück.
        ZustandSetzen("number.abluft_stufe", "5");
        Assert.False(await dienst.AbluftStufeAsync(_trockenZelt.Id, default));
        Assert.Single(_gesetzt);

        // Genau auf der Mindeststufe: nichts zu tun.
        ZustandSetzen("number.abluft_stufe", "2");
        Assert.False(await dienst.AbluftStufeAsync(_trockenZelt.Id, default));
        Assert.Single(_gesetzt);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("Auto")]
    public async Task Steht_der_Port_nicht_auf_On_schreibt_der_Fork_keine_Stufe(string modus)
    {
        ErnteImTrockenzelt();
        AbluftStufe(modus, "0");
        var dienst = await AbluftLaeuft(2);

        Assert.False(await dienst.AbluftStufeAsync(_trockenZelt.Id, default));
        Assert.Empty(_gesetzt);
        Assert.Equal(modus, _ha["select.abluft_modus"].State);
    }

    [Fact]
    public async Task Ohne_Mindeststufe_nicht_eingebunden_oder_nur_melden_schreibt_der_Fork_nichts()
    {
        ErnteImTrockenzelt();
        AbluftStufe("On", "0");

        Assert.False(await (await AbluftLaeuft(0)).AbluftStufeAsync(_trockenZelt.Id, default));            // keine Mindeststufe
        Assert.False(await (await AbluftLaeuft(2, folgt: false)).AbluftStufeAsync(_trockenZelt.Id, default)); // nicht eingebunden
        Assert.False(await (await AbluftLaeuft(2, betrieb: "melden")).AbluftStufeAsync(_trockenZelt.Id, default));
        Assert.Empty(_gesetzt);
    }

    [Fact]
    public async Task Lehnt_das_Geraet_die_Stufe_ab_meldet_der_Stand_stufeNichtAnhebbar()
    {
        ErnteImTrockenzelt();
        AbluftStufe("On", "1");
        _setzenGelingt = false;
        var dienst = await AbluftLaeuft(2);

        Assert.False(await dienst.AbluftStufeAsync(_trockenZelt.Id, default));
        var abluft = (await dienst.StandAsync(_trockenZelt.Id, default)).Geraete.First(g => g.Rolle == "abluft-feuchte");
        Assert.Equal("stufeNichtAnhebbar", abluft.Urteil);
        Assert.Equal(2, abluft.Stufe!.Mindest);
    }

    [Fact]
    public async Task Der_Stand_nennt_Mindeststufe_Stufe_jetzt_und_das_Urteil()
    {
        ErnteImTrockenzelt();
        AbluftStufe("On", "3");
        var dienst = await AbluftLaeuft(2);

        var stand = await dienst.StandAsync(_trockenZelt.Id, default);
        var abluft = stand.Geraete.First(g => g.Rolle == "abluft-feuchte");
        Assert.Equal(2, stand.AbluftMinStufe);
        Assert.Equal("haeltStufe", abluft.Urteil);
        Assert.Equal((2, 3.0, "haelt"), (abluft.Stufe!.Mindest, abluft.Stufe.Jetzt, abluft.Stufe.Zustand));

        ZustandSetzen("select.abluft_modus", "Off");
        Assert.Equal("stufePortNichtOn", (await dienst.StandAsync(_trockenZelt.Id, default)).Geraete.First(g => g.Rolle == "abluft-feuchte").Urteil);
    }

    [Fact]
    public async Task Die_Mindeststufe_wird_ueber_die_Schnittstelle_gesetzt_geprueft_und_bleibt_wenn_die_Angabe_fehlt()
    {
        ErnteImTrockenzelt();
        AbluftStufe("On", "3");
        var c = Controller();

        var stand = Wert(await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftFolgt = true, AbluftMinStufe = 2 }, default));
        Assert.Equal(2, stand.AbluftMinStufe);
        Assert.Equal(2, _steuerung.Get(_trockenZelt.Id).AbluftMinStufe);

        // Eine Anfrage ohne die Angabe lässt sie stehen; 11 und −1 werden abgewiesen.
        stand = Wert(await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln" }, default));
        Assert.Equal(2, stand.AbluftMinStufe);
        Assert.IsType<BadRequestObjectResult>((await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftMinStufe = 11 }, default)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftMinStufe = -1 }, default)).Result);

        // 0 heißt „keine“.
        stand = Wert(await c.Setzen(_trockenZelt.Id, new TrocknungSteuerungRequest { Betrieb = "regeln", AbluftMinStufe = 0 }, default));
        Assert.Equal(0, stand.AbluftMinStufe);
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
        var c = new TrocknungSteuerungApiController(_grows, _ports, _steuerung, dienst, ZuluftDienst(), Geraete());
        TrocknungSteuerungService.Arbeit? mittendrin = null;
        // Ein anderer Benutzer liest den Stand, während der Fork gerade an den Controller schreibt.
        _beimSetzen = () => mittendrin ??= dienst.StandAsync(_trockenZelt.Id, default).GetAwaiter().GetResult().InArbeit;

        var anfrage = Anfrage("regeln", false, true);
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
        var c = new TrocknungSteuerungApiController(_grows, _ports, _steuerung, dienst, ZuluftDienst(), Geraete());
        _beimSetzen = () => throw new InvalidOperationException("Controller antwortet nicht");

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.Setzen(_trockenZelt.Id, Anfrage("regeln", false, true), default));

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

    // ------------------------------------------------------------------ Zuluft im Trocknungsbetrieb (cc.337)

    [Fact]
    public async Task Zuluft_ohne_Trockenzelt_meldet_kein_Trockenzelt()
    {
        var leer = new GrowRepository(_paths);
        foreach (var t in leer.GetTents().Where(t => t.TentType == TentType.Drying)) leer.DeleteTent(t.Id);

        var dto = Wert(await Controller().ZuluftLesen(default));
        Assert.False(dto.Trockenzelt);
        Assert.False(dto.Folgt);

        var antwort = await Controller().ZuluftSetzen(new TrocknungZuluftRequest { Folgt = true }, default);
        Assert.IsType<BadRequestObjectResult>(antwort.Result);
    }

    [Fact]
    public async Task Zuluft_einschalten_ohne_zugeordneten_Schalter_wird_abgelehnt_und_nichts_gespeichert()
    {
        var antwort = await Controller().ZuluftSetzen(new TrocknungZuluftRequest { Folgt = true }, default);
        Assert.IsType<BadRequestObjectResult>(antwort.Result);
        Assert.False(_steuerung.Get(_trockenZelt.Id).ZuluftFolgt);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(11, null)]
    [InlineData(null, -1)]
    [InlineData(null, 21)]
    public async Task Zuluft_Hoechststufe_und_Abstand_haben_Grenzen(int? stufe, int? abstand)
    {
        var antwort = await Controller().ZuluftSetzen(new TrocknungZuluftRequest { Hoechststufe = stufe, AbstandProzent = abstand }, default);
        Assert.IsType<BadRequestObjectResult>(antwort.Result);
    }

    [Fact]
    public async Task Zuluft_Hoechststufe_und_Abstand_werden_gespeichert_und_lassen_den_Rest_stehen()
    {
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("regeln", true, false, true, 2));

        var dto = Wert(await Controller().ZuluftSetzen(new TrocknungZuluftRequest { Hoechststufe = 6, AbstandProzent = 5 }, default));

        Assert.Equal(6, dto.Hoechststufe);
        Assert.Equal(5, dto.AbstandProzent);
        var s = _steuerung.Get(_trockenZelt.Id);
        Assert.Equal(("regeln", true, false, true, 2, false), (s.Betrieb, s.EntfeuchterFolgt, s.KuehlungFolgt, s.AbluftFolgt, s.AbluftMinStufe, s.ZuluftFolgt));
    }

    [Fact]
    public async Task Das_Speichern_der_Trocknungs_Steuerung_laesst_die_Zuluft_Einstellungen_stehen()
    {
        _steuerung.Set(_trockenZelt.Id, new TrocknungSteuerungRepository.Einstellung("melden", false, false, false, 0, true, 7, 9));

        await Controller().Setzen(_trockenZelt.Id, Anfrage("melden", false, false), default);

        var s = _steuerung.Get(_trockenZelt.Id);
        Assert.Equal((true, 7, 9), (s.ZuluftFolgt, s.ZuluftHoechststufe, s.ZuluftAbstandProzent));
    }

    [Fact]
    public void Zuluft_Einstellungen_ueberleben_die_Datenbank_und_Altbestand_bekommt_Vorgaben()
    {
        Assert.Equal((false, TrocknungSteuerungRepository.ZuluftHoechststufeVorgabe, TrocknungSteuerungRepository.ZuluftAbstandVorgabe),
            (TrocknungSteuerungRepository.Einstellung.Standard.ZuluftFolgt, TrocknungSteuerungRepository.Einstellung.Standard.ZuluftHoechststufe, TrocknungSteuerungRepository.Einstellung.Standard.ZuluftAbstandProzent));
        _steuerung.Set(_trockenZelt.Id, TrocknungSteuerungRepository.Einstellung.Standard with { ZuluftFolgt = true, ZuluftHoechststufe = 5, ZuluftAbstandProzent = 2 });
        var neu = new TrocknungSteuerungRepository(_paths).Get(_trockenZelt.Id);
        Assert.Equal((true, 5, 2), (neu.ZuluftFolgt, neu.ZuluftHoechststufe, neu.ZuluftAbstandProzent));
    }
}
