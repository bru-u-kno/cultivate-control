using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Fertig-Erkennung der Trocknung (A-029, Etappe 4) über den Controller: Wägung eintragen, nachlesen, löschen — und was
/// abgewiesen wird.
/// </summary>
public sealed class TrocknungFertigApiTests : IDisposable
{
    private readonly string _wurzel;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungWaegungRepository _waegungen;
    private readonly TrocknungApiController _controller;
    private readonly Tent _trockenZelt;
    private readonly int _growId;

    public TrocknungFertigApiTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrocknungFertig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        var paths = new AppPaths(_wurzel);
        var bluete = TestDatabase.InitializeWithDefaultTent(paths);
        _grows = new GrowRepository(paths);
        _ernten = new HarvestRepository(paths);
        _waegungen = new TrocknungWaegungRepository(paths);
        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
        _growId = _grows.CreateGrow(new GrowRun { Name = "Charge A", TentId = bluete.Id, StartDate = DateTime.Today.AddDays(-90), Status = GrowStatus.Running });
        // Die Wissensbasis braucht dieser Weg nicht (nur „Stand“ liest Programme) — nicht geladen.
        _controller = new TrocknungApiController(_grows, _ernten, new KnowledgeBaseLoader(paths, NullLogger<KnowledgeBaseLoader>.Instance), _waegungen);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private void ErnteImTrockenzelt(double? frischG = null)
        => _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-1), WetWeightG = frischG, DryingTentId = _trockenZelt.Id });

    private static T Wert<T>(ActionResult<T> ergebnis) where T : class
        => (ergebnis.Result as ObjectResult)?.Value as T ?? ergebnis.Value ?? throw new InvalidOperationException("Keine Antwort: " + ergebnis.Result);

    [Fact]
    public void Ohne_Trocknung_meldet_die_Auskunft_leer_und_eine_Waegung_wird_abgewiesen()
    {
        Assert.False(Wert(_controller.Fertig(_trockenZelt.Id)).Trocknet);
        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 80 }).Result);
        Assert.IsType<NotFoundObjectResult>(_controller.Fertig(99999).Result);
    }

    [Fact]
    public void Die_erste_Waegung_ist_der_Startwert_und_die_Kurve_waechst_mit_jeder_weiteren()
    {
        ErnteImTrockenzelt();
        var erste = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 75.6 }));
        Assert.True(erste.Trocknet);
        Assert.Equal("erste", erste.Stufe);
        Assert.Single(erste.Punkte);
        Assert.Equal(100, erste.Punkte[0].Prozent, 6);
        Assert.False(erste.WaegungFaellig); // gerade gewogen: die nächste ist in zwei Tagen fällig

        var zweite = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 61.2 }));
        Assert.Equal(2, zweite.Punkte.Count);
        Assert.Equal(75.6, zweite.ReferenzG);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(200_001)]
    [InlineData(double.NaN)]
    public void Ein_unplausibles_Gewicht_wird_abgewiesen_und_nichts_gespeichert(double gramm)
    {
        ErnteImTrockenzelt();
        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = gramm }).Result);
        Assert.Empty(_waegungen.GetForGrow(_growId));
    }

    [Fact]
    public void Die_ganze_Charge_braucht_ein_Frischgewicht_in_der_Ernte()
    {
        ErnteImTrockenzelt(frischG: null);
        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 500, GanzeCharge = true }).Result);
    }

    [Fact]
    public void Mit_Frischgewicht_und_ganzer_Charge_gibt_es_eine_Zielmarke_und_der_erste_Eintrag_legt_die_Art_fest()
    {
        ErnteImTrockenzelt(frischG: 1000);
        var erste = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 640, GanzeCharge = true }));
        Assert.True(erste.BezugFrischgewicht);
        Assert.Equal(22, erste.ZielProzent);

        // Wer danach „Probe“ schickt, wiegt trotzdem dasselbe wie am Anfang — sonst vergliche die Kurve Verschiedenes.
        Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 500, GanzeCharge = false }));
        Assert.All(_waegungen.GetForGrow(_growId), w => Assert.True(w.GanzeCharge));
    }

    [Fact]
    public void Eine_Waegung_laesst_sich_loeschen_aber_nur_bei_der_eigenen_Charge()
    {
        ErnteImTrockenzelt();
        var stand = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 80 }));
        var id = stand.Punkte[0].Id;

        Assert.IsType<NotFoundObjectResult>(_controller.WaegungLoeschen(_trockenZelt.Id, id + 999).Result);
        Assert.Empty(Wert(_controller.WaegungLoeschen(_trockenZelt.Id, id)).Punkte);
        Assert.Empty(_waegungen.GetForGrow(_growId));
    }

    [Fact]
    public void Ein_Doppelklick_legt_keine_zweite_Waegung_an()
    {
        ErnteImTrockenzelt();
        Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 75.6 }));
        var zweite = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 75.6 }));
        Assert.Single(zweite.Punkte);
        Assert.Single(_waegungen.GetForGrow(_growId));

        // Ein anderes Gewicht ist dagegen eine neue Wägung.
        Assert.Equal(2, Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 70.1 })).Punkte.Count);
    }

    [Fact]
    public void Faellt_das_Frischgewicht_spaeter_weg_laesst_sich_weiter_wiegen()
    {
        var ernteId = _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-1), WetWeightG = 1000, DryingTentId = _trockenZelt.Id });
        Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 640, GanzeCharge = true }));

        _ernten.Update(new HarvestEntry { Id = ernteId, GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-1), WetWeightG = null, DryingTentId = _trockenZelt.Id });
        var danach = _controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 600 });
        Assert.IsNotType<BadRequestObjectResult>(danach.Result);
        Assert.Equal(2, _waegungen.GetForGrow(_growId).Count);
    }

    [Fact]
    public void Eine_Waegung_laesst_sich_fuer_einen_frueheren_Tag_eintragen_aber_nicht_fuer_Zukunft_oder_vor_der_Ernte()
    {
        _ernten.Create(new HarvestEntry { GrowId = _growId, HarvestedAt = DateTime.Today.AddDays(-4), DryingTentId = _trockenZelt.Id });
        var vorgestern = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd");

        var stand = Wert(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 80, Datum = vorgestern }));
        Assert.Equal(vorgestern, stand.Punkte[0].Datum);
        Assert.Equal(3, stand.Punkte[0].Tag); // Erntetag −4 → Tag 1; vorgestern → Tag 3

        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 79, Datum = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd") }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 79, Datum = DateTime.Today.AddDays(-9).ToString("yyyy-MM-dd") }).Result);
        Assert.IsType<BadRequestObjectResult>(_controller.WaegungEintragen(_trockenZelt.Id, new WaegungRequest { GewichtG = 79, Datum = "gestern" }).Result);
        Assert.Single(_waegungen.GetForGrow(_growId));
    }
}
