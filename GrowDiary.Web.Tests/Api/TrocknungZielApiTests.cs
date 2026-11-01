using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Trocknungs-Ziel je Zelt (A-029, Etappe 3, Teil A) über den Controller: Zuordnung der Ports, Vergleich Plan gegen Gerät.
/// Der Fork schreibt dabei nichts an Home Assistant — es gibt in diesem Weg keinen schreibenden Aufruf, der sich prüfen ließe;
/// die Zustände kommen nur über die lesende Funktion.
/// </summary>
public sealed class TrocknungZielApiTests : IDisposable
{
    private readonly string _wurzel;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPortRepository _ports;
    private readonly Tent _trockenZelt;
    private readonly int _growId;
    private readonly AppPaths _paths;
    private readonly List<string> _gelesen = [];

    private static readonly IReadOnlyList<TrocknungZiel.Port> Ports =
    [
        new("dev-dehumi", "Testport Entfeuchter", "C", 5, "select.dehumi_modus", "number.dehumi_feuchte", "switch.dehumi_feuchte_an", null, null),
        new("dev-clima", "Testport Kühlung", "C", 4, "select.clima_modus", null, null, "number.clima_temp", "switch.clima_temp_an"),
    ];

    public TrocknungZielApiTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrocknungZiel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        _paths = new AppPaths(_wurzel);
        var bluete = TestDatabase.InitializeWithDefaultTent(_paths);
        _grows = new GrowRepository(_paths);
        _ernten = new HarvestRepository(_paths);
        _ports = new TrocknungPortRepository(_paths);
        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
        _growId = _grows.CreateGrow(new GrowRun { Name = "Charge A", TentId = bluete.Id, StartDate = DateTime.Today.AddDays(-90), Status = GrowStatus.Running });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private TrocknungApiController Controller(IReadOnlyList<TrocknungZiel.Port>? ports, Func<string, HomeAssistantState?>? zustand = null)
        => new(_grows, _ernten, new TrocknungPlanQuelle(new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), new TrocknungPlanRepository(_paths)),
            new TrocknungWaegungRepository(_paths), _ports,
            new TrocknungZielService(
                _ => Task.FromResult(ports),
                (id, _) => { _gelesen.Add(id); return Task.FromResult(zustand?.Invoke(id)); }));

    private static T Wert<T>(ActionResult<T> ergebnis) where T : class
        => (ergebnis.Result as ObjectResult)?.Value as T ?? ergebnis.Value ?? throw new InvalidOperationException("Keine Antwort: " + ergebnis.Result);

    private void ErnteImTrockenzelt()
        => _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-1), DryingTentId = _trockenZelt.Id });

    private static HomeAssistantState S(string id, string state, string? einheit = null) => new() { EntityId = id, State = state, UnitOfMeasurement = einheit };

    [Fact]
    public async Task Ohne_Trocknung_ist_das_Ziel_leer_und_ein_unbekanntes_Zelt_404()
    {
        var c = Controller(Ports);
        Assert.False(Wert(await c.Ziel(_trockenZelt.Id, default)).Trocknet);
        Assert.IsType<NotFoundObjectResult>((await c.Ziel(99999, default)).Result);
        Assert.Empty(_gelesen); // ohne Trocknung wird nichts bei Home Assistant erfragt
    }

    [Fact]
    public async Task Das_Ziel_nennt_den_Plan_und_vergleicht_beide_Geraete_ohne_etwas_zu_schreiben()
    {
        ErnteImTrockenzelt();
        var c = Controller(Ports, id => id switch
        {
            "select.dehumi_modus" => S(id, "Auto"),
            "number.dehumi_feuchte" => S(id, "50", "%"),
            "switch.dehumi_feuchte_an" => S(id, "on"),
            "select.clima_modus" => S(id, "Off"),
            _ => null,
        });
        Wert(await c.PortsSetzen(_trockenZelt.Id, new PortsRequest { Entfeuchter = "dev-dehumi", Kuehlung = "dev-clima" }, default));

        var ziel = Wert(await c.Ziel(_trockenZelt.Id, default));

        Assert.True(ziel.Trocknet);
        Assert.Equal(2, ziel.Tag);                    // Erntetag −1 → Trocknungstag 2
        Assert.Equal("Anfang, langsam", ziel.Etappe); // Standard-Etappe 1
        Assert.Equal(20, ziel.TempZiel);
        Assert.Equal(60, ziel.FeuchteZiel);
        Assert.Equal(21, ziel.TempOben);              // Band oben: 20 + 1
        Assert.Equal(62, ziel.FeuchteOben);           // Band oben: 60 + 2, unter der Obergrenze 65
        var entfeuchter = ziel.Geraete.Single(g => g.Rolle == "entfeuchter");
        Assert.Equal("weichtAb", entfeuchter.Urteil);
        Assert.Equal(50, entfeuchter.WertAmGeraet);
        Assert.Equal(62, entfeuchter.PlanWert);
        Assert.Equal("geraetAus", ziel.Geraete.Single(g => g.Rolle == "kuehlung").Urteil);
        Assert.All(_gelesen, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public async Task Zuordnen_loesen_und_die_Regeln_dafuer()
    {
        var c = Controller(Ports);

        var ok = Wert(await c.PortsSetzen(_trockenZelt.Id, new PortsRequest { Entfeuchter = "dev-dehumi", Kuehlung = "dev-clima" }, default));
        Assert.Equal("dev-dehumi", ok.Entfeuchter);
        Assert.Equal("dev-clima", ok.Kuehlung);
        Assert.Equal(2, ok.Verfuegbar.Count);

        // Ein Port, den Home Assistant nicht als Port mit Automatik kennt, wird abgewiesen.
        Assert.IsType<BadRequestObjectResult>((await c.PortsSetzen(_trockenZelt.Id, new PortsRequest { Entfeuchter = "irgendwas" }, default)).Result);
        // Derselbe Port für beide Rollen ebenso.
        Assert.IsType<BadRequestObjectResult>((await c.PortsSetzen(_trockenZelt.Id, new PortsRequest { Entfeuchter = "dev-dehumi", Kuehlung = "dev-dehumi" }, default)).Result);
        // Die bestehende Zuordnung bleibt nach den Abweisungen unverändert.
        Assert.Equal("dev-dehumi", _ports.Get(_trockenZelt.Id)["entfeuchter"]);

        // Leer löst.
        var geloest = Wert(await c.PortsSetzen(_trockenZelt.Id, new PortsRequest(), default));
        Assert.Null(geloest.Entfeuchter);
        Assert.Null(geloest.Kuehlung);
        Assert.Empty(_ports.Get(_trockenZelt.Id));
    }

    [Fact]
    public async Task Zuordnen_geht_nur_bei_einem_Trockenzelt_und_nur_wenn_Home_Assistant_antwortet()
    {
        var c = Controller(Ports);
        var gewoehnlich = _grows.CreateTent(new Tent { Name = "Gewöhnlich", TentType = TentType.Production });
        Assert.IsType<BadRequestObjectResult>((await c.PortsSetzen(gewoehnlich.Id, new PortsRequest { Entfeuchter = "dev-dehumi" }, default)).Result);

        var nichtErreichbar = Controller(null);
        Assert.IsType<BadRequestObjectResult>((await nichtErreichbar.PortsSetzen(_trockenZelt.Id, new PortsRequest { Entfeuchter = "dev-dehumi" }, default)).Result);
        Assert.Empty(_ports.Get(_trockenZelt.Id));

        var lesen = Wert(await nichtErreichbar.PortsLesen(_trockenZelt.Id, default));
        Assert.False(lesen.Erreichbar);
    }

    [Fact]
    public async Task Eine_zugeordnete_aber_nicht_auffindbare_Zuordnung_ist_unbekannt_nicht_nicht_zugeordnet()
    {
        ErnteImTrockenzelt();
        _ports.Set(_trockenZelt.Id, "entfeuchter", "dev-weg");
        var ziel = Wert(await Controller(null).Ziel(_trockenZelt.Id, default));
        Assert.False(ziel.PortsErreichbar);
        Assert.Equal("unbekannt", ziel.Geraete.Single(g => g.Rolle == "entfeuchter").Urteil);
        Assert.Equal("nichtZugeordnet", ziel.Geraete.Single(g => g.Rolle == "kuehlung").Urteil);
    }
}
