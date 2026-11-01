using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Ein Düngeprogramm Grows zuweisen (A-030): die Düngeplan-Seite „Gilt für diese Grows“.
/// </summary>
/// <remarks>
/// Der Düngeplan gehört dem Grow, nicht dem Zelt. Geprüft wird, dass die Zuweisung denselben Wechsel
/// macht wie das Grow-Formular: geplante Grows bekommen das Programm eingetragen, Grows mit Plan wechseln
/// über <see cref="GrowPlanService.ProgrammWechseln"/>, abgeschlossene erscheinen nicht.
/// </remarks>
public sealed class ProgrammZuweisungApiTests : IDisposable
{
    private const string Neu = "skx-canna-aqua";
    private const string Alt = "athena";

    private readonly string _wurzel;
    private readonly GrowRepository _grows;
    private readonly GrowPlanService _plaene;
    private readonly ProgrammZuweisungApiController _api;
    private readonly List<int> _mitPlan = [];

    public ProgrammZuweisungApiTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "ProgrammZuweisung_" + Guid.NewGuid().ToString("N"));
        var pfade = new AppPaths(_wurzel);
        KopiereWissen(_wurzel);
        TestDatabase.Initialize(pfade);
        var wissen = new KnowledgeBaseLoader(pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        _grows = new GrowRepository(pfade);
        _plaene = new GrowPlanService(
            new GrowPlanRepository(pfade), wissen, new TargetValueService(wissen), NullLogger<GrowPlanService>.Instance,
            new EigeneProgramme(pfade, wissen, NullLogger<EigeneProgramme>.Instance));
        _api = new ProgrammZuweisungApiController(wissen, _grows, _plaene);
    }

    public void Dispose()
    {
        foreach (var id in _mitPlan) GrowPlanRegister.Entfernen(id);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private int Grow(string name, GrowStatus status, string? programm, bool mitPlan = false)
    {
        var id = _grows.CreateGrow(new GrowRun
        {
            Name = name, HydroStyle = HydroStyle.RDWC, Status = status, StartDate = DateTime.Today,
            FeedProgramId = programm, Nutrients = programm,
        });
        if (mitPlan)
        {
            _plaene.Anlegen(_grows.GetGrow(id)!);
            _mitPlan.Add(id);
        }
        return id;
    }

    private static T Wert<T>(ActionResult<T> antwort)
        => Assert.IsType<OkObjectResult>(antwort.Result).Value is T wert ? wert : throw new InvalidOperationException("Falscher Antworttyp.");

    private IReadOnlyList<ProgrammZuweisungErgebnisDto> Zuweisen(string programm, bool behalten, params int[] ids)
        => Wert(_api.Zuweisen(programm, new ProgrammZuweisenRequest { GrowIds = [.. ids], AenderungenBehalten = behalten }, CancellationToken.None)
            .GetAwaiter().GetResult());

    [Fact]
    public void Die_Liste_zeigt_laufende_und_geplante_Grows_aber_keine_abgeschlossenen()
    {
        Grow("Läuft", GrowStatus.Running, Alt);
        Grow("Geplant", GrowStatus.Planning, null);
        Grow("Fertig", GrowStatus.Completed, Alt);

        var liste = Wert(_api.Liste(Neu));

        Assert.Equal(["Geplant", "Läuft"], liste.Select(g => g.Name).Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
        Assert.DoesNotContain(liste, g => g.Name == "Fertig");
        Assert.All(liste, g => Assert.False(g.Zugewiesen));
        Assert.Null(liste.Single(g => g.Name == "Geplant").AktuellesProgramm);
    }

    [Fact]
    public void Ein_geplanter_Grow_ohne_Plan_bekommt_das_Programm_am_Grow_eingetragen()
    {
        var id = Grow("Geplant", GrowStatus.Planning, null);

        var ergebnis = Zuweisen(Neu, true, id).Single();

        Assert.True(ergebnis.Umgestellt);
        Assert.Equal(Neu, _grows.GetGrow(id)!.FeedProgramId);
        Assert.True(Wert(_api.Liste(Neu)).Single(g => g.GrowId == id).Zugewiesen);
    }

    [Fact]
    public void Ein_Grow_mit_Plan_wechselt_ueber_den_Plan_und_Plan_und_Grow_stimmen_danach_ueberein()
    {
        var id = Grow("Läuft", GrowStatus.Running, Alt, mitPlan: true);
        Assert.True(GrowPlanService.HatPlan(id));

        var ergebnis = Zuweisen(Neu, behalten: true, id).Single();

        Assert.True(ergebnis.Umgestellt);
        Assert.Equal(Neu, _grows.GetGrow(id)!.FeedProgramId);
        // Der Plan gewinnt beim Lesen — stünde dort noch das alte Programm, liefen Zielwerte und Alarme nach Programm A.
        Assert.Equal(Neu, GrowPlanRegister.Programm(id)?.Id);
    }

    [Fact]
    public void Ein_zweites_Zuweisen_desselben_Programms_aendert_nichts_und_sagt_es()
    {
        var id = Grow("Geplant", GrowStatus.Planning, Neu);

        var ergebnis = Zuweisen(Neu, true, id).Single();

        Assert.False(ergebnis.Umgestellt);
        Assert.Equal("Hat dieses Programm schon.", ergebnis.Hinweis);
    }

    [Fact]
    public void Ein_abgeschlossener_oder_unbekannter_Grow_wird_abgelehnt_und_stoppt_die_uebrigen_nicht()
    {
        var fertig = Grow("Fertig", GrowStatus.Completed, Alt);
        var geplant = Grow("Geplant", GrowStatus.Planning, null);

        var ergebnisse = Zuweisen(Neu, true, fertig, 987_654, geplant);

        Assert.False(ergebnisse.Single(e => e.GrowId == fertig).Umgestellt);
        Assert.False(ergebnisse.Single(e => e.GrowId == 987_654).Umgestellt);
        Assert.True(ergebnisse.Single(e => e.GrowId == geplant).Umgestellt);
        Assert.Equal(Alt, _grows.GetGrow(fertig)!.FeedProgramId);
    }

    [Fact]
    public void Ohne_Grow_gibt_es_einen_Fehler_und_ein_unbekanntes_Programm_ist_nicht_gefunden()
    {
        Assert.IsType<BadRequestObjectResult>(
            _api.Zuweisen(Neu, new ProgrammZuweisenRequest(), CancellationToken.None).GetAwaiter().GetResult().Result);
        Assert.IsType<NotFoundObjectResult>(_api.Liste("gibt-es-nicht").Result);
        Assert.IsType<NotFoundObjectResult>(
            _api.Zuweisen("gibt-es-nicht", new ProgrammZuweisenRequest { GrowIds = [1] }, CancellationToken.None).GetAwaiter().GetResult().Result);
    }

    [Fact]
    public void Die_Liste_nennt_Zelt_Plan_und_heutiges_Programm_je_Grow()
    {
        var mitPlan = Grow("Mit Plan", GrowStatus.Running, Alt, mitPlan: true);
        var ohnePlan = Grow("Ohne Plan", GrowStatus.Planning, null);

        var liste = Wert(_api.Liste(Neu));

        var a = liste.Single(g => g.GrowId == mitPlan);
        Assert.True(a.HatPlan);
        Assert.Equal("Running", a.Status);
        Assert.False(string.IsNullOrWhiteSpace(a.AktuellesProgramm));
        Assert.NotEqual(Neu, a.AktuellesProgramm);
        var b = liste.Single(g => g.GrowId == ohnePlan);
        Assert.False(b.HatPlan);
        Assert.Null(b.AktuellesProgramm);
        Assert.Equal(0, b.EigeneAenderungen);
    }

    [Fact]
    public void Bei_einem_Grow_mit_Plan_zaehlt_das_Programm_des_Plans_nicht_das_Feld_am_Grow()
    {
        var id = Grow("Läuft", GrowStatus.Running, Alt, mitPlan: true);
        // Das Feld am Grow läuft vom Plan weg (Plan: Athena). Der Plan gewinnt beim Lesen — zugewiesen ist daher NICHT das Feld-Programm.
        var grow = _grows.GetGrow(id)!;
        grow.FeedProgramId = Neu;
        _grows.UpdateGrow(grow);

        Assert.False(Wert(_api.Liste(Neu)).Single(g => g.GrowId == id).Zugewiesen);

        var ergebnis = Zuweisen(Neu, true, id).Single();

        Assert.True(ergebnis.Umgestellt);
        Assert.Equal(Neu, GrowPlanRegister.Programm(id)?.Id);
    }

    private static void KopiereWissen(string wurzel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) dir = Path.GetDirectoryName(dir);
        var quelle = Path.Combine(dir ?? throw new InvalidOperationException("Projektwurzel nicht gefunden."),
            "GrowDiary.Web", "wwwroot", "knowledge-defaults");
        var ziel = Path.Combine(wurzel, "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(ziel, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
    }
}
