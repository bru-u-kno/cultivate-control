using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Services.Knowledge.Schema;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>Ein Düngeprogramm in der Auswahl „Plan von".</summary>
public sealed record ProgrammKurzDto(
    string Id,
    string Name,
    string Hersteller,
    /// <summary>Eigenes Programm (Kennung <c>eigen-…</c>): änderbar. Mitgelieferte bleiben unverändert.</summary>
    bool Eigen,
    int Wochen,
    /// <summary>Hat das Programm Klimawerte je Woche (Luft, Feuchte …) oder kommen sie aus dem Standard?</summary>
    bool MitKlima);

/// <summary>Alle bearbeitbaren Wochen eines Programms — ohne Grow.</summary>
public sealed record ProgrammWerteDto(
    string ProgrammId,
    string ProgrammName,
    bool Eigen,
    /// <summary>Laufende Grows ohne eigenen Plan, die dieses Programm benutzen — eine Änderung wirkt dort sofort.</summary>
    int AndereGrows,
    List<WochenwertSpalteDto> Spalten);

/// <summary>Die Trocknung eines Programms: seine Etappen oder der Standard, dazu was gerade läuft.</summary>
public sealed record TrocknungDto(
    string ProgrammId,
    string ProgrammName,
    bool Eigen,
    /// <summary>Das Programm führt keine eigenen Etappen — angezeigt wird der Standard.</summary>
    bool IstStandard,
    int MaxTage,
    int MaxEtappen,
    List<TrockenEtappe> Etappen,
    List<TrockenEtappe> Standard,
    /// <summary>Trocknungen, die gerade nach diesem Programm laufen (ein Eintrag je Zelt).</summary>
    List<TrocknungLaeuftDto> Laeuft);

public sealed record TrocknungLaeuftDto(string Zelt, int Tag, int EtappeNr, string EtappeName, bool Gehalten);

public sealed class TrocknungSpeichernRequest
{
    public List<TrockenEtappe> Etappen { get; set; } = [];

    /// <summary>Name des neuen eigenen Programms; nur nötig, wenn ein mitgeliefertes geändert wird.</summary>
    public string? Name { get; set; }
}

public sealed record TrocknungGespeichertDto(TrocknungDto Trocknung, bool NeuAngelegt);

public sealed class ProgrammWerteSpeichernRequest
{
    public List<WochenwertAenderung> Aenderungen { get; set; } = [];

    /// <summary>Name des neuen eigenen Programms; nur nötig, wenn ein mitgeliefertes geändert wird.</summary>
    public string? Name { get; set; }
}

/// <summary>Antwort auf das Speichern: das Programm, das jetzt gilt (bei einer Kopie ein neues).</summary>
public sealed record ProgrammWerteGespeichertDto(
    ProgrammWerteDto Werte,
    bool NeuAngelegt,
    /// <summary>Gespeichert, aber die Übergabe an Home Assistant ging nicht durch — der nächste Lauf holt sie nach.</summary>
    string? Hinweis = null);

/// <summary>
/// Fork AI (A-029): Programme ohne Grow ansehen und bearbeiten.
/// </summary>
/// <remarks>
/// <para><b>Der Anlass.</b> Bearbeiten ließ sich ein Plan nur über einen laufenden
/// Grow (<c>/api/wochenplan/werte/{growId}</c>). Zwischen zwei Grows blieb die
/// Seite „Plan" leer — Vorlagen und eigene Programme waren nirgends änderbar.</para>
///
/// <para><b>Mitgelieferte bleiben unverändert.</b> Wer eines ändert, bekommt eine
/// Kopie als eigenes Programm (<see cref="EigeneProgramme"/>), genau wie beim
/// Speichern am Grow. Ein eigenes Programm wird an Ort und Stelle geändert.</para>
///
/// <para><b>Nur die Wochenwerte.</b> Die Dosierung (ml/L) gehört weiter dem Plan
/// eines Grows; hier geht es um Ziele je Woche.</para>
/// </remarks>
[ApiController]
[Route("api/programme")]
[Produces("application/json")]
[KiStufe(KiStufe.GrowPlanen)]
public sealed class ProgrammeApiController : ApiControllerBase
{
    private const int NameMax = 80;

    // Zwei gleichzeitige Speichern desselben Programms würden sich die Datei überschreiben.
    private static readonly object Schreiben = new();

    private readonly KnowledgeBaseLoader _wissen;
    private readonly EigeneProgramme _eigene;
    private readonly WochenwertUeberlagerung _ueberlagerung;
    private readonly WochenwertRepository _wochenwerte;
    private readonly GrowRepository _grows;
    private readonly WochenplanSyncService? _sync;
    private readonly HarvestRepository? _ernten;

    public ProgrammeApiController(
        KnowledgeBaseLoader wissen,
        EigeneProgramme eigene,
        WochenwertUeberlagerung ueberlagerung,
        WochenwertRepository wochenwerte,
        GrowRepository grows,
        WochenplanSyncService? sync = null,
        HarvestRepository? ernten = null)
    {
        _wissen = wissen;
        _eigene = eigene;
        _ueberlagerung = ueberlagerung;
        _wochenwerte = wochenwerte;
        _grows = grows;
        _sync = sync;
        _ernten = ernten;
    }

    /// <summary>Alle Programme mit Wochenraster — die Auswahl „Plan von".</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProgrammKurzDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProgrammKurzDto>> Liste()
        => Ok(_wissen.NutrientPrograms
            .Select(p => new ProgrammKurzDto(
                p.Id,
                p.Name,
                p.Manufacturer,
                EigeneProgramme.IstEigen(p.Id),
                p.FeedChart?.Columns.Count ?? 0,
                p.FeedChart?.Columns.Any(s => s.AirTempC is not null) ?? false))
            // Eigene zuletzt: die Auswahl zeigt zuerst, was mitgeliefert ist.
            .OrderBy(p => p.Eigen)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    [HttpGet("{id}/werte")]
    [ProducesResponseType(typeof(ProgrammWerteDto), StatusCodes.Status200OK)]
    public ActionResult<ProgrammWerteDto> Werte(string id)
        => Mitraster(id) is { } programm ? Ok(Dto(programm)) : KeinProgramm();

    [HttpPost("{id}/werte")]
    [ProducesResponseType(typeof(ProgrammWerteGespeichertDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProgrammWerteGespeichertDto>> WerteSpeichern(
        string id, [FromBody] ProgrammWerteSpeichernRequest anfrage, CancellationToken ct)
    {
        if (Mitraster(id) is not { } vorlage) return KeinProgramm();
        if (anfrage?.Aenderungen is null || anfrage.Aenderungen.Any(a => a is null))
            return ValidationError("Die Anfrage ist leer oder unlesbar.");

        // Die Vergleichswerte: was die Datei des Programms sagt, nicht eine alte Abweichung.
        double? Planwert(FeedChartColumn spalte, Wochenwertfelder.Feld feld) => _ueberlagerung.Planwert(spalte, feld);

        var pruefung = WochenwertPruefung.Pruefen(
            vorlage.FeedChart!.Columns, vorlage.Name, anfrage.Aenderungen, Planwert, bandWandertMit: false);
        if (!pruefung.Gueltig) return ValidationError(pruefung.Fehler!);

        var eigen = EigeneProgramme.IstEigen(vorlage.Id);
        var name = (anfrage.Name ?? string.Empty).Trim();
        if (!eigen && name.Length == 0) name = $"{vorlage.Name} (eigen)";
        if (name.Length > NameMax) return ValidationError($"Der Name darf höchstens {NameMax} Zeichen lang sein.");

        ProgrammWerteDto dto;
        lock (Schreiben)
        {
            if (eigen)
            {
                // Eine ältere Abweichung läge sonst über dem neuen Wert und verdeckte ihn.
                AbweichungenEntfernen(vorlage.Id);
            }

            var ziel = eigen ? vorlage : _eigene.Anlegen(vorlage, name);
            foreach (var g in pruefung.Aenderungen)
            {
                var feld = Wochenwertfelder.Finden(g.Feld)!;
                var quelle = vorlage.FeedChart!.Columns.First(s => s.Id == g.SpalteId);
                var spalte = ziel.FeedChart!.Columns.First(s => s.Id == g.SpalteId);
                feld.Schreiben(spalte, g.Wert ?? Planwert(quelle, feld));
            }

            _eigene.Speichern(ziel);
            dto = Dto(_eigene.Finden(ziel.Id) ?? ziel);
        }

        // Laufende Grows ohne eigenen Plan lesen ihre Ziele aus diesem Programm: dann gleich an
        // Home Assistant übergeben wie der Wochenplan, nicht erst beim Lauf um 06:00. Gespeichert
        // ist gespeichert — scheitert die Übergabe, sagt die Antwort es und der nächste Lauf holt sie nach.
        string? hinweis = null;
        if (UebergabeNoetig(eigen, dto.AndereGrows) && _sync is not null)
        {
            try
            {
                await _sync.UebergebenAsync(ct);
            }
            catch (Exception)
            {
                hinweis = "Gespeichert. Home Assistant war gerade nicht erreichbar — die Übergabe holt der nächste Lauf nach.";
            }
        }

        return Ok(new ProgrammWerteGespeichertDto(dto, NeuAngelegt: !eigen, hinweis));
    }

    /// <summary>
    /// Wirkt eine Änderung auf Grows, die jetzt laufen? Nur ein eigenes Programm mit Nutzern
    /// ohne eigenen Plan — eine neue Kopie hat noch keinen, und eine Vorlage ändert sich nie.
    /// </summary>
    public static bool UebergabeNoetig(bool eigen, int andereGrows) => eigen && andereGrows > 0;

    /// <summary>Die Trocknung eines Programms — auch eines ohne Wochenwerte (Canna Aqua, VBX).</summary>
    [HttpGet("{id}/trocknung")]
    [ProducesResponseType(typeof(TrocknungDto), StatusCodes.Status200OK)]
    public ActionResult<TrocknungDto> Trocknung(string id)
        => _eigene.Finden(id) is { } programm ? Ok(TrocknungDto(programm)) : KeinProgramm();

    /// <summary>
    /// Die Etappen der Trocknung speichern. Wie bei den Wochenwerten: eine mitgelieferte Vorlage
    /// bleibt unverändert, Speichern legt ein eigenes Programm an.
    /// </summary>
    [HttpPost("{id}/trocknung")]
    [ProducesResponseType(typeof(TrocknungGespeichertDto), StatusCodes.Status200OK)]
    public ActionResult<TrocknungGespeichertDto> TrocknungSpeichern(string id, [FromBody] TrocknungSpeichernRequest anfrage)
    {
        if (_eigene.Finden(id) is not { } vorlage) return KeinProgramm();
        if (anfrage?.Etappen is null || anfrage.Etappen.Any(e => e is null))
            return ValidationError("Die Anfrage ist leer oder unlesbar.");

        var etappen = TrocknungsPlan.Bereinigt(anfrage.Etappen);
        if (TrocknungsPlan.Pruefen(etappen) is { } fehler) return ValidationError(fehler);

        var eigen = EigeneProgramme.IstEigen(vorlage.Id);
        var name = (anfrage.Name ?? string.Empty).Trim();
        if (!eigen && name.Length == 0) name = $"{vorlage.Name} (eigen)";
        if (name.Length > NameMax) return ValidationError($"Der Name darf höchstens {NameMax} Zeichen lang sein.");

        lock (Schreiben)
        {
            var ziel = eigen ? vorlage : _eigene.Anlegen(vorlage, name);
            ziel.Trocknung = etappen;
            _eigene.Speichern(ziel);
            return Ok(new TrocknungGespeichertDto(TrocknungDto(_eigene.Finden(ziel.Id) ?? ziel), NeuAngelegt: !eigen));
        }
    }

    private TrocknungDto TrocknungDto(NutrientProgramDefinition programm)
        => new(
            programm.Id,
            programm.Name,
            EigeneProgramme.IstEigen(programm.Id),
            IstStandard: !TrocknungsPlan.HatEigene(programm),
            TrocknungsPlan.MaxTage,
            TrocknungsPlan.MaxEtappen,
            TrocknungsPlan.Bereinigt(TrocknungsPlan.Fuer(programm)),
            TrocknungsPlan.Bereinigt(TrocknungsPlan.Standard),
            Laeuft(programm));

    /// <summary>Welche Zelte trocknen gerade nach diesem Programm, und bei welcher Etappe?</summary>
    private List<TrocknungLaeuftDto> Laeuft(NutrientProgramDefinition programm)
    {
        var liste = new List<TrocknungLaeuftDto>();
        if (_ernten is null) return liste;

        foreach (var zelt in _grows.GetTents())
        {
            if (DryingWindow.StandFor(_grows, _ernten, zelt.Id, DateTime.Today) is not { } laeuft) continue;
            if (!string.Equals(laeuft.Grow.FeedProgramId, programm.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (TrocknungsPlan.EtappeAm(TrocknungsPlan.Fuer(programm), laeuft.Tag) is not { } stand) continue;
            liste.Add(new TrocknungLaeuftDto(zelt.Name, laeuft.Tag, stand.Index + 1, stand.Etappe.Name, stand.Gehalten));
        }

        return liste;
    }

    private NutrientProgramDefinition? Mitraster(string id)
        => _eigene.Finden(id) is { FeedChart.Columns.Count: > 0 } programm ? programm : null;

    private void AbweichungenEntfernen(string programmId)
    {
        var zeilen = _wochenwerte.Alle()
            .Where(w => string.Equals(w.ProgrammId, programmId, StringComparison.OrdinalIgnoreCase))
            .Select(w => (w.SpalteId, w.Feld, (double?)null))
            .ToList();
        if (zeilen.Count == 0) return;
        _wochenwerte.Speichern(programmId, zeilen);
    }

    private ProgrammWerteDto Dto(NutrientProgramDefinition programm)
        => new(
            programm.Id,
            programm.Name,
            EigeneProgramme.IstEigen(programm.Id),
            _grows.GetActiveGrows().Count(g =>
                !GrowPlanService.HatPlan(g.Id)
                && string.Equals(g.FeedProgramId, programm.Id, StringComparison.OrdinalIgnoreCase)),
            programm.FeedChart!.Columns
                .Select(spalte => new WochenwertSpalteDto(
                    spalte.Id,
                    spalte.Label,
                    spalte.Stage,
                    spalte.Week,
                    IstJetzt: false,
                    Wochenwertfelder.Alle
                        .Select(feld => new WochenwertFeldDto(
                            feld.Name,
                            feld.Bezeichnung,
                            feld.Einheit,
                            feld.Min,
                            feld.Max,
                            feld.Schritt,
                            feld.Lesen(spalte),
                            _ueberlagerung.Planwert(spalte, feld),
                            _ueberlagerung.IstGeaendert(programm.Id, spalte.Id, feld.Name)))
                        .ToList()))
                .ToList());

    private ActionResult KeinProgramm()
        => NotFoundError("programm_ohne_wochenplan", "Dieses Programm gibt es nicht oder es hat keinen Wochenplan.");
}
