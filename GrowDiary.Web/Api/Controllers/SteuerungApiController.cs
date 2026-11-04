using System.Globalization;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>
/// Fork AI (forkai.20): Die Seite „Steuerung" — Übersicht aller Regelungen des
/// Zelts und das CO₂-Modul mit Sollwerten, Livebild und Tageswerten.
/// </summary>
/// <remarks>
/// <para><b>Ein Rahmen, viele Module.</b> Die Übersicht liefert je Modul eine
/// Kennung, einen Titel, eine Kurzzeile und einen Wert. Heute ist nur CO₂ ein
/// vollständiges Modul mit eigener Detailseite; Entfeuchter, Chiller, Abluft
/// und Licht stehen als Anzeige-Zeilen da, damit die Übersicht das ganze Zelt
/// zeigt und ein späteres Modul nur noch seine Detailseite mitbringen muss.</para>
/// <para>Fork AI (A-003, 03.10.2026): Über einen Schlüssel ist fast alles hier
/// Verwaltung — Speichern schreibt Helfer-Werte nach Home Assistant und schaltet
/// Automationen, dazu Helfer, Rechenwerte, Automationen, Absicherung und die
/// Geräte-Zuordnung. „Geräte schalten" sind der Licht-Befehl und die
/// Probeschaltung; das Licht-Speichern braucht beides (siehe dort).</para>
/// </remarks>
[ApiController]
[KiStufe(KiStufe.Verwaltung)]
[Route("api/steuerung")]
[Produces("application/json")]
public sealed class SteuerungApiController : ApiControllerBase
{
    private readonly Co2SteuerungService _co2;
    private readonly LichtSteuerungService _licht;
    private readonly LichtWaechterService _waechter;
    private readonly LichtPlanService _lichtPlan;
    private readonly ZuluftSteuerungService _zuluft;
    private readonly ChillerSteuerungService _chiller;
    private readonly EntfeuchterSteuerungService _entfeuchter;
    private readonly EntfeuchterZusatzSteuerungService _zusatz;
    private readonly GrowRepository _grows;
    private readonly HomeAssistantService _ha;
    private readonly HomeAssistantSettingsRepository _haSettings;
    private readonly KostenRepository _kosten;
    private readonly SteuerungGeraeteService _geraete;
    private readonly SteuerungBestandService _bestand;
    private readonly SteuerungHelferService _helfer;
    private readonly SteuerungRechenwertService _rechenwerte;
    private readonly SteuerungAutomationService _automationen;
    private readonly SteuerungAbsicherungService _absicherung;
    private readonly SteuerungProbeService _probe;

    public SteuerungApiController(Co2SteuerungService co2, LichtSteuerungService licht, LichtWaechterService waechter, LichtPlanService lichtPlan, ZuluftSteuerungService zuluft, ChillerSteuerungService chiller, EntfeuchterSteuerungService entfeuchter, EntfeuchterZusatzSteuerungService zusatz, GrowRepository grows, HomeAssistantService ha, HomeAssistantSettingsRepository haSettings, KostenRepository kosten, SteuerungGeraeteService geraete, SteuerungBestandService bestand, SteuerungHelferService helfer, SteuerungRechenwertService rechenwerte, SteuerungAutomationService automationen, SteuerungAbsicherungService absicherung, SteuerungProbeService probe)
    {
        _co2 = co2;
        _licht = licht;
        _waechter = waechter;
        _lichtPlan = lichtPlan;
        _zuluft = zuluft;
        _chiller = chiller;
        _entfeuchter = entfeuchter;
        _zusatz = zusatz;
        _grows = grows;
        _ha = ha;
        _haSettings = haSettings;
        _kosten = kosten;
        _geraete = geraete;
        _bestand = bestand;
        _helfer = helfer;
        _rechenwerte = rechenwerte;
        _automationen = automationen;
        _absicherung = absicherung;
        _probe = probe;
    }

    // ------------------------------------------------------------ Übersicht

    [HttpGet]
    [ProducesResponseType(typeof(SteuerungUebersichtDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungUebersichtDto>> Uebersicht([FromServices] SteuerungAuswahlService auswahl, [FromServices] TrocknungSteuerungService trocknung, [FromServices] SteuerungZeltService zelteDienst, CancellationToken ct)
    {
        var live = await _co2.LiveAsync(ct);
        var licht = await _licht.LiveAsync(ct);
        var zuluft = await _zuluft.LiveAsync(ct);
        var chiller = await _chiller.LiveAsync(ct);
        var entfeuchter = await _entfeuchter.LiveAsync(ct);
        var zusatzLive = await _zusatz.LiveAsync(ct, mitEnergie: false);
        var zusatzStand = await _zusatz.EinstellungenAsync(ct);
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var entities = await _ha.GetEntitiesAsync(settings, ct);
        var nachId = entities.ToDictionary(x => x.EntityId, x => x, StringComparer.OrdinalIgnoreCase);
        var de = CultureInfo.GetCultureInfo("de-DE");

        string? Text(string id) => nachId.TryGetValue(id, out var s) ? s.State : null;
        double? Zahl(string id) => Zahlenlesen.Maschine(Text(id));
        string F(double? v, string einheit, string format = "0") => v is { } x ? x.ToString(format, de) + einheit : "–";

        // CO₂ und Abluft gibt es je Zelt mit eigener CO₂-Steuerung; zelt == null ist das Bestand-Zelt (alte Kennungen).
        SteuerungModulDto Co2Zeile(Co2Live l, int? zeltId) => new(
            Kennung: "co2",
            Titel: "CO₂-Begasung",
            Status: l.AutomatikAn == false ? "aus" : l.KlimaOk == false ? "warn" : "an",
            Kurz: l.ZielQuelle == "plan" && l.PlanPpm is { } p
                ? $"Plan {p} ppm · Ziel {l.ZielPpm?.ToString(de) ?? "–"} · {(l.AutomatikAn == true ? "Automatik an" : "Automatik aus")}"
                : $"Fest · Ziel {l.ZielPpm?.ToString(de) ?? "–"} · {(l.AutomatikAn == true ? "Automatik an" : "Automatik aus")}",
            Wert: F(l.Co2Ppm, " ppm"),
            Unterzeile: $"Ventil {(l.VentilOffen == true ? "offen" : "zu")} · Lüfter Stufe {l.T6Stufe?.ToString(de) ?? "–"}",
            HatDetail: true,
            ZeltId: zeltId ?? Co2Zelte.Aktuell.BestandZeltId ?? zelteDienst.StandardZelt());
        SteuerungModulDto AbluftZeile(Co2Live l, int? zeltId) => new(
            Kennung: "abluft",
            Titel: "Abluft",
            Status: l.KlimaOk == false ? "warn" : "an",
            Kurz: $"Klima hat Vorrang · Stufen {Zahl(Co2Modul.Id(Co2SteuerungService.Entitaeten.T6Normal, zeltId))?.ToString("0", de) ?? "–"} / {Zahl(Co2Modul.Id(Co2SteuerungService.Entitaeten.T6Dosierung, zeltId))?.ToString("0", de) ?? "–"} / {Zahl(Co2Modul.Id(Co2SteuerungService.Entitaeten.T6Tief, zeltId))?.ToString("0", de) ?? "–"}",
            Wert: $"Stufe {l.T6Stufe?.ToString(de) ?? "–"}",
            Unterzeile: l.KlimaOk == true ? "gedrosselt" : "Klima gesperrt",
            HatDetail: false,
            ZeltId: zeltId ?? Co2Zelte.Aktuell.BestandZeltId ?? zelteDienst.StandardZelt());

        SteuerungModulDto ChillerZeile(ChillerLive c, string kennung, string titel, int? zeltId) =>
            new(
                        Kennung: kennung,
                        Titel: titel,
                        // Der Zustand kommt vom SCHALTER, nicht vom Kuehlbedarf: die
                        // Zeile meldete „kuehlt", waehrend die Steckdose an einer
                        // Schaltsperre haengen blieb — der Bedarf ist der Wunsch, nicht
                        // die Tat.
                        // Fork AI (Chiller-Ansteuerung): ein Sollwert-Gerät regelt
                        // selbst — dann zählt, ob es eingeschaltet ist, nicht die Steckdose.
                        Status: c.AutomatikAn == false ? "aus"
                            : c.Ansteuerung is ChillerAnsteuerung.Regelbar or ChillerAnsteuerung.Beides
                                ? (c.KuehlerZustand is null or "off" or "unavailable" or "unknown" ? "aus" : "an")
                                : c.SteckdoseAn == true ? "an" : "aus",
                        Kurz: $"Tag {F(c.ZielTagC, " °C", "0.0")} · Nacht {F(c.ZielNachtC, " °C", "0.0")} · {(c.AutomatikAn == true ? "Automatik an" : "Automatik aus")}",
                        Wert: F(c.WasserC, " °C", "0.0"),
                        Unterzeile: c.Ansteuerung is ChillerAnsteuerung.Regelbar or ChillerAnsteuerung.Beides
                            ? $"regelt selbst · Soll {F(c.KuehlerSollC, " °C", "0.0")}"
                            : c.SteckdoseAn == true
                                ? "kühlt"
                                : c.Kuehlbedarf == true ? "wartet auf Schaltsperre" : "bereit",
                        HatDetail: true,
                        ZeltId: zeltId);

        var module = new List<SteuerungModulDto>
        {
            Co2Zeile(live, null),
            new(
                Kennung: "entfeuchter",
                Titel: "Entfeuchter",
                Status: entfeuchter.AutomatikAn == false ? "aus" : entfeuchter.PortAn == true ? "an" : "aus",
                Kurz: $"{(Text(EntfeuchterSteuerungService.Entitaeten.VpdRegelung) == "on" ? "VPD-Modus" : "Luftfeuchte")} · ein ab {F(entfeuchter.EinAktivProzent, " %", "0.0")} · aus unter {F(entfeuchter.AusAktivProzent, " %", "0.0")}",
                Wert: F(entfeuchter.FeuchteProzent, " %", "0.0"),
                Unterzeile: $"{(entfeuchter.PortAn == true ? "entfeuchtet" : "bereit")} · VPD {F(entfeuchter.Vpd, "", "0.00")}",
                HatDetail: true),
            // Fork AI (A-009): Der Zusatz-Entfeuchter (Shelly) neben dem Entfeuchter.
            // Der Titel ist der Anzeigename — kein „Port 7" fest im Text.
            new(
                Kennung: "entfeuchter-zusatz",
                Titel: zusatzLive.ZusatzName,
                Status: ZusatzStatus(zusatzLive, zusatzStand),
                Kurz: $"{ZusatzHilfeText(zusatzStand)} · {(zusatzLive.AutomatikAn == true && EntfeuchterZusatzSteuerungService.AutomatikWirksam(zusatzStand) ? "Automatik an" : "Automatik aus")} · folgt {zusatzLive.FuehrungName}",
                Wert: F(zusatzLive.LeistungW, " W"),
                Unterzeile: zusatzLive.ZiehtNichts == true ? "zieht nichts"
                    : zusatzLive.ZusatzAn == true ? "entfeuchtet"
                    : zusatzLive.ZusatzAn == false ? "bereit" : "Zustand unbekannt",
                HatDetail: true),
            ChillerZeile(chiller, "chiller", "Wasserkühler", null),
            // Fork AI (forkai.136): Die Zeile „Crop Steering" ist entfallen —
            // stillgelegt, siehe ForkAiSchalter. Das Ziel führt der Plan.
            AbluftZeile(live, null),
            ZuluftKachel.Modul(zuluft, F(Zahl(ZuluftSteuerungService.Entitaeten.MindestDifferenz), " g/m³", "0.0")),
            new(
                Kennung: "licht",
                Titel: "Licht",
                Status: licht.Fehlgeschlagen.Count > 0 ? "warn" : licht.LichtAn == true ? "an" : "aus",
                Kurz: LichtKurz(licht),
                Wert: licht.LichtAn == true ? "an" : "aus",
                Unterzeile: licht.NaechsterWechsel ?? (licht.LichtAn == true ? "Lichtphase" : "Dunkelphase"),
                HatDetail: true),
        };

        // Steuerungen je Zelt: Jedes weitere Zelt mit eigener CO₂-Steuerung bekommt eigene Zeilen, hinter denen des Bestand-Zeltes;
        // alle anderen Zeilen tragen das Zelt, dem der Anwender sie zugewiesen hat.
        foreach (var zeltId in Co2Zelte.Aktuell.ZeltIds.Where(z => z != Co2Zelte.Aktuell.BestandZeltId))
        {
            var zeltLive = await _co2.Fuer(zeltId).LiveAsync(ct);
            module.Insert(module.FindLastIndex(m => m.Kennung == "co2") + 1, Co2Zeile(zeltLive, zeltId));
            module.Insert(module.FindLastIndex(m => m.Kennung == "abluft") + 1, AbluftZeile(zeltLive, zeltId));
        }
        // Weitere Wasserkühler: je Instanz eine Zeile hinter dem ersten Kühler, mit dem Namen aus dem Register.
        foreach (var instanz in SteuerungInstanzen.Aktuell.Where(i => i.Art == "chiller"))
        {
            var instanzLive = await _chiller.Fuer(instanz.Modul).LiveAsync(ct);
            module.Insert(module.FindLastIndex(m => m.Kennung.StartsWith("chiller", StringComparison.Ordinal)) + 1,
                ChillerZeile(instanzLive, instanz.Modul, instanz.Name, null));
        }
        var zuordnung = zelteDienst.Zuordnung();
        for (var i = 0; i < module.Count; i++)
        {
            if (module[i].ZeltId is null && zuordnung.TryGetValue(module[i].Kennung, out var zugewiesen)) module[i] = module[i] with { ZeltId = zugewiesen };
        }

        // Fork AI (A-016): Nur die Steuerungen, die der Nutzer hat. Der Rest steht als „nicht eingerichtet" daneben.
        var gewaehlt = auswahl.Gewaehlt();
        var sichtbar = module.Where(m => SteuerungAuswahlService.Sichtbar(m.Kennung, gewaehlt)).ToList();
        // A-029 (Teil B): „Trocknung“ gehört dem Trockenzelt, nicht der Auswahl der RDWC-Steuerungen — sie erscheint, sobald ein Zelt
        // mit dem Zweck „Trocknung“ existiert.
        if (_grows.GetTents().FirstOrDefault(t => t.TentType == TentType.Drying) is { } trockenZelt)
            sichtbar.Add(await TrocknungModulAsync(trockenZelt, trocknung, ct));
        var nichtEingerichtet = SteuerungAuswahlService.Alle
            .Where(a => !gewaehlt.Contains(a.Kennung))
            .Select(a => new SteuerungKarteDto(a.Kennung, a.Titel, a.Beschreibung))
            .ToList();

        return Ok(new SteuerungUebersichtDto(live.HaErreichbar, sichtbar, nichtEingerichtet, DateTime.UtcNow,
            zelteDienst.AktiveZelte().Select(t => new SteuerungZeltKurzDto(t.Id, t.Name)).ToList()));
    }

    private static async Task<SteuerungModulDto> TrocknungModulAsync(Tent zelt, TrocknungSteuerungService trocknung, CancellationToken ct)
    {
        var stand = await trocknung.StandAsync(zelt.Id, ct);
        var betrieb = stand.Betrieb == TrocknungSteuerungRepository.BetriebRegeln ? "Regeln" : "Nur melden";
        // „warn“, wenn ein Gerät dem Plan folgen soll, der Port aber nicht lesbar ist.
        var warn = stand.Trocknet && stand.Geraete.Any(g => g.Folgt && g.Urteil is "unbekannt" or "nichtZugeordnet");
        return new SteuerungModulDto(
            Kennung: "trocknung",
            Titel: "Trocknung",
            Status: !stand.Trocknet ? "aus" : warn ? "warn" : "an",
            Kurz: stand.Trocknet ? $"{zelt.Name} · Tag {stand.Tag} · {betrieb}" : $"{zelt.Name} · {betrieb}",
            Wert: stand.Trocknet ? $"Tag {stand.Tag} von {stand.TageGeplant}" : "–",
            Unterzeile: stand.Trocknet ? stand.Etappe ?? "ohne Etappe" : "trocknet nichts",
            HatDetail: true,
            ZeltId: zelt.Id);
    }

    // -------------------------------------------------------------- Auswahl

    /// <summary>
    /// Fork AI (A-016): Welche Steuerungen der Nutzer hat — und wie weit jede eingerichtet ist.
    /// </summary>
    [HttpGet("auswahl")]
    [ProducesResponseType(typeof(SteuerungAuswahlDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungAuswahlDto> Auswahl([FromServices] SteuerungAuswahlService auswahl)
        => Ok(AuswahlDto(auswahl));

    /// <summary>
    /// Die Auswahl setzen. Blendet ab- und einblendet nur — gelöscht und geschaltet wird nichts,
    /// Automationen in Home Assistant laufen weiter.
    /// </summary>
    [HttpPut("auswahl")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(SteuerungAuswahlDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungAuswahlDto> AuswahlSpeichern([FromServices] SteuerungAuswahlService auswahl, [FromBody] SteuerungAuswahlAenderung request)
    {
        if (request?.Gewaehlt is null) return BadRequestError("steuerung_auswahl_invalid", "Es wurde keine Auswahl übergeben.");
        var unbekannt = auswahl.Speichern(request.Gewaehlt);
        if (unbekannt.Count > 0)
        {
            return BadRequestError("steuerung_auswahl_unbekannt", $"Diese Steuerung gibt es nicht: {string.Join(", ", unbekannt)}.");
        }
        return Ok(AuswahlDto(auswahl));
    }

    // ------------------------------------------------------------ Zuweisung

    /// <summary>Zu welchem Zelt gehört jede Steuerung — und welche Zelte es gibt.</summary>
    [HttpGet("zuweisung")]
    [ProducesResponseType(typeof(SteuerungZuweisungDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungZuweisungDto> Zuweisung([FromServices] SteuerungZeltService zelteDienst, [FromServices] SteuerungAuswahlService auswahl)
        => Ok(ZuweisungDto(zelteDienst, auswahl));

    /// <summary>
    /// Steuerungen Zelten zuweisen. Ändert die Gliederung der Übersicht, sonst nichts: Geräte, Helfer und Automationen bleiben,
    /// wie sie sind.
    /// </summary>
    [HttpPut("zuweisung")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(SteuerungZuweisungDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungZuweisungDto> ZuweisungSpeichern(
        [FromServices] SteuerungZeltService zelteDienst, [FromServices] SteuerungAuswahlService auswahl, [FromBody] SteuerungZuweisungAenderung request)
    {
        if (request?.Zuordnungen is null) return BadRequestError("steuerung_zuweisung_invalid", "Es wurde nichts übergeben.");
        var fehler = zelteDienst.Speichern(request.Zuordnungen);
        if (fehler is not null) return BadRequestError("steuerung_zuweisung_ungueltig", fehler);
        return Ok(ZuweisungDto(zelteDienst, auswahl));
    }

    private static SteuerungZuweisungDto ZuweisungDto(SteuerungZeltService zelteDienst, SteuerungAuswahlService auswahl)
    {
        var gewaehlt = auswahl.Gewaehlt();
        var jetzt = zelteDienst.Zuordnung();
        var eintraege = SteuerungZeltService.Zuweisbar
            .Select(k => (Kennung: k, Art: SteuerungAuswahlService.Alle.FirstOrDefault(a => a.Uebersicht.Contains(InstanzModul.Art(k) ?? k, StringComparer.Ordinal))))
            // Der Zusatz-Entfeuchter folgt dem Entfeuchter: eine Zeile für beide.
            .Where(x => x.Art is not null && gewaehlt.Contains(x.Art.Kennung) && x.Kennung != "entfeuchter-zusatz")
            .Select(x => new SteuerungZuweisungEintragDto(x.Kennung, InstanzTitel(x.Kennung), jetzt.GetValueOrDefault(x.Kennung)))
            .ToList();
        return new SteuerungZuweisungDto(zelteDienst.AktiveZelte().Select(t => new SteuerungZeltKurzDto(t.Id, t.Name)).ToList(), eintraege);
    }

    private static SteuerungAuswahlDto AuswahlDto(SteuerungAuswahlService auswahl)
    {
        var gewaehlt = auswahl.Gewaehlt();
        var eintraege = SteuerungAuswahlService.Alle.Select(a =>
        {
            var (zugeordnet, gesamt) = auswahl.Pflichtrollen(a);
            return new SteuerungAuswahlEintragDto(a.Kennung, a.Titel, a.Beschreibung, gewaehlt.Contains(a.Kennung), zugeordnet, gesamt, SteuerungAuswahlService.Bausteinmodule(a), SteuerungAuswahlService.ModuleVon(a), auswahl.FehlendePflichtrollen(a));
        }).ToList();
        return new SteuerungAuswahlDto(auswahl.Gespeichert(), eintraege);
    }

    // ------------------------------------------------------------------ CO₂

    /// <summary>
    /// Der CO₂-Dienst eines Zeltes: ohne Angabe der des Bestand-Zeltes (wie bisher), sonst der des gewählten Zeltes — das muss ein Zelt mit
    /// CO₂-Steuerung sein.
    /// </summary>
    private Co2SteuerungService? Co2Dienst(int? zeltId)
    {
        if (zeltId is not { } z) return _co2.Bestand();
        var aktuell = Co2Zelte.Aktuell;
        return aktuell.BestandZeltId == z || aktuell.ZeltIds.Contains(z) ? _co2.Fuer(z) : null;
    }

    [HttpGet("co2")]
    [ProducesResponseType(typeof(Co2SeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<Co2SeiteDto>> Co2([FromQuery] int? zeltId, CancellationToken ct)
    {
        var co2 = Co2Dienst(zeltId);
        if (co2 is null) return NotFoundError("co2_zelt_unbekannt", $"Zelt {zeltId} hat keine CO₂-Steuerung.");

        var live = await co2.LiveAsync(ct);
        var tage = co2.LetzteTage(14);
        var artikel = _kosten.GetArtikel(nurAktive: true)
            .Select(a => new KostenArtikelKurzDto(a.Id, a.Name, a.Einheit))
            .ToList();
        var (grow, stage) = co2.LaufenderGrow();
        var geraete = _geraete.EntitiesFuerModul(co2.ModulName);
        var einstellungen = co2.Einstellungen;
        return Ok(new Co2SeiteDto(
            Co2SteuerungService.MitWertenAusHomeAssistant(einstellungen, live),
            live,
            tage.Select(ToDto).ToList(),
            artikel,
            grow?.Name,
            stage is { } st ? Phasenname.Fuer(st) : null,
            co2.PlanZielPpm(),
            co2.PlanHerkunft())
        {
            GeraeteZugeordnet = geraete.Values.Count(entity => !string.IsNullOrWhiteSpace(entity)),
            GeraeteGesamt = geraete.Count,
            ZeltvolumenQuelle = co2.WirksamesZeltvolumen(einstellungen.ZeltvolumenM3).Quelle,
            ZeltId = co2.ZeltId ?? Co2Zelte.Aktuell.BestandZeltId,
            Zelte = Co2ZeltListe(),
        });
    }

    /// <summary>Die Zelte mit CO₂-Steuerung, das Bestand-Zelt zuerst — für den Zeltwechsel der Seite.</summary>
    private IReadOnlyList<Co2ZeltKurzDto> Co2ZeltListe()
    {
        var namen = _grows.GetTents(includeArchived: true).ToDictionary(t => t.Id, t => t.Name);
        var aktuell = Co2Zelte.Aktuell;
        return aktuell.ZeltIds
            .OrderBy(i => i == aktuell.BestandZeltId ? 0 : 1).ThenBy(i => i)
            .Select(i => new Co2ZeltKurzDto(i, namen.GetValueOrDefault(i, $"Zelt {i}"), Co2Zelte.ModulFuer(i)))
            .ToList();
    }

    [HttpPut("co2")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(Co2SeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<Co2SeiteDto>> Co2Speichern([FromBody] Co2Einstellungen request, [FromQuery] int? zeltId, CancellationToken ct)
    {
        if (request is null) return BadRequestError("co2_invalid", "Es wurde nichts übergeben.");
        var co2 = Co2Dienst(zeltId);
        if (co2 is null) return NotFoundError("co2_zelt_unbekannt", $"Zelt {zeltId} hat keine CO₂-Steuerung.");

        var (gespeichert, fehler, erreicht) = await co2.SpeichernAsync(request, ct);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        var seite = await Co2(zeltId, ct);
        if (seite.Result is OkObjectResult ok && ok.Value is Co2SeiteDto dto)
        {
            return Ok(dto with { HaAngenommen = erreicht });
        }
        return seite;
    }

    // --------------------------------------------------------------- Zuluft

    /// <summary>
    /// Fork AI (forkai.76): Die Zuluft-Seite — Sollwerte und das Livebild der
    /// Kellerzuluft. Geregelt wird weiter in Home Assistant.
    /// </summary>
    [HttpGet("zuluft")]
    [ProducesResponseType(typeof(ZuluftSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZuluftSeiteDto>> Zuluft(CancellationToken ct)
    {
        var geraete = _geraete.EntitiesFuerModul(ZuluftSteuerungService.Modul);
        return Ok(new ZuluftSeiteDto(await _zuluft.EinstellungenAsync(ct), await _zuluft.LiveAsync(ct))
        {
            // Solange nichts gespeichert ist, stehen die Werte der vorhandenen
            // Helfer in der Seite. Die Seite sagt das, damit niemand sie für
            // Werkseinstellungen haelt und blind speichert.
            AusHomeAssistantUebernommen = _zuluft.Gespeichert is null,
            GeraeteZugeordnet = geraete.Count(g => g.Value is not null),
            // Zähler und Nenner zählen dieselben Rollen: EntitiesFuerModul liefert die eigenen UND die
            // mitbenutzten (Raum-Fühler), also muss der Nenner sie auch zählen (sonst „10 / 8“).
            GeraeteGesamt = SteuerungGeraeteRollen.FuerModulMitMitbenutzten(ZuluftSteuerungService.Modul).Count,
        });
    }

    [HttpPut("zuluft")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(ZuluftSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ZuluftSeiteDto>> ZuluftSpeichern([FromBody] ZuluftEinstellungen request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("zuluft_invalid", "Es wurde nichts übergeben.");
        var (gespeichert, fehler, erreicht) = await _zuluft.SpeichernAsync(request, ct);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }

        var seite = await Zuluft(ct);
        if (seite.Result is OkObjectResult ok && ok.Value is ZuluftSeiteDto dto) return Ok(dto with { HaAngenommen = erreicht });
        return seite;
    }

    // ---------------------------------------------------------- Entfeuchter

    /// <summary>
    /// Fork AI (forkai.129, F-023): Die Entfeuchter-Seite — Geräteverhalten und
    /// Livebild. Pflanzenziele werden nur gelesen; geregelt wird in Home Assistant.
    /// </summary>
    [HttpGet("entfeuchter")]
    [ProducesResponseType(typeof(EntfeuchterSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterSeiteDto>> Entfeuchter([FromQuery] string? instanz, CancellationToken ct)
    {
        var dienst = EntfeuchterDienst(instanz);
        if (dienst is null) return NotFoundError("steuerung_instanz_unbekannt", "Diese Instanz gibt es nicht.");
        var geraete = _geraete.EntitiesFuerModul(dienst.ModulName);
        var einstellungen = await dienst.EinstellungenAsync(ct);
        return Ok(new EntfeuchterSeiteDto(einstellungen, await dienst.LiveAsync(ct))
        {
            Abweichungen = await dienst.AbweichungenAsync(einstellungen, ct),
            AusHomeAssistantUebernommen = dienst.Gespeichert is null,
            GeraeteZugeordnet = geraete.Count(g => g.Value is not null),
            GeraeteGesamt = SteuerungGeraeteRollen.FuerModulMitMitbenutzten(dienst.ModulName).Count,
        });
    }

    /// <summary>Der Entfeuchter-Dienst: ohne Angabe der erste, sonst der einer angelegten Instanz — eine unbekannte gibt <c>null</c>.</summary>
    private EntfeuchterSteuerungService? EntfeuchterDienst(string? instanz)
    {
        if (string.IsNullOrWhiteSpace(instanz) || string.Equals(instanz, EntfeuchterSteuerungService.Modul, StringComparison.Ordinal)) return _entfeuchter.Bestand();
        return SteuerungInstanzen.Module.Contains(instanz, StringComparer.Ordinal) && InstanzModul.Art(instanz) == EntfeuchterSteuerungService.Modul
            ? _entfeuchter.Fuer(instanz)
            : null;
    }

    [HttpPut("entfeuchter")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(EntfeuchterSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterSeiteDto>> EntfeuchterSpeichern([FromBody] EntfeuchterEinstellungen request, [FromQuery] string? instanz, CancellationToken ct)
    {
        if (request is null) return BadRequestError("entfeuchter_invalid", "Es wurde nichts übergeben.");
        var dienst = EntfeuchterDienst(instanz);
        if (dienst is null) return NotFoundError("steuerung_instanz_unbekannt", "Diese Instanz gibt es nicht.");
        var (gespeichert, fehler, erreicht) = await dienst.SpeichernAsync(request, ct);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }

        var seite = await Entfeuchter(instanz, ct);
        if (seite.Result is OkObjectResult ok && ok.Value is EntfeuchterSeiteDto dto) return Ok(dto with { HaAngenommen = erreicht });
        return seite;
    }

    // ------------------------------------------------- Zusatz-Entfeuchter

    /// <summary>
    /// Fork AI (A-009): Die Seite „Zusatz-Entfeuchter" — Einstellungen und Livebild
    /// des zweiten Entfeuchters (Shelly). Geregelt wird in Home Assistant.
    /// </summary>
    [HttpGet("entfeuchter-zusatz")]
    [ProducesResponseType(typeof(EntfeuchterZusatzSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterZusatzSeiteDto>> EntfeuchterZusatz(CancellationToken ct)
    {
        // Vor dem Lesen merken: das Lesen übernimmt die Werte aus den Helfern und
        // speichert sie — danach wäre „übernommen" nie mehr wahr.
        var ausHomeAssistant = _zusatz.Gespeichert is null;
        var geraete = _geraete.EntitiesFuerModul(EntfeuchterZusatzSteuerungService.Modul);
        return Ok(new EntfeuchterZusatzSeiteDto(await _zusatz.EinstellungenAsync(ct), await _zusatz.LiveAsync(ct))
        {
            AusHomeAssistantUebernommen = ausHomeAssistant,
            GeraeteZugeordnet = geraete.Count(g => g.Value is not null),
            GeraeteGesamt = SteuerungGeraeteRollen.FuerModulMitMitbenutzten(EntfeuchterZusatzSteuerungService.Modul).Count,
        });
    }

    /// <summary>
    /// Speichert NUR die Felder, die im Body vorkommen. Alles andere — auch die
    /// gemeinsame Höchsttemperatur des Entfeuchters — bleibt unangetastet.
    /// </summary>
    [HttpPut("entfeuchter-zusatz")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(EntfeuchterZusatzSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterZusatzSeiteDto>> EntfeuchterZusatzSpeichern([FromBody] EntfeuchterZusatzAenderung request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("entfeuchter_zusatz_invalid", "Es wurde nichts übergeben.");
        var ergebnis = await _zusatz.SpeichernAsync(request, ct);
        if (ergebnis.Gespeichert is null)
        {
            foreach (var (feld, meldung) in ergebnis.Fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }

        var seite = await EntfeuchterZusatz(ct);
        if (seite.Result is OkObjectResult ok && ok.Value is EntfeuchterZusatzSeiteDto dto)
        {
            // haAngenommen: aus den echten Schreibergebnissen — null, wenn nichts zu schreiben war.
            return Ok(dto with { HaAngenommen = ergebnis.HaErreicht, AusHomeAssistantUebernommen = false, Hinweise = ergebnis.Hinweise });
        }
        return seite;
    }

    /// <summary>Die Anzeigenamen der beiden Entfeuchter-Geräte; Vorgabe ist der Gerätename aus Home Assistant.</summary>
    [HttpGet("entfeuchter-namen")]
    [ProducesResponseType(typeof(EntfeuchterNamenDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterNamenDto>> EntfeuchterNamen(CancellationToken ct)
        => Ok(await _zusatz.NamenAsync(ct));

    /// <summary>Nur die genannten Namen ändern; leer oder null setzt auf die Vorgabe, ein fehlendes Feld lässt den Namen.</summary>
    [HttpPut("entfeuchter-namen")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(EntfeuchterNamenDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<EntfeuchterNamenDto>> EntfeuchterNamenSpeichern([FromBody] EntfeuchterNamenAenderung request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("entfeuchter_namen_invalid", "Es wurde nichts übergeben.");
        var fehler = _zusatz.NamenSpeichern(request);
        if (fehler.Count > 0)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        return Ok(await _zusatz.NamenAsync(ct));
    }

    /// <summary>Fork AI (forkai.241): Der Raum — Name, Vorgabe und die Zelte darin. Die Fühler stehen bei den Geräte-Rollen (Modul <c>raum</c>).</summary>
    [HttpGet("raum")]
    [ProducesResponseType(typeof(RaumDto), StatusCodes.Status200OK)]
    public ActionResult<RaumDto> Raum([FromServices] RaumService raum) => Ok(raum.Stand());

    /// <summary>Den Raumnamen ändern; leer setzt auf die Vorgabe „Aufstellraum“.</summary>
    [HttpPut("raum")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(RaumDto), StatusCodes.Status200OK)]
    public ActionResult<RaumDto> RaumSpeichern([FromBody] RaumAenderung request, [FromServices] RaumService raum)
    {
        if (request is null) return BadRequestError("raum_invalid", "Es wurde nichts übergeben.");
        var fehler = raum.NameSpeichern(request.Name);
        if (fehler.Count > 0)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        return Ok(raum.Stand());
    }

    /// <summary>Fork AI (A-015): Wie viele Entfeuchter es gibt — null heißt „nach der Zuordnung entscheiden".</summary>
    [HttpGet("entfeuchtung-einrichtung")]
    [ProducesResponseType(typeof(EntfeuchtungEinrichtungDto), StatusCodes.Status200OK)]
    public ActionResult<EntfeuchtungEinrichtungDto> EntfeuchtungEinrichtung() => Ok(_zusatz.Einrichtung());

    /// <summary>Sagt, ob es einen Zusatz-Entfeuchter gibt (true/false) oder ob der Fork das aus der Zuordnung schließen soll (null). Die Regelung in Home Assistant bleibt unberührt.</summary>
    [HttpPut("entfeuchtung-einrichtung")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(EntfeuchtungEinrichtungDto), StatusCodes.Status200OK)]
    public ActionResult<EntfeuchtungEinrichtungDto> EntfeuchtungEinrichtungSpeichern([FromBody] EntfeuchtungEinrichtungAenderung request)
    {
        if (request is null) return BadRequestError("entfeuchtung_einrichtung_invalid", "Es wurde nichts übergeben.");
        return Ok(_zusatz.EinrichtungSpeichern(request.ZusatzVorhanden));
    }

    /// <summary>Die Statuszeile der Übersicht: „warn" bei „zieht nichts" oder zu warmem Zelt.</summary>
    private static string ZusatzStatus(EntfeuchterZusatzLive live, EntfeuchterZusatzEinstellungen e)
    {
        var grenze = live.TagPhase switch { true => live.TempMaxTagC, false => live.TempMaxNachtC, _ => null };
        var zuWarm = live.TempC is { } t && grenze is { } g && t > g;
        if (live.ZiehtNichts == true || zuWarm) return "warn";
        if (live.AutomatikAn == false || !EntfeuchterZusatzSteuerungService.AutomatikWirksam(e)) return "aus";
        return live.ZusatzAn == true ? "an" : "aus";
    }

    private static string ZusatzHilfeText(EntfeuchterZusatzEinstellungen e) => EntfeuchterZusatzHilfe.Erkennen(e) switch
    {
        EntfeuchterZusatzHilfe.Aus => "aus",
        EntfeuchterZusatzHilfe.Sparsam => "sparsam",
        EntfeuchterZusatzHilfe.Normal => "normal",
        EntfeuchterZusatzHilfe.Kraeftig => "kräftig",
        _ => "eigene Werte",
    };

    // -------------------------------------------------------------- Chiller

    /// <summary>
    /// Fork AI: Die Kühler-Seite — Sollwerte, Schutz und das Livebild der
    /// Steckdose. Geschaltet wird weiter in Home Assistant.
    /// </summary>
    [HttpGet("chiller")]
    [ProducesResponseType(typeof(ChillerSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChillerSeiteDto>> Chiller([FromQuery] string? instanz, CancellationToken ct)
    {
        var kuehler = ChillerDienst(instanz);
        if (kuehler is null) return NotFoundError("steuerung_instanz_unbekannt", "Diese Instanz gibt es nicht.");
        var (zugeordnet, gesamt) = ChillerSteuerungService.GeraeteZaehlen(
            _geraete.EntitiesFuerModul(kuehler.ModulName), kuehler.ModulName);
        return Ok(new ChillerSeiteDto(await kuehler.EinstellungenAsync(ct), await kuehler.LiveAsync(ct))
        {
            AusHomeAssistantUebernommen = kuehler.Gespeichert is null,
            GeraeteZugeordnet = zugeordnet,
            GeraeteGesamt = gesamt,
        });
    }

    [HttpPut("chiller")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(ChillerSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChillerSeiteDto>> ChillerSpeichern([FromBody] ChillerEinstellungen request, [FromQuery] string? instanz, CancellationToken ct)
    {
        if (request is null) return BadRequestError("chiller_invalid", "Es wurde nichts übergeben.");
        var kuehler = ChillerDienst(instanz);
        if (kuehler is null) return NotFoundError("steuerung_instanz_unbekannt", "Diese Instanz gibt es nicht.");
        var (gespeichert, fehler, erreicht) = await kuehler.SpeichernAsync(request, ct);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }

        var seite = await Chiller(instanz, ct);
        if (seite.Result is OkObjectResult ok && ok.Value is ChillerSeiteDto dto) return Ok(dto with { HaAngenommen = erreicht });
        return seite;
    }

    /// <summary>Der Kühler-Dienst: ohne Angabe der erste, sonst der einer angelegten Instanz — eine unbekannte gibt <c>null</c>.</summary>
    private ChillerSteuerungService? ChillerDienst(string? instanz)
    {
        if (string.IsNullOrWhiteSpace(instanz) || string.Equals(instanz, ChillerSteuerungService.Modul, StringComparison.Ordinal)) return _chiller.Bestand();
        return SteuerungInstanzen.Module.Contains(instanz, StringComparer.Ordinal) && InstanzModul.Art(instanz) == ChillerSteuerungService.Modul
            ? _chiller.Fuer(instanz)
            : null;
    }

    // ------------------------------------------------------------ Instanzen

    /// <summary>Die weiteren Instanzen einer Steuerung mit ihrem Zelt (die erste Instanz steht nicht darin).</summary>
    [HttpGet("instanzen")]
    [ProducesResponseType(typeof(SteuerungInstanzenDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungInstanzenDto> Instanzen([FromServices] SteuerungInstanzService instanzen, [FromServices] SteuerungZeltService zelteDienst)
        => Ok(InstanzenDto(instanzen, zelteDienst));

    /// <summary>
    /// Eine weitere Instanz anlegen (heute nur der Wasserkühler). Legt nur den Eintrag im Fork an und weist das Zelt zu; die
    /// Bausteine in Home Assistant entstehen danach über die Einrichtung der Instanz.
    /// </summary>
    [HttpPost("instanzen")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(SteuerungInstanzenDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungInstanzenDto> InstanzAnlegen(
        [FromServices] SteuerungInstanzService instanzen, [FromServices] SteuerungZeltService zelteDienst, [FromBody] SteuerungInstanzAnlegen request)
    {
        if (request is null) return BadRequestError("steuerung_instanz_invalid", "Es wurde nichts übergeben.");
        // Nur Steuerungen, deren Dienst schon mehrere Instanzen kann: sonst entstünde ein Eintrag ohne Wirkung.
        if (!string.Equals(request.Art, ChillerSteuerungService.Modul, StringComparison.Ordinal))
            return BadRequestError("steuerung_instanz_art", "Diese Steuerung lässt sich noch nicht vervielfachen.");
        if (request.ZeltId is { } zelt && !zelteDienst.AktiveZelte().Any(t => t.Id == zelt))
            return BadRequestError("steuerung_instanz_zelt", "Dieses Zelt gibt es nicht.");

        var (instanz, fehler) = instanzen.Anlegen(request.Art, request.Name);
        if (instanz is null) return BadRequestError("steuerung_instanz_ungueltig", fehler!);
        if (request.ZeltId is { } z)
        {
            var zuweisung = zelteDienst.Speichern(new Dictionary<string, int> { [instanz.Modul] = z });
            if (zuweisung is not null)
            {
                // Ohne Zelt bleibt keine Instanz stehen, die in der Übersicht nirgends hingehört.
                instanzen.Entfernen(instanz.Modul);
                return BadRequestError("steuerung_instanz_zelt", zuweisung);
            }
        }

        return Ok(InstanzenDto(instanzen, zelteDienst));
    }

    /// <summary>Eine Instanz umbenennen — nur der Anzeigename im Fork, nichts in Home Assistant.</summary>
    [HttpPut("instanzen/{modul}")]
    [KiStufe(KiStufe.Verwaltung)]
    [ProducesResponseType(typeof(SteuerungInstanzenDto), StatusCodes.Status200OK)]
    public ActionResult<SteuerungInstanzenDto> InstanzUmbenennen(
        string modul, [FromServices] SteuerungInstanzService instanzen, [FromServices] SteuerungZeltService zelteDienst, [FromBody] SteuerungInstanzUmbenennen request)
    {
        if (request is null) return BadRequestError("steuerung_instanz_invalid", "Es wurde nichts übergeben.");
        var fehler = instanzen.Umbenennen(modul, request.Name);
        if (fehler is not null) return BadRequestError("steuerung_instanz_ungueltig", fehler);
        return Ok(InstanzenDto(instanzen, zelteDienst));
    }

    /// <summary>
    /// Eine Instanz aus dem Fork entfernen. Ohne <c>ausHa</c> bleiben ihre Bausteine in Home Assistant stehen; mit <c>ausHa=true</c>
    /// werden Automationen (vorher als Datei gesichert), Rechenwerte und Helfer der Instanz dort zuerst entfernt — und nur wenn das
    /// ganz gelingt, verschwindet die Instanz aus dem Fork (sonst lässt sich das Entfernen wiederholen). Die Nummer wird nie wieder
    /// vergeben. Die erste Instanz gibt es hier nicht — sie ist nicht löschbar, nur abwählbar.
    /// </summary>
    [HttpDelete("instanzen/{modul}")]
    [KiStufe(KiStufe.Verwaltung)]
    [KiSicherungVorher]
    [ProducesResponseType(typeof(SteuerungInstanzenDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungInstanzenDto>> InstanzEntfernen(
        string modul, [FromServices] SteuerungInstanzService instanzen, [FromServices] SteuerungZeltService zelteDienst,
        [FromServices] SteuerungInstanzAufraeumenService aufraeumen, CancellationToken ct, [FromQuery] bool ausHa = false)
    {
        if (!instanzen.Liste().Any(i => i.Modul == modul)) return NotFoundError("steuerung_instanz_unbekannt", "Diese Instanz gibt es nicht.");

        SteuerungInstanzAufraeumenService.Bilanz? bilanz = null;
        if (ausHa)
        {
            bilanz = await aufraeumen.EntfernenAsync(modul, _haSettings.GetEffectiveHomeAssistantSettings(), ct);
            if (!bilanz.Erreichbar)
                return ConflictError("home_assistant_stumm", "Home Assistant antwortet nicht — es wurde nichts entfernt. Die Instanz bleibt stehen.");
            if (!bilanz.Vollstaendig)
            {
                var misslungen = bilanz.Einzeln.Where(t => t.Stand == "fehler").ToList();
                var beispiele = string.Join("; ", misslungen.Take(3).Select(t => $"{t.Kennung}: {t.Hinweis}"));
                return ConflictError("steuerung_instanz_ha_teilweise",
                    $"Nicht alles ließ sich aus Home Assistant entfernen ({misslungen.Count} von {bilanz.Einzeln.Count} Bausteinen — {beispiele}{(misslungen.Count > 3 ? "; …" : "")}). Die Instanz bleibt stehen; das Entfernen lässt sich wiederholen.");
            }
        }

        var fehler = instanzen.Entfernen(modul);
        if (fehler is not null) return NotFoundError("steuerung_instanz_unbekannt", fehler);
        return Ok(InstanzenDto(instanzen, zelteDienst) with { HaEntfernt = bilanz?.Entfernt });
    }

    private static SteuerungInstanzenDto InstanzenDto(SteuerungInstanzService instanzen, SteuerungZeltService zelteDienst)
    {
        var zuordnung = zelteDienst.Zuordnung();
        return new SteuerungInstanzenDto(instanzen.Liste()
            .Select(i => new SteuerungInstanzDto(i.Modul, i.Art, i.Nummer, i.Name, zuordnung.GetValueOrDefault(i.Modul)))
            .ToList());
    }

    // ---------------------------------------------------------------- Licht

    /// <summary>
    /// Fork AI: Die Licht-Seite — Zeitpläne, Stufe und der Zustand am Controller.
    /// </summary>
    [HttpGet("licht")]
    [ProducesResponseType(typeof(LichtSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<LichtSeiteDto>> Licht(CancellationToken ct)
    {
        var geraete = _geraete.EntitiesFuerModul(LichtSteuerungService.Modul);
        return Ok(new LichtSeiteDto(_licht.Einstellungen, await _licht.LiveAsync(ct))
        {
            GeraeteZugeordnet = geraete.Values.Count(entity => !string.IsNullOrWhiteSpace(entity)),
            GeraeteGesamt = geraete.Count,
        });
    }

    // Fork AI (A-003, 03.10.2026): Beide Stufen. Das Speichern schreibt die
    // Helfer (Verwaltung) und, solange ein Preset aktiv ist, dessen Ein- und
    // Aus-Zeit sofort an den Controller (LichtSteuerungService.SpeichernAsync) —
    // eine frühere Ein-Zeit macht das Licht auf der Stelle an.
    [HttpPut("licht")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(LichtSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<LichtSeiteDto>> LichtSpeichern([FromBody] LichtEinstellungen request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("licht_invalid", "Es wurde nichts übergeben.");
        var (gespeichert, fehler, erreicht) = await _licht.SpeichernAsync(request, ct);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        var seite = await Licht(ct);
        if (seite.Result is OkObjectResult ok && ok.Value is LichtSeiteDto dto) return Ok(dto with { HaAngenommen = erreicht });
        return seite;
    }

    /// <summary>Der Licht-Wächter: Einstellungen, Plan, Lichtsensor und die jüngste Prüfung.</summary>
    [HttpGet("licht/waechter")]
    [ProducesResponseType(typeof(WaechterStand), StatusCodes.Status200OK)]
    public async Task<ActionResult<WaechterStand>> LichtWaechter(CancellationToken ct)
        => Ok(await _waechter.StandAsync(ct));

    /// <summary>Wächter an/aus, Modus (korrigieren oder nur melden) und „Hell ab“ speichern. Die Pause bleibt unberührt.</summary>
    [HttpPut("licht/waechter")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(WaechterStand), StatusCodes.Status200OK)]
    public async Task<ActionResult<WaechterStand>> LichtWaechterSpeichern([FromBody] LichtWaechterEinstellungen request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("waechter_invalid", "Es wurde nichts übergeben.");
        request.PauseBisUtc = _waechter.Einstellungen.PauseBisUtc;
        var (gespeichert, fehler) = _waechter.Speichern(request);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        return Ok(await _waechter.StandAsync(ct));
    }

    /// <summary>Der Wächter schweigt für 1, 2 oder 72 Stunden (bis zur Ernte) — oder die Pause wird mit 0 aufgehoben.</summary>
    [HttpPost("licht/waechter/pause")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(WaechterStand), StatusCodes.Status200OK)]
    public async Task<ActionResult<WaechterStand>> LichtWaechterPause([FromBody] LichtWaechterPauseRequest request, CancellationToken ct)
    {
        if (request is null || request.Stunden is not (0 or 1 or 2 or 72))
            return BadRequestError("pause_invalid", "Pause: 0 (aufheben), 1, 2 oder 72 Stunden.");
        _waechter.Pausieren(request.Stunden);
        return Ok(await _waechter.StandAsync(ct));
    }

    /// <summary>Der Lichtplan: Kalibrierung, Kurve Stufe → Lichtwert, Planwochen mit Stufenvorschlag, die Phase aus dem Grow-Plan.</summary>
    [HttpGet("licht/plan")]
    [ProducesResponseType(typeof(LichtPlanStand), StatusCodes.Status200OK)]
    public ActionResult<LichtPlanStand> LichtPlan() => Ok(_lichtPlan.Stand());

    /// <summary>Kalibrierung speichern (Herstellerangabe, Messpunkte) und die Automatik „Stufe nach Plan“ ein- oder ausschalten.</summary>
    [HttpPut("licht/kalibrierung")]
    [KiStufe(KiStufe.Verwaltung | KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(LichtPlanStand), StatusCodes.Status200OK)]
    public ActionResult<LichtPlanStand> LichtKalibrierungSpeichern([FromBody] LichtKalibrierung request)
    {
        if (request is null) return BadRequestError("kalibrierung_invalid", "Es wurde nichts übergeben.");
        var (gespeichert, fehler) = _lichtPlan.Speichern(request);
        if (gespeichert is null)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }

        return Ok(_lichtPlan.Stand());
    }

    /// <summary>Aus, An, Preset anwenden, Stufe setzen — oder eine gemeldete Fehlmeldung wegräumen.</summary>
    [HttpPost("licht/befehl")]
    [KiStufe(KiStufe.GeraeteSchalten)]
    [ProducesResponseType(typeof(LichtSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<LichtSeiteDto>> LichtBefehl([FromBody] LichtBefehlRequest request, CancellationToken ct)
    {
        var art = request?.Art?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(art)) return BadRequestError("befehl_invalid", "Es wurde kein Befehl übergeben.");

        if (art == "quittieren")
        {
            LichtSteuerungService.OffeneVergessen();
            return await Licht(ct);
        }

        if (art is not ("aus" or "an" or "preset" or "stufe"))
            return BadRequestError("befehl_unbekannt", $"Der Befehl „{art}\" ist nicht bekannt.");

        var erreicht = await _licht.BefehlAsync(art, request!.Preset, request.Stufe, ct);
        var seite = await Licht(ct);
        if (seite.Result is OkObjectResult ok && ok.Value is LichtSeiteDto dto) return Ok(dto with { HaAngenommen = erreicht });
        return seite;
    }

    /// <summary>Die Kurzzeile der Licht-Kachel auf der Steuerungs-Übersicht.</summary>
    /// <remarks>
    /// Seltene Modi stehen mit ihrem deutschen Namen da (<see cref="AcModi.Name"/>).
    /// Bis zum 02.10.2026 stand hier <c>licht.Modus</c> selbst — „Modus Auto",
    /// roh, weil die Übersetzung nur die Oberfläche kannte.
    /// </remarks>
    public static string LichtKurz(LichtLive licht)
    {
        var stufe = licht.Stufe?.ToString(CultureInfo.InvariantCulture) ?? "–";
        if (licht.Modus == AcModi.Aus) return $"Aus · Stufe {stufe}";
        if (licht.Modus == AcModi.An) return $"Dauerlicht · Stufe {stufe}";
        if (licht.Modus != AcModi.Zeitplan) return $"Modus {licht.ModusName} · Stufe {stufe}";

        var preset = licht.AktivesPreset switch
        {
            "veggie" => "Veggie",
            "bluete" => "Blüte",
            _ => "eigen",
        };
        return $"Zeitplan {preset} {licht.EinZeit ?? "–"} – {licht.AusZeit ?? "–"} · Stufe {stufe}";
    }

    // -------------------------------------------------------------- Geräte

    /// <summary>
    /// Fork AI (forkai.21): Die Geräte-Zuordnung aller Steuerungen samt Livewert,
    /// damit in der Oberfläche sichtbar ist, ob hinter einer Rolle wirklich das
    /// gemeinte Gerät hängt.
    /// </summary>
    /// <summary>
    /// Was die Steuerung in Home Assistant an eigenen Objekten braucht — und was
    /// davon fehlt.
    /// </summary>
    /// <remarks>
    /// Fork AI (forkai.45): Ohne das zeigt die Seite bei einer fremden
    /// Installation nur „nicht verfügbar", ohne den Grund zu nennen. Der
    /// Endpunkt stellt fest und erklärt; angelegt wird hier nichts.
    /// </remarks>
    /// <summary>Die fehlenden Helfer einer Steuerung in Home Assistant anlegen.</summary>
    /// <remarks>
    /// <para>Fork AI (forkai.58): Legt nur die einfachen Arten an — Zahlen,
    /// Schalter, Zeitstempel, Zähler. Rechen-Sensoren und Automationen brauchen
    /// andere Wege und kommen getrennt, weil sie getrennt schiefgehen.</para>
    /// <para>POST, obwohl nichts im Fork entsteht: Der Aufruf verändert den
    /// Zustand von Home Assistant und darf nicht durch einen Vorlauf des
    /// Browsers ausgelöst werden.</para>
    /// </remarks>
    /// <summary>Die fehlenden Rechenwerte einer Steuerung anlegen.</summary>
    /// <remarks>
    /// Fork AI (forkai.69): Getrennt von den Helfern, weil Template-Helfer über
    /// den Einrichtungsdialog entstehen und damit anders scheitern. Zuerst die
    /// Helfer anlegen — die Rechenwerte lesen sie.
    /// </remarks>
    /// <summary>Die Automationen einer Steuerung anlegen.</summary>
    /// <remarks>
    /// <para>Fork AI (forkai.72): Getrennt von Helfern und Rechenwerten und mit
    /// eigener Zustimmung, weil am Ende dieser Automationen ein Ventil an einer
    /// Gasflasche hängt.</para>
    /// <para><c>vorschau=true</c> schreibt nichts und sagt nur, was geschähe —
    /// das ist der Stand, den der Knopf vor der Bestätigung zeigt.</para>
    /// </remarks>
    /// <summary>Das Dosier-Ventil zwei Sekunden öffnen und nachsehen.</summary>
    /// <remarks>
    /// Fork AI (forkai.73): In dieser Anlage hat es Tage gedauert
    /// herauszufinden, dass der Port „an" meldete und trotzdem kein Gas kam.
    /// Zwei Sekunden beim Einrichten ersparen dem Nächsten diese Suche.
    /// </remarks>
    [HttpPost("{modul}/probe")]
    [KiStufe(KiStufe.GeraeteSchalten)]
    public async Task<ActionResult<SteuerungProbeService.Ergebnis>> Probeschaltung(
        string modul, CancellationToken ct)
    {
        if (SteuerungBauteile.FuerModul(modul).Count == 0) return NotFound();

        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var zuordnung = _geraete.EntitiesFuerModul(modul)
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

        return Ok(await _probe.ProbierenAsync(zuordnung, settings, ct));
    }

    [HttpPost("{modul}/automationen")]
    public async Task<ActionResult<SteuerungAutomationService.Bilanz>> AutomationenAnlegen(
        string modul, [FromQuery] bool vorschau, CancellationToken ct)
    {
        if (SteuerungBauteile.FuerModul(modul).Count == 0) return NotFound();

        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var zuordnung = _geraete.EntitiesFuerModul(modul)
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

        var bilanz = await _automationen.AnlegenAsync(modul, zuordnung, settings, vorschau, ct);
        // Fork AI (forkai.234): Ein Nachzug, der auf eine Automation wartet (der Merker braucht seinen Abgleich),
        // wird hier nachgeholt — gleich, in welcher Reihenfolge Rechenwerte und Automationen angelegt wurden.
        if (!vorschau && bilanz.Erreichbar)
        {
            bilanz = bilanz with { Nachgezogen = await _rechenwerte.NachziehenAsync(modul, settings, ct) };
        }

        return Ok(bilanz);
    }

    /// <summary>Die vorhandenen CO₂-Automationen auf Schwachstellen prüfen. Ändert nichts.</summary>
    /// <remarks>
    /// Fork AI: Die Absicherungen aus forkai.153 erreichten nur Automationen,
    /// die der Fork selbst anlegt. Hier sieht der Bediener, was in seinen
    /// vorhandenen — auch handgebauten — Automationen im Zweifel offen bleibt.
    /// </remarks>
    [HttpGet("{modul}/absicherung")]
    public async Task<ActionResult<SteuerungAbsicherungService.Lage>> AbsicherungPruefen(
        string modul, CancellationToken ct)
    {
        if (modul != "co2") return NotFound();
        return Ok(await _absicherung.PruefenAsync(Zuordnung(modul), _haSettings.GetEffectiveHomeAssistantSettings(), ct));
    }

    /// <summary>Die behebbaren Schwachstellen beheben — auf ausdrückliche Freigabe auf der CO₂-Seite.</summary>
    /// <remarks>
    /// Vorher wird jede Automation gesichert (wie beim Anlegen), nachher aus
    /// Home Assistant nachgelesen. Läuft gerade eine Dosierung, wird nichts
    /// geschrieben: neu schreiben bricht sie ab, mitten im Impuls.
    /// </remarks>
    [HttpPost("{modul}/absicherung")]
    public async Task<ActionResult<SteuerungAbsicherungService.Bilanz>> Absichern(
        string modul, CancellationToken ct)
    {
        if (modul != "co2") return NotFound();
        return Ok(await _absicherung.AbsichernAsync(Zuordnung(modul), _haSettings.GetEffectiveHomeAssistantSettings(), ct));
    }

    private Dictionary<string, string> Zuordnung(string modul)
        => _geraete.EntitiesFuerModul(modul)
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    [HttpPost("{modul}/rechenwerte")]
    public async Task<ActionResult<SteuerungRechenwertService.Bilanz>> RechenwerteAnlegen(
        string modul, CancellationToken ct)
    {
        if (SteuerungBauteile.FuerModul(modul).Count == 0) return NotFound();

        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var zuordnung = _geraete.EntitiesFuerModul(modul)
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

        var bilanz = await _rechenwerte.AnlegenAsync(modul, zuordnung, settings, ct);
        // Fork AI (forkai.225): Vorhandene Rechenwerte bekommen feste Änderungen nach.
        if (bilanz.Erreichbar)
        {
            bilanz = bilanz with { Nachgezogen = await _rechenwerte.NachziehenAsync(modul, settings, ct) };
        }

        if (!bilanz.Erreichbar)
        {
            return ConflictError(
                "home_assistant_stumm",
                "Es wurde nichts angelegt. Ohne Antwort von Home Assistant ist nicht zu erkennen, welche Rechenwerte es schon gibt.");
        }

        return Ok(bilanz);
    }

    [HttpPost("{modul}/helfer")]
    public async Task<ActionResult<SteuerungHelferService.Bilanz>> HelferAnlegen(
        string modul, [FromServices] SteuerungStartwerteService startwerte, CancellationToken ct)
    {
        if (SteuerungBauteile.FuerModul(modul).Count == 0) return NotFound();

        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var belegt = _geraete.EntitiesFuerModul(modul)
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bilanz = await startwerte.AnlegenAsync(modul, belegt, settings, ct);
        if (!bilanz.Erreichbar)
        {
            // Der Fork hat ein eigenes Fehlerformat; Problem() waere das von
            // ASP.NET und damit das zweite, das die Oberflaeche kennen muesste.
            return ConflictError(
                "home_assistant_stumm",
                "Es wurde nichts angelegt. Ohne Antwort von Home Assistant ist nicht zu erkennen, welche Helfer es schon gibt.");
        }

        return Ok(bilanz);
    }

    [HttpGet("{modul}/bestand")]
    public async Task<ActionResult<SteuerungBestandService.Bestandsaufnahme>> Bestand(
        string modul, CancellationToken ct)
    {
        if (SteuerungBauteile.FuerModul(modul).Count == 0) return NotFound();

        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var zuordnung = _geraete.EntitiesFuerModul(modul);
        var belegt = zuordnung
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Ok(await _bestand.AufnehmenAsync(modul, belegt, settings, ct, zuordnung));
    }

    /// <summary>Fork AI (forkai.141, F-037): Stand der Übertragung ans Bluelab-Gerät.</summary>
    [HttpGet("bluelab")]
    [ProducesResponseType(typeof(BluelabGrenzenService.Stand), StatusCodes.Status200OK)]
    public async Task<ActionResult<BluelabGrenzenService.Stand>> Bluelab(
        [FromServices] BluelabGrenzenService bluelab, CancellationToken ct)
        => Ok(await bluelab.StandAsync(ct));

    [HttpGet("geraete")]
    [ProducesResponseType(typeof(SteuerungGeraeteSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungGeraeteSeiteDto>> Geraete(CancellationToken ct)
    {
        var settings = _haSettings.GetEffectiveHomeAssistantSettings();
        var entities = await _ha.GetEntitiesAsync(settings, ct);
        var nachId = entities.ToDictionary(x => x.EntityId, x => x, StringComparer.OrdinalIgnoreCase);

        string? Wert(string? id) => id is not null && nachId.TryGetValue(id, out var s) ? s.State : null;

        var module = new List<SteuerungGeraeteModulDto>();
        // Die Zelt-Module der CO₂-Steuerung (co2-z{id}) stehen hinter den festen Modulen — das Bestand-Zelt bleibt „co2“.
        foreach (var modul in SteuerungGeraeteRollen.Module.Concat(Co2Zelte.Module.Where(m => m != Co2Modul.Basis)).Concat(SteuerungInstanzen.Module))
        {
            var gespeichert = _geraete.Gespeichert(modul);
            var aufgeloest = _geraete.EntitiesFuerModul(modul);
            var zeilen = SteuerungGeraeteRollen.FuerModul(modul).Select(rolle =>
            {
                var roh = gespeichert.TryGetValue(rolle.Schluessel, out var eigen) && !string.IsNullOrWhiteSpace(eigen)
                    ? eigen
                    : string.Empty;
                // Bewusst leer gelassen: das Feld zeigt leer, nicht die Marke.
                if (roh == SteuerungGeraeteRollen.BewusstLeer) roh = string.Empty;
                var ziel = aufgeloest.TryGetValue(rolle.Schluessel, out var id) ? id : null;
                return new SteuerungGeraetZeileDto(
                    rolle.Schluessel, rolle.Label, rolle.Gruppe, rolle.Einheit, rolle.Hinweis,
                    rolle.Pflicht, rolle.Domains, rolle.Vorgabe, roh, ziel, Wert(ziel),
                    ziel is not null && nachId.ContainsKey(ziel));
            }).ToList();
            module.Add(new SteuerungGeraeteModulDto(modul, Co2Modul.ZeltId(modul) is { } zeltId ? $"{ModulTitel(Co2Modul.Basis)} · {ZeltName(zeltId)}" : InstanzTitel(modul), zeilen));
        }

        var eigene = _geraete.EigeneGeraete()
            .Select(g => new EigenesGeraetDto(g.Rolle, g.EntityId, Wert(g.EntityId), nachId.ContainsKey(g.EntityId)))
            .ToList();

        return Ok(new SteuerungGeraeteSeiteDto(entities.Count > 0, module, eigene));
    }

    [HttpPut("geraete/{modul}")]
    [ProducesResponseType(typeof(SteuerungGeraeteSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungGeraeteSeiteDto>> GeraeteSpeichern(string modul, [FromBody] SteuerungGeraeteRequest request, CancellationToken ct)
    {
        if (request?.Zuordnungen is null) return BadRequestError("geraete_invalid", "Es wurde nichts übergeben.");
        if (!SteuerungGeraeteRollen.Module.Contains(modul, StringComparer.OrdinalIgnoreCase) && !Co2Zelte.Module.Contains(modul, StringComparer.Ordinal)
            && !SteuerungInstanzen.Module.Contains(modul, StringComparer.Ordinal))
            return BadRequestError("modul_unbekannt", $"Die Steuerung „{modul}\" hat keine Geräte-Rollen.");

        var fehler = _geraete.Speichern(modul, request.Zuordnungen);
        if (fehler.Count > 0)
        {
            foreach (var (feld, meldung) in fehler) ModelState.AddModelError(feld, meldung);
            return ValidationError();
        }
        return await Geraete(ct);
    }

    [HttpPost("geraete/eigene")]
    [ProducesResponseType(typeof(SteuerungGeraeteSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungGeraeteSeiteDto>> EigenesGeraet([FromBody] EigenesGeraetRequest request, CancellationToken ct)
    {
        if (request is null) return BadRequestError("geraet_invalid", "Es wurde nichts übergeben.");
        if (_geraete.EigenesGeraetSpeichern(request.Name ?? string.Empty, request.EntityId ?? string.Empty, request.AlterName) is { } fehler)
        {
            ModelState.AddModelError("name", fehler);
            return ValidationError();
        }
        return await Geraete(ct);
    }

    [HttpDelete("geraete/eigene/{name}")]
    [KiSicherungVorher]
    [ProducesResponseType(typeof(SteuerungGeraeteSeiteDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SteuerungGeraeteSeiteDto>> EigenesGeraetLoeschen(string name, CancellationToken ct)
    {
        if (_geraete.EigenesGeraetLoeschen(name) is { } fehler)
            return BadRequestError("geraet_in_benutzung", fehler);
        return await Geraete(ct);
    }

    private string ZeltName(int zeltId)
        => _grows.GetTents(includeArchived: true).FirstOrDefault(t => t.Id == zeltId)?.Name ?? $"Zelt {zeltId}";

    /// <summary>Der Titel eines Moduls; für eine weitere Instanz „Art · Name“ (zum Beispiel „Wasserkühler · Reserve“).</summary>
    private static string InstanzTitel(string modul)
        => SteuerungInstanzen.Aktuell.FirstOrDefault(i => i.Modul == modul) is { } i ? $"{ModulTitel(i.Art)} · {i.Name}" : ModulTitel(modul);

    internal static string ModulTitel(string modul) => modul switch
    {
        "co2" => "CO₂ · Begasung",
        "licht" => "Licht · Lampe",
        "zuluft" => "Zuluft · Lüfter",
        "raum" => "Raum",
        "chiller" => "Wasserkühler",
        "entfeuchter" => "Entfeuchter",
        "entfeuchter-zusatz" => "Zusatz-Entfeuchter",
        "bluelab" => "Bluelab · Gerätealarm",
        "cropsteering" => "Crop Steering",
        _ => modul,
    };

    private static Co2TagDto ToDto(Co2Tag t) => new(
        t.Datum, t.Impulse, Math.Round(t.VentilSekunden), Math.Round(t.Gramm), t.ZielErreichtUm, t.Abgeschlossen,
        t.JournalEntryId is not null, t.VerbrauchId is not null, t.Flaschenwechsel);
}

/// <summary>
/// Fork AI (forkai.238): Die Zuluft-Kachel der Übersicht — als eigene Funktion, damit die Zuordnung (Punktfarbe,
/// Wert, Zustand, Balken) ohne laufende App geprüft werden kann.
/// </summary>
/// <param name="schwelleText">Die Einschaltschwelle für die Kurzzeile, schon formatiert („1,5 g/m³").</param>
public static class ZuluftKachel
{
    public static SteuerungModulDto Modul(ZuluftLive zuluft, string schwelleText)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var diff = zuluft.DifferenzRegelGm3 ?? zuluft.DifferenzGm3;
        return new(
            Kennung: "zuluft",
            Titel: "Zuluft",
            Status: zuluft.AutomatikAn == false ? "aus" : zuluft.PortAn == true ? "an" : zuluft.Zustand?.Ton == "warn" ? "warn" : "aus",
            Kurz: $"Ansaugen ab {schwelleText} · {(zuluft.AutomatikAn == true ? "Automatik an" : "Automatik aus")}",
            // Die Differenz, nach der die Regelung entscheidet — dieselbe wie auf der Detailseite. Die rohe
            // stand neben „noch 0,07 bis 1,5" und widersprach dem Satz (forkai.238).
            Wert: diff is { } d ? d.ToString("0.00", de) + " g/m³" : "–",
            Unterzeile: zuluft.Zustand is { } z ? $"{z.Wort} · {z.Satz}" : "Zustand unbekannt",
            HatDetail: true,
            Zustand: zuluft.Zustand is { } zz ? new SteuerungModulZustandDto(zz.Wort, zz.Satz, zz.Ton) : null,
            Balken: zuluft.Zustand?.Balken is { } b ? new SteuerungModulBalkenDto(b.Ist, b.AusUnter, b.AnAb) : null);
    }
}

/// <param name="Zustand">Fork AI (forkai.238): Nur die Zuluft — Schild und Satz als eigene Zeile unter dem Wert; die anderen Module behalten ihre Unterzeile rechts.</param>
/// <param name="Balken">Fork AI (forkai.238): Nur die Zuluft bei „zu wenig Unterschied" — das Band mit Messwert und Grenzen.</param>
public sealed record SteuerungModulDto(string Kennung, string Titel, string Status, string Kurz, string Wert, string Unterzeile, bool HatDetail,
    SteuerungModulZustandDto? Zustand = null, SteuerungModulBalkenDto? Balken = null, int? ZeltId = null);
public sealed record SteuerungModulZustandDto(string Wort, string Satz, string Ton);
public sealed record SteuerungModulBalkenDto(double Ist, double AusUnter, double AnAb);
public sealed record SteuerungKarteDto(string Kennung, string Titel, string Beschreibung);
/// <param name="Zelte">Die aktiven Zelte in der Reihenfolge der Oberfläche — mit zwei oder mehr gliedert die Seite nach Zelten.</param>
public sealed record SteuerungUebersichtDto(bool HaErreichbar, IReadOnlyList<SteuerungModulDto> Module, IReadOnlyList<SteuerungKarteDto> NichtEingerichtet, DateTime StandUtc, IReadOnlyList<SteuerungZeltKurzDto>? Zelte = null);
public sealed record SteuerungZeltKurzDto(int Id, string Name);
/// <param name="Module">Die Module, für die Bausteine in Home Assistant angelegt werden, in Anlege-Reihenfolge.</param>
/// <param name="Rollenmodule">Alle Module der Steuerung, deren Geräte (Rollen) zugeordnet werden — auch die Lampe.</param>
/// <param name="FehlendePflicht">Die Namen der Pflicht-Rollen ohne Gerät — die Meldung „Pflichtgerät fehlt" nennt sie.</param>
public sealed record SteuerungAuswahlEintragDto(string Kennung, string Titel, string Beschreibung, bool Gewaehlt, int PflichtZugeordnet, int PflichtGesamt, IReadOnlyList<string> Module, IReadOnlyList<string> Rollenmodule, IReadOnlyList<string> FehlendePflicht);
/// <param name="Gespeichert">False: der Nutzer hat nie gewählt, die Liste ergibt sich aus den zugeordneten Geräten.</param>
public sealed record SteuerungAuswahlDto(bool Gespeichert, IReadOnlyList<SteuerungAuswahlEintragDto> Eintraege);
public sealed record SteuerungAuswahlAenderung(IReadOnlyList<string> Gewaehlt);
public sealed record SteuerungZuweisungEintragDto(string Kennung, string Titel, int? ZeltId);
public sealed record SteuerungZuweisungDto(IReadOnlyList<SteuerungZeltKurzDto> Zelte, IReadOnlyList<SteuerungZuweisungEintragDto> Eintraege);
/// <param name="Zuordnungen">Steuerung (Kennung) → Zelt (Id). Nur die genannten ändern sich.</param>
public sealed record SteuerungZuweisungAenderung(Dictionary<string, int> Zuordnungen);
public sealed record KostenArtikelKurzDto(int Id, string Name, string Einheit);
public sealed record Co2TagDto(string Datum, int Impulse, double VentilSekunden, double Gramm, string? ZielErreichtUm, bool Abgeschlossen, bool ImJournal, bool InKosten, bool Flaschenwechsel);
public sealed record Co2SeiteDto(
    Co2Einstellungen Einstellungen,
    Co2Live Live,
    IReadOnlyList<Co2TagDto> Tage,
    IReadOnlyList<KostenArtikelKurzDto> Artikel,
    string? GrowName,
    string? Phase,
    int? PlanPpm,
    string? PlanHerkunft)
{
    /// <summary>Nach dem Speichern: ob Home Assistant alle Sollwerte angenommen hat (null beim Lesen).</summary>
    public bool? HaAngenommen { get; init; }

    /// <summary>Woher das Zeltvolumen kommt: aus den Maßen des Zeltes mit CO₂ oder von Hand (dann mit Grund).</summary>
    public Co2ZeltvolumenQuelle? ZeltvolumenQuelle { get; init; }

    /// <summary>Das Zelt, dessen CO₂-Steuerung die Seite zeigt (null, solange noch kein Zelt CO₂-Begasung hat).</summary>
    public int? ZeltId { get; init; }

    /// <summary>Alle Zelte mit CO₂-Steuerung, das Bestand-Zelt zuerst — für den Zeltwechsel.</summary>
    public IReadOnlyList<Co2ZeltKurzDto> Zelte { get; init; } = [];

    /// <summary>
    /// Fork AI (forkai.21): Wie viele Rollen dieser Steuerung eine Entität haben.
    /// Die Seite zeigt damit eine Zeile zur Geräte-Zuordnung — bearbeitet wird dort,
    /// nicht hier, damit ein Gerät genau eine Wahrheit behält.
    /// </summary>
    public int GeraeteZugeordnet { get; init; }
    public int GeraeteGesamt { get; init; }
}

public sealed record ZuluftSeiteDto(ZuluftEinstellungen Einstellungen, ZuluftLive Live)
{
    /// <summary>Nach dem Speichern: ob Home Assistant alle Sollwerte angenommen hat (null beim Lesen).</summary>
    public bool? HaAngenommen { get; init; }

    /// <summary>True, solange die Werte aus den vorhandenen Helfern kommen und nicht aus der Datenbank.</summary>
    public bool AusHomeAssistantUebernommen { get; init; }

    public int GeraeteZugeordnet { get; init; }
    public int GeraeteGesamt { get; init; }
}

public sealed record EntfeuchterSeiteDto(EntfeuchterEinstellungen Einstellungen, EntfeuchterLive Live)
{
    /// <summary>Nach dem Speichern: ob Home Assistant alle Sollwerte angenommen hat (null beim Lesen).</summary>
    public bool? HaAngenommen { get; init; }

    /// <summary>True, solange die Werte aus den vorhandenen Helfern kommen und nicht aus der Datenbank.</summary>
    public bool AusHomeAssistantUebernommen { get; init; }

    public int GeraeteZugeordnet { get; init; }
    public int GeraeteGesamt { get; init; }

    /// <summary>Helfer in Home Assistant, die von den Einstellungen des Fork abweichen — leer, wenn alles übereinstimmt oder HA nicht antwortet.</summary>
    public IReadOnlyList<EntfeuchterSteuerungService.Abweichung> Abweichungen { get; init; } = [];
}

/// <summary>Eine weitere Instanz einer Steuerung für die Oberfläche.</summary>
public sealed record SteuerungInstanzDto(string Modul, string Art, int Nummer, string Name, int? ZeltId);

/// <param name="HaEntfernt">Nach dem Entfernen mit <c>ausHa</c>: wie viele Bausteine aus Home Assistant verschwunden sind; sonst <c>null</c>.</param>
public sealed record SteuerungInstanzenDto(IReadOnlyList<SteuerungInstanzDto> Instanzen, int? HaEntfernt = null);

public sealed class SteuerungInstanzAnlegen
{
    public string Art { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? ZeltId { get; set; }
}

public sealed class SteuerungInstanzUmbenennen
{
    public string Name { get; set; } = string.Empty;
}

public sealed record ChillerSeiteDto(ChillerEinstellungen Einstellungen, ChillerLive Live)
{
    /// <summary>Nach dem Speichern: ob Home Assistant alle Sollwerte angenommen hat (null beim Lesen).</summary>
    public bool? HaAngenommen { get; init; }

    /// <summary>True, solange die Werte aus den vorhandenen Helfern kommen und nicht aus der Datenbank.</summary>
    public bool AusHomeAssistantUebernommen { get; init; }

    public int GeraeteZugeordnet { get; init; }
    public int GeraeteGesamt { get; init; }
}

public sealed record LichtBefehlRequest(string? Art, string? Preset, int? Stufe);

public sealed record LichtWaechterPauseRequest(int Stunden);

public sealed record LichtSeiteDto(LichtEinstellungen Einstellungen, LichtLive Live)
{
    /// <summary>Nach einem Befehl: ob Home Assistant alle Aufrufe angenommen hat (null beim Lesen).</summary>
    public bool? HaAngenommen { get; init; }
    public int GeraeteZugeordnet { get; init; }
    public int GeraeteGesamt { get; init; }
}

public sealed record SteuerungGeraetZeileDto(
    string Rolle,
    string Label,
    string Gruppe,
    string? Einheit,
    string? Hinweis,
    bool Pflicht,
    IReadOnlyList<string> Domains,
    string Vorgabe,
    // Eingetragen: was in der Oberfläche im Feld steht — Entity-ID oder @Verweis.
    string Eingetragen,
    // EntityId: worauf es am Ende hinausläuft; null bei leerem oder totem Verweis.
    string? EntityId,
    string? Livewert,
    bool Gefunden);

public sealed record SteuerungGeraeteModulDto(string Modul, string Titel, IReadOnlyList<SteuerungGeraetZeileDto> Zeilen);
public sealed record EigenesGeraetDto(string Name, string EntityId, string? Livewert, bool Gefunden);
public sealed record EigenesGeraetRequest(string? Name, string? EntityId, string? AlterName);
public sealed record SteuerungGeraeteRequest(Dictionary<string, string?> Zuordnungen);
public sealed record SteuerungGeraeteSeiteDto(bool HaErreichbar, IReadOnlyList<SteuerungGeraeteModulDto> Module, IReadOnlyList<EigenesGeraetDto> Eigene);

/// <summary>Ein Zelt mit CO₂-Steuerung für den Zeltwechsel: Zelt-Id, Name, Modul (<c>co2</c> oder <c>co2-z{id}</c>).</summary>
public sealed record Co2ZeltKurzDto(int Id, string Name, string Modul);
