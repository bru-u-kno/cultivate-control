using GrowDiary.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>
/// Fork AI: Welche Reinigung nach einem abgeschlossenen Grow empfohlen wird (Aufgaben › Fällige Routinen und das Blatt nach der Ernte).
/// </summary>
[ApiController]
[Route("api/reinigung-nach-grow")]
[Produces("application/json")]
public sealed class ReinigungNachGrowApiController : ApiControllerBase
{
    private readonly ReinigungNachGrowService _reinigung;

    public ReinigungNachGrowApiController(ReinigungNachGrowService reinigung) => _reinigung = reinigung;

    /// <summary>Die empfohlenen Reinigungen — leer, wenn nichts ansteht.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ReinigungNachGrow>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ReinigungNachGrow>> Empfohlen() => Ok(_reinigung.Empfohlen(DateTime.Today));
}
