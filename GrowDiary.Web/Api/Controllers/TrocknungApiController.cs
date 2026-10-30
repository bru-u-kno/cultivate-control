using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>Was in einem Zelt gerade trocknet (cc.281) — die Karte „Trocknung" an der Zelt-Seite liest das.</summary>
[ApiController]
[Route("api/tents")]
[Produces("application/json")]
public sealed class TrocknungApiController : ApiControllerBase
{
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _harvests;
    private readonly KnowledgeBaseLoader _wissen;

    public TrocknungApiController(GrowRepository grows, HarvestRepository harvests, KnowledgeBaseLoader wissen)
    {
        _grows = grows;
        _harvests = harvests;
        _wissen = wissen;
    }

    /// <summary>Die Charge im Zelt: Grow, Trocknungstag, Etappe und deren Zielband. <c>trocknet: false</c>, wenn nichts hängt.</summary>
    [HttpGet("{tentId:int}/trocknung")]
    [ProducesResponseType(typeof(TrocknungStandDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<TrocknungStandDto> Stand(int tentId)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand) return Ok(TrocknungStandDto.Leer);

        var programm = _wissen.NutrientPrograms.FirstOrDefault(p =>
            string.Equals(p.Id, stand.Grow.FeedProgramId, StringComparison.OrdinalIgnoreCase));
        var etappen = TrocknungsPlan.Fuer(programm);
        var geplant = etappen.Sum(e => Math.Max(1, e.DauerTage));
        var etappe = TrocknungsPlan.EtappeAm(etappen, stand.Tag);
        var band = etappe is null ? (TrocknungsPlan.Band?)null : TrocknungsPlan.BandFuer(etappe.Etappe);
        return Ok(new TrocknungStandDto(
            true, stand.Grow.Id, stand.Grow.Name, stand.Tag, geplant, etappe?.Etappe.Name,
            band?.TempMin, band?.TempMax, band?.FeuchteMin, band?.FeuchteMax));
    }
}

public sealed record TrocknungStandDto(
    bool Trocknet,
    int? GrowId,
    string? GrowName,
    int? Tag,
    int? TageGeplant,
    string? Etappe,
    double? TempMin,
    double? TempMax,
    double? FeuchteMin,
    double? FeuchteMax)
{
    public static TrocknungStandDto Leer { get; } = new(false, null, null, null, null, null, null, null, null, null);
}
