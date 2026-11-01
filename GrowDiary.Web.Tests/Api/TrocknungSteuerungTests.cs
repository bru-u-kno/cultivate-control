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

    private static readonly IReadOnlyList<TrocknungZiel.Port> Ports =
    [
        new("dev-dehumi", "Testport Entfeuchter", "C", 5, "select.dehumi_modus", "number.dehumi_feuchte", "switch.dehumi_feuchte_an", null, null),
        new("dev-clima", "Testport Kühlung", "C", 4, "select.clima_modus", null, null, "number.clima_temp", "switch.clima_temp_an"),
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
                if (_setzenGelingt) ZustandSetzen(id, wert.ToString(System.Globalization.CultureInfo.InvariantCulture), _ha[id].UnitOfMeasurement);
                return Task.FromResult(_setzenGelingt);
            });
        return new TrocknungSteuerungService(_grows, _ernten, new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), _ports, _steuerung, ziel, NullLogger<TrocknungSteuerungService>.Instance);
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
}
