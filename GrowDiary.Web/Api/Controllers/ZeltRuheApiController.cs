using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>
/// Fork AI (cc.277): „Zelt ruhend" — zwischen zwei Durchgängen die nicht benötigten Geräte ausschalten und später genau
/// das wieder einschalten, was ausgeschaltet wurde. Die Karte steht an der Zelt-Seite, das Blatt nach der Ernte fragt danach.
/// </summary>
/// <remarks>
/// Über einen Schlüssel braucht das Schalten beide Stufen („Geräte schalten" und „Verwaltung"): Es stellt die Automatik der
/// Steuerungen um, nicht nur ein Gerät. Das Lesen ist frei.
/// </remarks>
[ApiController]
[KiStufe(KiStufe.Verwaltung)]
[Route("api/steuerung/zelt-ruhe")]
[Produces("application/json")]
public sealed class ZeltRuheApiController : ApiControllerBase
{
    private readonly ZeltRuheService _ruhe;

    public ZeltRuheApiController(ZeltRuheService ruhe)
    {
        _ruhe = ruhe;
    }

    /// <summary>Ruhend oder in Betrieb, und jedes Gerät mit seinem Zustand und ob es im Ruhemodus ausgeht.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ZeltRuheStand), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZeltRuheStand>> Stand([FromQuery] int? zeltId, CancellationToken ct)
        => Ok(await _ruhe.StandAsync(zeltId, ct));

    /// <summary>Speichert, welche Geräte im Ruhemodus ausgehen (ohne etwas zu schalten).</summary>
    [HttpPut("auswahl")]
    [ProducesResponseType(typeof(ZeltRuheStand), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZeltRuheStand>> AuswahlSpeichern([FromBody] ZeltRuheAuswahlRequest request, [FromQuery] int? zeltId, CancellationToken ct)
    {
        if (request?.Geraete is null) return BadRequestError("auswahl_invalid", "Es wurde keine Geräteliste übergeben.");
        var (ok, fehler) = _ruhe.AuswahlSpeichern(request.Geraete, zeltId);
        if (!ok) return BadRequestError("auswahl_unbekannt", fehler!);
        return Ok(await _ruhe.StandAsync(zeltId, ct));
    }

    /// <summary>
    /// Zelt ruhend schalten: Automatik und Gerät aus, je Gerät mit dem Zustand davor gemerkt. Mit <c>geraete</c> gilt diese
    /// Auswahl (und wird gemerkt), ohne die gespeicherte.
    /// </summary>
    [HttpPost("ruhend")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(ZeltRuheErgebnis), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZeltRuheErgebnis>> Ruhend([FromBody] ZeltRuheRuhendRequest? request, [FromQuery] int? zeltId, CancellationToken ct)
        => Ok(await _ruhe.RuhendAsync(request?.Geraete, zeltId, ct));

    /// <summary>Zelt wieder aktiv: genau das wieder an, was der Fork ausgeschaltet hat — Gerät zuerst, dann die Automatik.</summary>
    [HttpPost("aktiv")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(ZeltRuheErgebnis), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZeltRuheErgebnis>> Aktiv([FromQuery] int? zeltId, CancellationToken ct)
        => Ok(await _ruhe.AktivAsync(zeltId, ct));
}

public sealed class ZeltRuheAuswahlRequest
{
    public List<string>? Geraete { get; set; }
}

public sealed class ZeltRuheRuhendRequest
{
    public List<string>? Geraete { get; set; }
}
