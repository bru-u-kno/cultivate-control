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
    /// Betriebsart und „Folgt dem Plan“ je Gerät setzen. Das PUT ersetzt alle Angaben; nur „abluftFolgt“ darf fehlen (dann bleibt es). Bei „regeln“ zieht der Fork die
    /// Grenzen sofort nach (danach alle fünf Minuten) und schaltet den Entfeuchter ein und aus (jede Minute); bei „melden“ schreibt er nichts. Die Grenzwerte
    /// ändern nur die Auslöser; den Port des Entfeuchters schaltet er nur zwischen On und Off, nie auf Auto.
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

        // Ein Aufruf ohne „abluftFolgt“ (ältere Clients) lässt den Schalter der Abluft, wie er ist.
        var abluftFolgt = request.AbluftFolgt ?? _steuerung.Get(tentId).AbluftFolgt;
        if (abluftFolgt && !zuordnung.ContainsKey(TrocknungZiel.RolleAbluft))
            return BadRequestError("kein_port", "Der Abluft ist noch kein Port zugeordnet.");

        // Solange der Vorgang läuft, sieht jede Seite (auch die anderer Benutzer) im Stand „inArbeit“ mit der Phase.
        using var arbeit = _dienst.ArbeitBeginnen(tentId, request.ClientId);
        _steuerung.Set(tentId, new TrocknungSteuerungRepository.Einstellung(request.Betrieb, request.EntfeuchterFolgt, request.KuehlungFolgt, abluftFolgt));
        // Sofort nachziehen, nicht erst im nächsten Takt: Wer „Regeln“ einschaltet, will das Ergebnis sehen.
        arbeit.Phase(TrocknungSteuerungService.PhaseAbgleichen);
        await _dienst.AbgleichenAsync(tentId, ct);
        arbeit.Phase(TrocknungSteuerungService.PhasePruefen);
        var stand = await _dienst.StandAsync(tentId, ct);
        // Die Antwort gilt erst nach dem Ende: sie meldet „fertig“, nicht „in Arbeit“.
        return Ok(Dto(stand with { InArbeit = null }));
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
            g.ZuletztGesetztUtc is { } z ? DateTime.SpecifyKind(z, DateTimeKind.Utc).ToString("O") : null,
            g.Schalten is { } sc ? new TrocknungSchaltDto(
                sc.Zustand, sc.Feuchte, sc.EinAb, sc.AusUnter, sc.Modus,
                sc.ZuletztUtc is { } sz ? DateTime.SpecifyKind(sz, DateTimeKind.Utc).ToString("O") : null, sc.ZuletztAktion, sc.Gescheitert) : null)).ToList(),
        s.InArbeit is { } a ? new TrocknungArbeitDto(a.Phase, DateTime.SpecifyKind(a.SeitUtc, DateTimeKind.Utc).ToString("O"), a.ClientId) : null);
}

public sealed class TrocknungSteuerungRequest
{
    /// <summary>„melden“ (nichts schreiben, nur zeigen) oder „regeln“ (Grenzen auf den Plan setzen).</summary>
    public string Betrieb { get; set; } = TrocknungSteuerungRepository.BetriebMelden;

    /// <summary>Schaltet der Fork den Entfeuchter-Port nach dem Plan ein und aus (ab dem Ziel der Etappe, aus unter dem Band)?</summary>
    public bool EntfeuchterFolgt { get; set; }

    /// <summary>Folgt die Temperatur-Obergrenze am Kühlungs-Port dem Plan?</summary>
    public bool KuehlungFolgt { get; set; }

    /// <summary>
    /// Folgen die Luftfeuchte- und die Temperatur-Obergrenze am Abluft-Port dem Plan? Fehlt die Angabe, bleibt der Schalter.
    /// </summary>
    public bool? AbluftFolgt { get; set; }

    /// <summary>Kennung der Seite, die speichert (frei gewählt, nur zum Wiedererkennen): damit weiß jede Seite, ob der Vorgang ihr gehört oder einem anderen Benutzer.</summary>
    public string? ClientId { get; set; }
}

public sealed record TrocknungSteuerungGeraetDto(
    string Rolle, string? PortName, bool Folgt, string? Modus, double? WertAmGeraet, string? Einheit, double? PlanWert,
    string Urteil, double? Vorher, string? ZuletztGesetzt, TrocknungSchaltDto? Schalten = null);

/// <summary>Der Entfeuchter, den der Fork schaltet: Zustand (laeuft, steht, wartet, schaltetEin, schaltetAus, portNichtSchaltbar, keinMesswert), Schwellen und letzter Schaltvorgang.</summary>
public sealed record TrocknungSchaltDto(
    string Zustand, double? Feuchte, double? EinAb, double? AusUnter, string? Modus, string? ZuletztUtc, string? ZuletztAktion, bool Gescheitert);

public sealed record TrocknungSteuerungDto(
    bool Trocknet, string Betrieb, bool PortsErreichbar, int? Tag, int? TageGeplant, string? Etappe,
    double? TempMin, double? TempMax, double? FeuchteMin, double? FeuchteMax, IReadOnlyList<TrocknungSteuerungGeraetDto> Geraete,
    TrocknungArbeitDto? InArbeit);

/// <summary>Ein Vorgang läuft gerade: Phase „speichern“, „abgleichen“ oder „pruefen“; clientId nennt, welche Seite ihn angestoßen hat.</summary>
public sealed record TrocknungArbeitDto(string Phase, string Seit, string? ClientId);

public sealed record TrocknungZuruecksetzenDto(int Zurueck, int Gescheitert, TrocknungSteuerungDto Stand);
