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
    private readonly TrocknungZuluftService _zuluft;
    private readonly SteuerungGeraeteService _geraete;

    public TrocknungSteuerungApiController(GrowRepository grows, TrocknungPortRepository ports, TrocknungSteuerungRepository steuerung, TrocknungSteuerungService dienst,
        TrocknungZuluftService zuluft, SteuerungGeraeteService geraete)
    {
        _zuluft = zuluft;
        _geraete = geraete;
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

        // Mindeststufe der Abluft: fehlt die Angabe, bleibt sie; 0 heißt „keine“.
        var minStufe = request.AbluftMinStufe ?? _steuerung.Get(tentId).AbluftMinStufe;
        if (!TrocknungAbluftStufe.Gueltig(minStufe))
            return BadRequestError("stufe_ungueltig", $"Die Mindeststufe der Abluft ist eine ganze Zahl von 0 bis {TrocknungAbluftStufe.HoechstStufe}.");
        if (minStufe > 0 && !zuordnung.ContainsKey(TrocknungZiel.RolleAbluft))
            return BadRequestError("kein_port", "Der Abluft ist noch kein Port zugeordnet.");

        // Solange der Vorgang läuft, sieht jede Seite (auch die anderer Benutzer) im Stand „inArbeit“ mit der Phase.
        using var arbeit = _dienst.ArbeitBeginnen(tentId, request.ClientId);
        _steuerung.Set(tentId, _steuerung.Get(tentId) with { Betrieb = request.Betrieb, EntfeuchterFolgt = request.EntfeuchterFolgt, KuehlungFolgt = request.KuehlungFolgt, AbluftFolgt = abluftFolgt, AbluftMinStufe = minStufe });
        // Sofort nachziehen, nicht erst im nächsten Takt: Wer „Regeln“ einschaltet, will das Ergebnis sehen.
        arbeit.Phase(TrocknungSteuerungService.PhaseAbgleichen);
        await _dienst.AbgleichenAsync(tentId, ct);
        arbeit.Phase(TrocknungSteuerungService.PhasePruefen);
        var stand = await _dienst.StandAsync(tentId, ct);
        // Die Antwort gilt erst nach dem Ende: sie meldet „fertig“, nicht „in Arbeit“.
        return Ok(Dto(stand with { InArbeit = null }));
    }

    /// <summary>
    /// Die Zuluft im Trocknungsbetrieb (cc.337): Folgt sie dem Trocknungsplan, mit Höchststufe und Abstand unter dem Planwert, dazu die
    /// Messwerte und was der Fork gerade entscheidet. Das Zelt ist das erste mit dem Zweck „Trocknung“; gibt es keines, steht
    /// <c>trockenzelt</c> auf false. Nur lesend.
    /// </summary>
    [HttpGet("~/api/trocknung/zuluft")]
    [ProducesResponseType(typeof(TrocknungZuluftDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TrocknungZuluftDto>> ZuluftLesen(CancellationToken ct)
    {
        if (_zuluft.Trockenzelt() is not { } zelt) return Ok(TrocknungZuluftDto.Kein);
        return Ok(ZuluftDto(await _zuluft.StandAsync(zelt.Id, ct)));
    }

    /// <summary>
    /// Schalter „Folgt dem Trocknungsplan“, Höchststufe und Abstand unter dem Planwert der Zuluft setzen. Beim Einschalten stellt der Fork die
    /// Betriebsart auf „regeln“ (die anderen Geräte folgen nur mit ihrem eigenen Haken). Fehlt eine Angabe, bleibt sie.
    /// </summary>
    [HttpPut("~/api/trocknung/zuluft")]
    [KiStufe(KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(TrocknungZuluftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TrocknungZuluftDto>> ZuluftSetzen([FromBody] TrocknungZuluftRequest request, CancellationToken ct)
    {
        if (_zuluft.Trockenzelt() is not { } zelt)
            return BadRequestError("kein_trockenzelt", "Die Zuluft im Trocknungsbetrieb gibt es nur mit einem Zelt mit dem Zweck „Trocknung“.");

        var jetzt = _steuerung.Get(zelt.Id);
        var hoechst = request.Hoechststufe ?? jetzt.ZuluftHoechststufe;
        var abstand = request.AbstandProzent ?? jetzt.ZuluftAbstandProzent;
        if (hoechst is < 1 or > 10) return BadRequestError("stufe_ungueltig", "Die Höchststufe ist eine ganze Zahl von 1 bis 10.");
        if (abstand is < 0 or > 20) return BadRequestError("abstand_ungueltig", "Der Abstand unter dem Planwert ist eine ganze Zahl von 0 bis 20 Prozentpunkten.");

        var folgt = request.Folgt ?? jetzt.ZuluftFolgt;
        if (folgt && string.IsNullOrWhiteSpace(_geraete.EntitiesFuerModul(ZuluftSteuerungService.Modul).GetValueOrDefault(ZuluftSteuerungService.Rollen.PortSchalter)))
            return BadRequestError("kein_port", "Der Zuluft ist noch kein Schalter zugeordnet (Steuerung › Zuluft › Geräte).");

        var betrieb = folgt && !jetzt.ZuluftFolgt ? TrocknungSteuerungRepository.BetriebRegeln : jetzt.Betrieb;
        _steuerung.Set(zelt.Id, jetzt with { Betrieb = betrieb, ZuluftFolgt = folgt, ZuluftHoechststufe = hoechst, ZuluftAbstandProzent = abstand });
        // Sofort einen Lauf, nicht erst im nächsten Takt: Wer einschaltet, will das Ergebnis sehen; wer ausschaltet, die Rückgabe an die Regelung.
        await _zuluft.LaufAsync(zelt.Id, ct);
        return Ok(ZuluftDto(await _zuluft.StandAsync(zelt.Id, ct)));
    }

    internal static TrocknungZuluftDto ZuluftDto(TrocknungZuluftService.Stand s) => new(
        s.Trockenzelt, s.TentId, s.ZeltName, s.Trocknet, s.Folgt, s.Regelt, s.Hoechststufe, s.AbstandProzent,
        s.ZeltTempC, s.ZeltRhProzent, s.ZeltAbsolutGm3, s.AussenAbsolutGm3, s.PlanRhProzent, s.PlanTempC, s.Tag, s.TageGeplant,
        s.Zustand, s.Stufe, s.Differenz, s.LaeuftStufe, s.Gescheitert);

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
                sc.ZuletztUtc is { } sz ? DateTime.SpecifyKind(sz, DateTimeKind.Utc).ToString("O") : null, sc.ZuletztAktion, sc.Gescheitert) : null,
            g.Stufe is { } st ? new TrocknungStufeDto(st.Mindest, st.Jetzt, st.Zustand, st.Modus, st.Gescheitert) : null)).ToList(),
        s.InArbeit is { } a ? new TrocknungArbeitDto(a.Phase, DateTime.SpecifyKind(a.SeitUtc, DateTimeKind.Utc).ToString("O"), a.ClientId) : null,
        s.AbluftMinStufe);
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

    /// <summary>Mindeststufe der Abluft, 0 (keine) bis 10. Fehlt die Angabe, bleibt der gespeicherte Wert. Wirkt im Modus On des Abluft-Ports und nur bei eingebundener Abluft.</summary>
    public int? AbluftMinStufe { get; set; }

    /// <summary>Kennung der Seite, die speichert (frei gewählt, nur zum Wiedererkennen): damit weiß jede Seite, ob der Vorgang ihr gehört oder einem anderen Benutzer.</summary>
    public string? ClientId { get; set; }
}

public sealed record TrocknungSteuerungGeraetDto(
    string Rolle, string? PortName, bool Folgt, string? Modus, double? WertAmGeraet, string? Einheit, double? PlanWert,
    string Urteil, double? Vorher, string? ZuletztGesetzt, TrocknungSchaltDto? Schalten = null, TrocknungStufeDto? Stufe = null);

/// <summary>Die Abluft mit Mindeststufe: Zustand (haelt, hebtAn, portNichtOn, keinWert), die Stufe jetzt und der Modus des Ports.</summary>
public sealed record TrocknungStufeDto(int Mindest, double? Jetzt, string Zustand, string? Modus, bool Gescheitert);

/// <summary>Der Entfeuchter, den der Fork schaltet: Zustand (laeuft, steht, wartet, schaltetEin, schaltetAus, portNichtSchaltbar, keinMesswert), Schwellen und letzter Schaltvorgang.</summary>
public sealed record TrocknungSchaltDto(
    string Zustand, double? Feuchte, double? EinAb, double? AusUnter, string? Modus, string? ZuletztUtc, string? ZuletztAktion, bool Gescheitert);

public sealed record TrocknungSteuerungDto(
    bool Trocknet, string Betrieb, bool PortsErreichbar, int? Tag, int? TageGeplant, string? Etappe,
    double? TempMin, double? TempMax, double? FeuchteMin, double? FeuchteMax, IReadOnlyList<TrocknungSteuerungGeraetDto> Geraete,
    TrocknungArbeitDto? InArbeit, int AbluftMinStufe = 0);

/// <summary>Ein Vorgang läuft gerade: Phase „speichern“, „abgleichen“ oder „pruefen“; clientId nennt, welche Seite ihn angestoßen hat.</summary>
public sealed record TrocknungArbeitDto(string Phase, string Seit, string? ClientId);

public sealed record TrocknungZuruecksetzenDto(int Zurueck, int Gescheitert, TrocknungSteuerungDto Stand);

public sealed class TrocknungZuluftRequest
{
    /// <summary>Folgt die Zuluft dem Trocknungsplan? Fehlt die Angabe, bleibt der Schalter.</summary>
    public bool? Folgt { get; set; }

    /// <summary>Höchststufe im Trocknungsbetrieb, 1 bis 10. Fehlt die Angabe, bleibt sie.</summary>
    public int? Hoechststufe { get; set; }

    /// <summary>Liegt das Zelt mehr als so viele Prozentpunkte unter dem Planwert, bleibt die Zuluft aus (0 bis 20). Fehlt die Angabe, bleibt sie.</summary>
    public int? AbstandProzent { get; set; }
}

/// <summary>
/// Zuluft im Trocknungsbetrieb. <c>zustand</c>: saugt, zeltZuKalt, frost, zeltZuTrocken, zeltImPlan, aussenFeuchter, keinMesswert;
/// <c>stufe</c> die Stufe, die der Fork jetzt will (0 = aus); <c>regelt</c>: der Fork führt die Zuluft gerade wirklich.
/// </summary>
public sealed record TrocknungZuluftDto(
    bool Trockenzelt, int? TentId, string? ZeltName, bool Trocknet, bool Folgt, bool Regelt, int Hoechststufe, int AbstandProzent,
    double? ZeltTempC, double? ZeltRhProzent, double? ZeltAbsolutGm3, double? AussenAbsolutGm3, double? PlanRhProzent, double? PlanTempC,
    int? Tag, int? TageGeplant, string Zustand, int Stufe, double? Differenz, double? LaeuftStufe, bool Gescheitert)
{
    public static TrocknungZuluftDto Kein { get; } = new(
        false, null, null, false, false, false, TrocknungSteuerungRepository.ZuluftHoechststufeVorgabe, TrocknungSteuerungRepository.ZuluftAbstandVorgabe,
        null, null, null, null, null, null, null, null, "keinMesswert", 0, null, null, false);
}
