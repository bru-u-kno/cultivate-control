using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
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
    private readonly TrocknungPlanQuelle _plan;
    private readonly TrocknungWaegungRepository _waegungen;
    private readonly TrocknungPortRepository _ports;
    private readonly TrocknungZielService _ziel;

    public TrocknungApiController(GrowRepository grows, HarvestRepository harvests, TrocknungPlanQuelle plan, TrocknungWaegungRepository waegungen, TrocknungPortRepository ports, TrocknungZielService ziel)
    {
        _waegungen = waegungen;
        _ports = ports;
        _ziel = ziel;
        _grows = grows;
        _harvests = harvests;
        _plan = plan;
    }

    /// <summary>Die Charge im Zelt: Grow, Trocknungstag, Etappe und deren Zielband. <c>trocknet: false</c>, wenn nichts hängt.</summary>
    [HttpGet("{tentId:int}/trocknung")]
    [ProducesResponseType(typeof(TrocknungStandDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<TrocknungStandDto> Stand(int tentId)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand) return Ok(TrocknungStandDto.Leer);

        var (geplant, etappe, band) = PlanFuer(tentId, stand);
        return Ok(new TrocknungStandDto(
            true, stand.Grow.Id, stand.Grow.Name, stand.Tag, geplant, etappe?.Etappe.Name,
            band?.TempMin, band?.TempMax, band?.FeuchteMin, band?.FeuchteMax));
    }

    /// <summary>Etappe und Band, die für diesen Stand gelten — EINE Stelle für Kachel, Karte „Trocknungs-Ziel“ und alles Spätere.</summary>
    private (int Geplant, TrocknungsPlan.Stand? Etappe, TrocknungsPlan.Band? Band) PlanFuer(int tentId, DryingWindow.Stand stand)
        => _plan.Fuer(tentId, stand);

    /// <summary>Die Fertig-Erkennung: Wägungen, Kurve in Prozent, „sinkt / hält sich / Ziel“ und die nächste Wägung.</summary>
    [HttpGet("{tentId:int}/trocknung/fertig")]
    [ProducesResponseType(typeof(FertigStandDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<FertigStandDto> Fertig(int tentId)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        return Ok(FertigStand(tentId));
    }

    /// <summary>Eine Wägung eintragen (Probe oder ganze Charge). Der erste Eintrag legt fest, was gewogen wird.</summary>
    [HttpPost("{tentId:int}/trocknung/waegungen")]
    [KiStufe(KiStufe.Dokumentieren)]
    [ProducesResponseType(typeof(FertigStandDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<FertigStandDto> WaegungEintragen(int tentId, [FromBody] WaegungRequest request)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand)
            return BadRequestError("keine_trocknung", "In diesem Zelt trocknet gerade nichts — eine Wägung gehört zu einer laufenden Trocknung.");
        if (!double.IsFinite(request.GewichtG) || request.GewichtG <= 0 || request.GewichtG > 200_000)
            return BadRequestError("gewicht_unplausibel", "Das Gewicht muss größer als 0 g und höchstens 200.000 g sein.");

        var bisher = _waegungen.GetForGrow(stand.Grow.Id);
        var ernte = _harvests.GetForGrow(stand.Grow.Id);

        var zeitpunkt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.Datum))
        {
            if (!DateTime.TryParseExact(request.Datum, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var tag))
                return BadRequestError("datum_unlesbar", "Das Datum der Wägung muss als JJJJ-MM-TT angegeben werden.");
            if (tag.Date > DateTime.Today)
                return BadRequestError("datum_zukunft", "Eine Wägung kann nicht in der Zukunft liegen.");
            if (ernte is not null && tag.Date < ernte.HarvestedAt.Date)
                return BadRequestError("datum_vor_ernte", "Eine Wägung kann nicht vor der Ernte liegen.");
            // Heute gilt der genaue Zeitpunkt; ein früherer Tag zählt mittags (Ortszeit), damit er sicher auf diesen Tag fällt.
            if (tag.Date < DateTime.Today)
                zeitpunkt = DateTime.SpecifyKind(tag.Date.AddHours(12), DateTimeKind.Local).ToUniversalTime();
        }
        // Was gewogen wird, legt die erste Wägung fest — sonst vergleicht die Kurve Äpfel mit Birnen.
        var ganzeCharge = bisher.Count > 0 ? bisher[0].GanzeCharge : request.GanzeCharge;
        // Nur die ERSTE Wägung braucht das Frischgewicht. Fällt es später weg, rechnet die Kurve gegen die erste Wägung
        // weiter (das war schon die ganze Charge) — der Nutzer soll nicht am Wiegen gehindert werden.
        if (bisher.Count == 0 && ganzeCharge && ernte?.WetWeightG is not > 0)
            return BadRequestError("frischgewicht_fehlt", "Für die ganze Charge braucht die Ernte ein Frischgewicht. Wiege eine Probe, oder trage das Frischgewicht bei der Ernte ein.");

        // Doppelklick: dieselbe Zahl innerhalb einer halben Minute ist dieselbe Wägung, nicht eine zweite.
        if (bisher.Any(w => (zeitpunkt - w.ZeitpunktUtc).Duration() < TimeSpan.FromSeconds(30) && Math.Abs(w.GewichtG - request.GewichtG) < 1e-9))
            return Ok(FertigStand(tentId));

        _waegungen.Add(new TrocknungWaegung { GrowId = stand.Grow.Id, GewichtG = request.GewichtG, GanzeCharge = ganzeCharge, ZeitpunktUtc = zeitpunkt });
        return Ok(FertigStand(tentId));
    }

    /// <summary>Eine Wägung löschen (Tippfehler).</summary>
    [HttpDelete("{tentId:int}/trocknung/waegungen/{id:int}")]
    [KiStufe(KiStufe.Dokumentieren)]
    [KiSicherungVorher]
    [ProducesResponseType(typeof(FertigStandDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public ActionResult<FertigStandDto> WaegungLoeschen(int tentId, int id)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand)
            return NotFoundError("keine_trocknung", "In diesem Zelt trocknet gerade nichts.");
        if (!_waegungen.Delete(id, stand.Grow.Id)) return NotFoundError("waegung_not_found", $"Wägung {id} gibt es bei dieser Charge nicht.");
        return Ok(FertigStand(tentId));
    }

    /// <summary>
    /// Das Trocknungs-Ziel des Zelts (A-029, Etappe 3, Teil A): Was der Plan von Entfeuchter und Kühlung verlangt — und was an
    /// den Ports des Controllers eingestellt ist. Nur lesend; es wird nichts geschrieben.
    /// </summary>
    [HttpGet("{tentId:int}/trocknung/ziel")]
    [ProducesResponseType(typeof(ZielDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ZielDto>> Ziel(int tentId, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand) return Ok(ZielDto.Leer);

        var (_, etappe, band) = PlanFuer(tentId, stand);
        var (erreichbar, vergleiche) = await _ziel.VergleicheAsync(_ports.Get(tentId), band?.FeuchteMax, band?.TempMax, ct);
        return Ok(new ZielDto(
            true, stand.Tag, etappe?.Etappe.Name,
            etappe?.Etappe.LufttemperaturC, etappe?.Etappe.LuftfeuchteZiel, band?.TempMax, band?.FeuchteMax,
            erreichbar,
            vergleiche.Select(v => new ZielGeraetDto(
                v.Rolle, UrteilName(v.Urteil), v.PortName, v.Modus, v.WertAmGeraet, v.Einheit, v.PlanWert)).ToList()));
    }

    /// <summary>Die zugeordneten Ports dieses Zelts und die Ports mit eigener Automatik, die zur Wahl stehen.</summary>
    [HttpGet("{tentId:int}/trocknung/ports")]
    [ProducesResponseType(typeof(PortsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortsDto>> PortsLesen(int tentId, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is null) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        return Ok(await PortsDtoAsync(tentId, frisch: true, ct));
    }

    /// <summary>
    /// Ports zuordnen: welcher Port des Controllers ist am Trockenzelt der Entfeuchter, welcher die Kühlung. Leer löst die
    /// Zuordnung. Es werden nur Ports angenommen, die Home Assistant gerade als Ports mit eigener Automatik kennt.
    /// <b>Das PUT ersetzt beide Rollen:</b> Eine weggelassene Rolle wird gelöst, nicht beibehalten.
    /// </summary>
    [HttpPut("{tentId:int}/trocknung/ports")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(PortsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortsDto>> PortsSetzen(int tentId, [FromBody] PortsRequest request, CancellationToken ct)
    {
        if (_grows.GetTent(tentId) is not { } zelt) return NotFoundError("tent_not_found", $"Zelt mit Id {tentId} existiert nicht.");
        if (zelt.TentType != TentType.Drying)
            return BadRequestError("kein_trockenzelt", "Ports lassen sich nur einem Zelt mit dem Zweck „Trocknung“ zuordnen.");

        var entfeuchter = string.IsNullOrWhiteSpace(request.Entfeuchter) ? null : request.Entfeuchter.Trim();
        var kuehlung = string.IsNullOrWhiteSpace(request.Kuehlung) ? null : request.Kuehlung.Trim();
        if (entfeuchter is not null && entfeuchter == kuehlung)
            return BadRequestError("port_doppelt", "Entfeuchter und Kühlung können nicht derselbe Port sein.");

        if (entfeuchter is not null || kuehlung is not null)
        {
            var ports = await _ziel.PortsAsync(frisch: true, ct);
            if (ports is null)
                return BadRequestError("ha_nicht_erreichbar", "Home Assistant ist gerade nicht erreichbar — die Ports lassen sich nicht prüfen.");
            foreach (var deviceId in new[] { entfeuchter, kuehlung }.Where(d => d is not null))
            {
                if (ports.All(p => p.DeviceId != deviceId))
                    return BadRequestError("port_unbekannt", "Dieser Port ist Home Assistant nicht als Port mit eigener Automatik bekannt.");
            }
        }

        _ports.Set(tentId, TrocknungZiel.RolleEntfeuchter, entfeuchter);
        _ports.Set(tentId, TrocknungZiel.RolleKuehlung, kuehlung);
        _ziel.Vergessen();
        return Ok(await PortsDtoAsync(tentId, frisch: true, ct));
    }

    private async Task<PortsDto> PortsDtoAsync(int tentId, bool frisch, CancellationToken ct)
    {
        var zuordnung = _ports.Get(tentId);
        var ports = await _ziel.PortsAsync(frisch, ct);
        return new PortsDto(
            ports is not null,
            zuordnung.GetValueOrDefault(TrocknungZiel.RolleEntfeuchter),
            zuordnung.GetValueOrDefault(TrocknungZiel.RolleKuehlung),
            (ports ?? []).Select(p => new PortAuswahlDto(p.DeviceId, p.Name)).ToList());
    }

    private static string UrteilName(TrocknungZiel.Urteil urteil) => urteil switch
    {
        TrocknungZiel.Urteil.NichtZugeordnet => "nichtZugeordnet",
        TrocknungZiel.Urteil.GeraetAus => "geraetAus",
        TrocknungZiel.Urteil.OhneAutomatik => "ohneAutomatik",
        TrocknungZiel.Urteil.AusloeserAus => "ausloeserAus",
        TrocknungZiel.Urteil.Passt => "passt",
        TrocknungZiel.Urteil.WeichtAb => "weichtAb",
        _ => "unbekannt",
    };

    private FertigStandDto FertigStand(int tentId)
    {
        if (DryingWindow.StandFor(_grows, _harvests, tentId, DateTime.Today) is not { } stand) return FertigStandDto.Leer;
        var ernte = _harvests.GetForGrow(stand.Grow.Id);
        var erntedatum = ernte?.HarvestedAt ?? DateTime.Today.AddDays(-(stand.Tag - 1));
        var ergebnis = FertigErkennung.Berechne(_waegungen.GetForGrow(stand.Grow.Id), ernte?.WetWeightG, erntedatum);
        return new FertigStandDto(
            true,
            stand.Grow.Id,
            stand.Tag,
            ergebnis.Stufe switch
            {
                FertigErkennung.Stufe.KeineWaegung => "keine",
                FertigErkennung.Stufe.ErsteWaegung => "erste",
                FertigErkennung.Stufe.Sinkt => "sinkt",
                FertigErkennung.Stufe.HaeltSich => "haeltSich",
                _ => "ziel",
            },
            ergebnis.BezugFrischgewicht,
            ergebnis.BezugFrischgewicht ? FertigErkennung.ZielProzent : null,
            ergebnis.ReferenzG,
            ernte?.WetWeightG is > 0,
            ergebnis.AenderungPunkte,
            ergebnis.NaechsteAm?.ToString("yyyy-MM-dd"),
            ergebnis.NaechsteAm is { } faellig && faellig.Date <= DateTime.Today,
            ergebnis.Punkte.Select(p => new FertigPunktDto(p.Id, p.Tag, p.Prozent, p.GewichtG, p.ZeitpunktUtc.ToLocalTime().ToString("yyyy-MM-dd"))).ToList());
    }
}

public sealed class WaegungRequest
{
    /// <summary>Gewicht in Gramm.</summary>
    public double GewichtG { get; set; }

    /// <summary>true = die ganze Charge (Bezug: Frischgewicht der Ernte); false = eine Probe (Bezug: erste Wägung).</summary>
    public bool GanzeCharge { get; set; }

    /// <summary>Optional: der Tag der Wägung (yyyy-MM-dd), wenn sie nicht heute war. Nicht in der Zukunft, nicht vor der Ernte.</summary>
    public string? Datum { get; set; }
}

public sealed record FertigPunktDto(int Id, int Tag, double Prozent, double GewichtG, string Datum);

public sealed record FertigStandDto(
    bool Trocknet,
    int? GrowId,
    int? Tag,
    string? Stufe,
    bool BezugFrischgewicht,
    double? ZielProzent,
    double? ReferenzG,
    bool ChargeWiegbar,
    double? AenderungPunkte,
    string? NaechsteWaegung,
    bool WaegungFaellig,
    IReadOnlyList<FertigPunktDto> Punkte)
{
    public static FertigStandDto Leer { get; } = new(false, null, null, null, false, null, null, false, null, null, false, []);
}

public sealed class PortsRequest
{
    /// <summary>Geräte-Id des Ports, der den Entfeuchter steuert (leer = lösen).</summary>
    public string? Entfeuchter { get; set; }

    /// <summary>Geräte-Id des Ports der Kühlung (leer = lösen).</summary>
    public string? Kuehlung { get; set; }
}

public sealed record PortAuswahlDto(string DeviceId, string Name);

public sealed record PortsDto(bool Erreichbar, string? Entfeuchter, string? Kuehlung, IReadOnlyList<PortAuswahlDto> Verfuegbar);

public sealed record ZielGeraetDto(string Rolle, string Urteil, string? PortName, string? Modus, double? WertAmGeraet, string? Einheit, double? PlanWert);

public sealed record ZielDto(
    bool Trocknet,
    int? Tag,
    string? Etappe,
    double? TempZiel,
    double? FeuchteZiel,
    double? TempOben,
    double? FeuchteOben,
    bool PortsErreichbar,
    IReadOnlyList<ZielGeraetDto> Geraete)
{
    public static ZielDto Leer { get; } = new(false, null, null, null, null, null, null, false, []);
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
