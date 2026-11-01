using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>
/// Geräte am Trockenzelt folgen dem Plan (A-029, Etappe 3, Teil B). Die Seite „Steuerung › Trocknung“ liest und stellt das ein;
/// die Live-Seite zeigt nur Werte (Teil A).
/// </summary>
[ApiController]
[Route("api/tents")]
[Produces("application/json")]
public sealed class TrocknungSteuerungApiController : ApiControllerBase
{
    private readonly GrowRepository _grows;
    private readonly TrocknungPortRepository _ports;
    private readonly TrocknungSteuerungRepository _steuerung;
    private readonly TrocknungSteuerungService _dienst;

    public TrocknungSteuerungApiController(GrowRepository grows, TrocknungPortRepository ports, TrocknungSteuerungRepository steuerung, TrocknungSteuerungService dienst)
    {
        _grows = grows;
        _ports = ports;
        _steuerung = steuerung;
        _dienst = dienst;
    }

    /// <summary>
    /// Die Steuerung der Trocknung: Betriebsart („melden“ oder „regeln“), je Gerät „Folgt dem Plan“, der Wert am Gerät, was der
    /// Plan verlangt und der Wert davor. Nur lesend.
    /// </summary>
    [HttpGet("{tentId:int}/trocknung/steuerung")]
    [ProducesResponseType(typeof(TrocknungSteuerungDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrocknungSteuerungDto>> Lesen(int tentId, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        return Ok(Dto(await _dienst.StandAsync(tentId, ct)));
    }

    /// <summary>
    /// Betriebsart und „Folgt dem Plan“ je Gerät setzen. Das PUT ersetzt alle drei Angaben. Bei „regeln“ zieht der Fork die
    /// Grenzen sofort nach (danach alle fünf Minuten); bei „melden“ schreibt er nichts. Ändert nur die Grenzwerte der Auslöser,
    /// nie den Modus des Ports.
    /// </summary>
    [HttpPut("{tentId:int}/trocknung/steuerung")]
    [KiStufe(KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(TrocknungSteuerungDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrocknungSteuerungDto>> Setzen(int tentId, [FromBody] TrocknungSteuerungRequest request, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is not { } zelt) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (zelt.TentType != TentType.Drying)
            return BadRequestError("kein_trockenzelt", "Die Steuerung der Trocknung gibt es nur bei einem Zelt mit dem Zweck „Trocknung“.");
        if (request.Betrieb is not (TrocknungSteuerungRepository.BetriebMelden or TrocknungSteuerungRepository.BetriebRegeln))
            return BadRequestError("betrieb_unbekannt", "Die Betriebsart ist „melden“ oder „regeln“.");

        var zuordnung = _ports.Get(tentId);
        if (request.EntfeuchterFolgt && !zuordnung.ContainsKey(TrocknungZiel.RolleEntfeuchter))
            return BadRequestError("kein_port", "Dem Entfeuchter ist noch kein Port zugeordnet.");
        if (request.KuehlungFolgt && !zuordnung.ContainsKey(TrocknungZiel.RolleKuehlung))
            return BadRequestError("kein_port", "Der Kühlung ist noch kein Port zugeordnet.");

        _steuerung.Set(tentId, new TrocknungSteuerungRepository.Einstellung(request.Betrieb, request.EntfeuchterFolgt, request.KuehlungFolgt));
        // Sofort nachziehen, nicht erst im nächsten Takt: Wer „Regeln“ einschaltet, will das Ergebnis sehen.
        await _dienst.AbgleichenAsync(tentId, ct);
        return Ok(Dto(await _dienst.StandAsync(tentId, ct)));
    }

    /// <summary>
    /// „Alte Werte zurück“: setzt die Grenzen auf den Stand vor der ersten Übernahme und stellt auf „melden“. Eine Grenze, die
    /// sich nicht zurücksetzen ließ, behält ihren Merker; die Antwort sagt, wie viele zurückgingen.
    /// </summary>
    [HttpPost("{tentId:int}/trocknung/steuerung/zuruecksetzen")]
    [KiStufe(KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(TrocknungZuruecksetzenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrocknungZuruecksetzenDto>> Zuruecksetzen(int tentId, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        var ergebnis = await _dienst.ZuruecksetzenAsync(tentId, ct);
        return Ok(new TrocknungZuruecksetzenDto(ergebnis.Geschrieben, ergebnis.Gescheitert, Dto(await _dienst.StandAsync(tentId, ct))));
    }

    internal static TrocknungSteuerungDto Dto(TrocknungSteuerungService.Stand s) => new(
        s.Trocknet, s.Betrieb, s.PortsErreichbar, s.Tag, s.TageGeplant, s.Etappe, s.TempMin, s.TempMax, s.FeuchteMin, s.FeuchteMax,
        s.Geraete.Select(g => new TrocknungSteuerungGeraetDto(
            g.Rolle, g.PortName, g.Folgt, g.Modus, g.WertAmGeraet, g.Einheit, g.PlanWert, g.Urteil, g.Vorher,
            g.ZuletztGesetztUtc is { } z ? DateTime.SpecifyKind(z, DateTimeKind.Utc).ToString("O") : null)).ToList());
}

public sealed class TrocknungSteuerungRequest
{
    /// <summary>„melden“ (nichts schreiben, nur zeigen) oder „regeln“ (Grenzen auf den Plan setzen).</summary>
    public string Betrieb { get; set; } = TrocknungSteuerungRepository.BetriebMelden;

    /// <summary>Folgt die Luftfeuchte-Obergrenze am Entfeuchter-Port dem Plan?</summary>
    public bool EntfeuchterFolgt { get; set; }

    /// <summary>Folgt die Temperatur-Obergrenze am Kühlungs-Port dem Plan?</summary>
    public bool KuehlungFolgt { get; set; }
}

public sealed record TrocknungSteuerungGeraetDto(
    string Rolle, string? PortName, bool Folgt, string? Modus, double? WertAmGeraet, string? Einheit, double? PlanWert,
    string Urteil, double? Vorher, string? ZuletztGesetzt);

public sealed record TrocknungSteuerungDto(
    bool Trocknet, string Betrieb, bool PortsErreichbar, int? Tag, int? TageGeplant, string? Etappe,
    double? TempMin, double? TempMax, double? FeuchteMin, double? FeuchteMax, IReadOnlyList<TrocknungSteuerungGeraetDto> Geraete);

public sealed record TrocknungZuruecksetzenDto(int Zurueck, int Gescheitert, TrocknungSteuerungDto Stand);
