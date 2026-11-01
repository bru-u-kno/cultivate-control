using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge.Schema;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>
/// Der eigene Trocknungsplan des Trockenzelts (A-029): Etappen mit Dauer, Temperatur und Luftfeuchte — unabhängig vom Düngeprogramm.
/// Die Seite Plan › Trocknung liest und speichert ihn hier.
/// </summary>
[ApiController]
[Route("api/tents")]
[Produces("application/json")]
public sealed class TrocknungPlanApiController : ApiControllerBase
{
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungPlanQuelle _plan;

    public TrocknungPlanApiController(GrowRepository grows, HarvestRepository ernten, TrocknungPlanQuelle plan)
    {
        _grows = grows;
        _ernten = ernten;
        _plan = plan;
    }

    /// <summary>Der Trocknungsplan dieses Zelts: die Etappen, der Standard zum Zurücksetzen und welche Etappe gerade läuft.</summary>
    [HttpGet("{tentId:int}/trocknung/plan")]
    [ProducesResponseType(typeof(TrocknungPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<TrocknungPlanDto> Lesen(int tentId)
        => _grows.GetTent(tentId) is { } zelt ? Ok(Dto(zelt)) : NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");

    /// <summary>
    /// Den Plan speichern: die Etappen ersetzen den bisherigen Plan als Ganzes. Nur bei einem Zelt mit dem Zweck „Trocknung“.
    /// Hinter der letzten Etappe gilt sie weiter, bis das Trockengewicht eingetragen ist.
    /// </summary>
    [HttpPut("{tentId:int}/trocknung/plan")]
    [KiStufe(KiStufe.GrowPlanen)]
    [ProducesResponseType(typeof(TrocknungPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<TrocknungPlanDto> Speichern(int tentId, [FromBody] TrocknungPlanRequest request)
    {
        if (_grows.GetTent(tentId) is not { } zelt) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (zelt.TentType != TentType.Drying)
            return BadRequestError("kein_trockenzelt", "Einen Trocknungsplan gibt es nur bei einem Zelt mit dem Zweck „Trocknung“.");
        if (request?.Etappen is null || request.Etappen.Any(e => e is null))
            return ValidationError("Die Anfrage ist leer oder unlesbar.");

        var etappen = TrocknungsPlan.Bereinigt(request.Etappen);
        if (TrocknungsPlan.Pruefen(etappen) is { } fehler) return ValidationError(fehler);

        _plan.Speichern(tentId, etappen);
        return Ok(Dto(zelt));
    }

    private TrocknungPlanDto Dto(Tent zelt)
    {
        var stand = DryingWindow.StandFor(_grows, _ernten, zelt.Id, DateTime.Today);
        var etappen = TrocknungsPlan.Bereinigt(_plan.Etappen(zelt.Id, stand?.Grow));
        var standard = TrocknungsPlan.Bereinigt(TrocknungsPlan.Standard);
        TrocknungLaeuftDto? laeuft = null;
        if (stand is { } s && TrocknungsPlan.EtappeAm(etappen, s.Tag) is { } e)
            laeuft = new TrocknungLaeuftDto(zelt.Name, s.Tag, e.Index + 1, e.Etappe.Name, e.Gehalten);

        return new TrocknungPlanDto(
            zelt.Id, zelt.Name, zelt.TentType == TentType.Drying, IstGleich(etappen, standard),
            TrocknungsPlan.MaxTage, TrocknungsPlan.MaxEtappen, etappen, standard, laeuft);
    }

    private static bool IstGleich(IReadOnlyList<TrockenEtappe> a, IReadOnlyList<TrockenEtappe> b)
        => a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Name == p.Second.Name && p.First.DauerTage == p.Second.DauerTage
            && p.First.LufttemperaturC == p.Second.LufttemperaturC && p.First.LuftfeuchteZiel == p.Second.LuftfeuchteZiel
            && p.First.LuftfeuchteMax == p.Second.LuftfeuchteMax);
}

public sealed class TrocknungPlanRequest
{
    /// <summary>Die Etappen in der Reihenfolge, in der sie ablaufen (mindestens eine).</summary>
    public List<TrockenEtappe> Etappen { get; set; } = [];
}

public sealed record TrocknungPlanDto(
    int ZeltId,
    string ZeltName,
    bool IstTrockenzelt,
    bool IstStandard,
    int MaxTage,
    int MaxEtappen,
    IReadOnlyList<TrockenEtappe> Etappen,
    IReadOnlyList<TrockenEtappe> Standard,
    TrocknungLaeuftDto? Laeuft);
