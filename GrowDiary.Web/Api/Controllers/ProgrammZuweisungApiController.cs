using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Infrastructure.KiZugriff;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;

namespace GrowDiary.Web.Api.Controllers;

/// <summary>Ein Grow in der Liste „Gilt für diese Grows“ auf der Seite Plan.</summary>
public sealed record ZuweisungGrowDto(
    int GrowId,
    string Name,
    string? Zelt,
    /// <summary><c>Running</c> (läuft) oder <c>Planning</c> (geplant, noch nicht gestartet).</summary>
    string Status,
    /// <summary>Name des Programms, das der Grow heute nutzt; <c>null</c> = keines.</summary>
    string? AktuellesProgramm,
    /// <summary>Dieses Programm ist dem Grow schon zugewiesen — nichts zu tun.</summary>
    bool Zugewiesen,
    /// <summary>Der Grow hat einen Plan; ein Wechsel stellt seine Wochenwerte um.</summary>
    bool HatPlan,
    /// <summary>Eigene Änderungen am Plan, die beim Wechsel mitgehen oder entfallen können.</summary>
    int EigeneAenderungen);

public sealed class ProgrammZuweisenRequest
{
    public List<int> GrowIds { get; set; } = [];

    /// <summary>Eigene Änderungen am Plan der laufenden Grows in die neuen Wochen mitnehmen.</summary>
    public bool AenderungenBehalten { get; set; } = true;
}

public sealed record ProgrammZuweisungErgebnisDto(int GrowId, string Name, bool Umgestellt, int Entfallen, string? Hinweis);

/// <summary>
/// Fork AI (A-030): Ein Düngeprogramm Grows zuweisen — von der Seite „Plan“ aus.
/// </summary>
/// <remarks>
/// <para><b>Düngeplan gehört dem Grow</b> (er richtet sich nach den Pflanzen und ihrer Woche), nicht dem Zelt.
/// Diese Schnittstelle ist nur eine zweite Tür zu demselben Wechsel wie im Grow-Formular: Grows mit Plan gehen über
/// <see cref="GrowPlanService.ProgrammWechseln"/> (setzt Plan und Programm am Grow in einem Zug), Grows ohne Plan
/// (geplant, noch nicht gestartet) bekommen das Programm am Grow eingetragen.</para>
/// <para>Abgeschlossene Grows erscheinen nicht — ihr Plan ist eingefroren.</para>
/// </remarks>
[ApiController]
[Route("api/programme/{programmId}/zuweisung")]
[Produces("application/json")]
[KiStufe(KiStufe.GrowPlanen)]
public sealed class ProgrammZuweisungApiController : ApiControllerBase
{
    private readonly KnowledgeBaseLoader _wissen;
    private readonly GrowRepository _grows;
    private readonly GrowPlanService _plaene;
    private readonly WochenplanSyncService? _sync;

    public ProgrammZuweisungApiController(
        KnowledgeBaseLoader wissen, GrowRepository grows, GrowPlanService plaene, WochenplanSyncService? sync = null)
    {
        _wissen = wissen;
        _grows = grows;
        _plaene = plaene;
        _sync = sync;
    }

    /// <summary>Alle Grows, denen sich dieses Programm zuweisen lässt, mit ihrem heutigen Stand.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ZuweisungGrowDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ZuweisungGrowDto>> Liste(string programmId)
    {
        if (Programm(programmId) is null) return NotFoundError("programm_nicht_gefunden", "Dieses Programm gibt es nicht.");
        return Ok(Kandidaten().Select(g => Dto(g, programmId)).ToList());
    }

    /// <summary>Das Programm den gewählten Grows zuweisen. Ein Grow, der nicht geht, stoppt die übrigen nicht.</summary>
    [HttpPost]
    [KiSicherungVorher]
    [ProducesResponseType(typeof(IReadOnlyList<ProgrammZuweisungErgebnisDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProgrammZuweisungErgebnisDto>>> Zuweisen(
        string programmId, [FromBody] ProgrammZuweisenRequest anfrage, CancellationToken ct)
    {
        if (Programm(programmId) is not { } programm) return NotFoundError("programm_nicht_gefunden", "Dieses Programm gibt es nicht.");
        if (anfrage.GrowIds.Count == 0) return ValidationError("Bitte mindestens einen Grow wählen.");

        var erlaubt = Kandidaten().ToDictionary(g => g.Id);
        var ergebnisse = new List<ProgrammZuweisungErgebnisDto>();
        foreach (var id in anfrage.GrowIds.Distinct())
        {
            if (!erlaubt.TryGetValue(id, out var grow))
            {
                ergebnisse.Add(new ProgrammZuweisungErgebnisDto(id, $"Grow {id}", false, 0, "Diesen Grow gibt es nicht oder er ist abgeschlossen."));
                continue;
            }

            if (string.Equals(AktuelleProgrammId(grow), programm.Id, StringComparison.OrdinalIgnoreCase))
            {
                ergebnisse.Add(new ProgrammZuweisungErgebnisDto(id, grow.Name, false, 0, "Hat dieses Programm schon."));
                continue;
            }

            try
            {
                var entfallen = 0;
                if (GrowPlanService.HatPlan(grow.Id))
                {
                    var wechsel = _plaene.ProgrammWechseln(grow, programm.Id, anfrage.AenderungenBehalten);
                    grow.FeedProgramId = wechsel.ProgrammId;
                    grow.Nutrients = wechsel.ProgrammName;
                    entfallen = wechsel.Entfallen;
                }
                else
                {
                    grow.FeedProgramId = programm.Id;
                    grow.Nutrients = programm.Name;
                }

                _grows.UpdateGrow(grow);
                ergebnisse.Add(new ProgrammZuweisungErgebnisDto(id, grow.Name, true, entfallen, null));
            }
            catch (Exception ex)
            {
                // Hat der Plan schon gewechselt und nur das Speichern am Grow scheiterte, laufen Plan und Feld sonst auseinander —
                // und der Plan gewinnt beim Lesen. Das Feld wird deshalb nachgezogen; gelingt auch das nicht, steht es im Hinweis.
                var nachgezogen = Nachziehen(grow, programm);
                var hinweis = ex is ArgumentException or InvalidOperationException
                    ? ex.Message
                    : "Das Zuweisen ist nicht gelungen. Bitte versuche es noch einmal.";
                ergebnisse.Add(new ProgrammZuweisungErgebnisDto(id, grow.Name, nachgezogen, 0, nachgezogen ? null : hinweis));
            }
        }

        if (_sync is not null && ergebnisse.Any(e => e.Umgestellt))
        {
            try
            {
                await _sync.UebergebenAsync(ct);
            }
            catch (Exception)
            {
                // Zugewiesen ist gespeichert; die Übergabe an Home Assistant holt der nächste Lauf nach.
            }
        }

        return Ok(ergebnisse);
    }

    private Services.Knowledge.Schema.NutrientProgramDefinition? Programm(string id)
        => _wissen.NutrientPrograms.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Grows, die laufen oder geplant sind und deren Plan nicht eingefroren ist.</summary>
    private List<GrowRun> Kandidaten()
        => _grows.GetActiveGrows()
            .Where(g => !g.IsArchived && _plaene.Stand(g.Id, GrowPlanStaende.Ende) is null)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Zieht das Feld am Grow auf das Programm des Plans nach, falls der Plan schon gewechselt hat.</summary>
    private bool Nachziehen(GrowRun grow, Services.Knowledge.Schema.NutrientProgramDefinition programm)
    {
        try
        {
            if (!GrowPlanService.HatPlan(grow.Id)
                || !string.Equals(GrowPlanRegister.Programm(grow.Id)?.Id, programm.Id, StringComparison.OrdinalIgnoreCase)) return false;
            grow.FeedProgramId = programm.Id;
            grow.Nutrients = programm.Name;
            _grows.UpdateGrow(grow);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Bei einem Grow mit Plan gewinnt der Plan beim Lesen — nicht das Feld am Grow.</summary>
    private static string? AktuelleProgrammId(GrowRun grow)
        => GrowPlanService.HatPlan(grow.Id) ? GrowPlanRegister.Programm(grow.Id)?.Id ?? grow.FeedProgramId : grow.FeedProgramId;

    private ZuweisungGrowDto Dto(GrowRun grow, string programmId)
    {
        var hatPlan = GrowPlanService.HatPlan(grow.Id);
        var aktuell = string.IsNullOrWhiteSpace(grow.FeedProgramId)
            ? null
            : Programm(grow.FeedProgramId)?.Name ?? grow.Nutrients ?? grow.FeedProgramId;
        return new ZuweisungGrowDto(
            grow.Id,
            grow.Name,
            grow.TentId is { } tentId ? _grows.GetTent(tentId)?.Name : null,
            grow.Status.ToString(),
            aktuell,
            string.Equals(AktuelleProgrammId(grow), programmId, StringComparison.OrdinalIgnoreCase),
            hatPlan,
            hatPlan ? _plaene.EigeneAenderungen(grow.Id) : 0);
    }
}
