using GrowDiary.Web.Api.Contracts;
using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Der Abschluss eines Grows — auf jedem Weg stehen seine Pflanzen danach auf „geerntet".
/// </summary>
/// <remarks>
/// <para><b>Anlass (31.10.2026).</b> Der Nutzer hatte seinen Grow über den Status abgeschlossen und die Pflanzen standen auf der
/// Zeltseite weiter auf „aktiv". Der Status „geerntet" war in der Auswahl und in der Übersetzung vorhanden, gesetzt hat ihn aber
/// nirgends etwas. Es gibt zwei Wege zum Abschluss (<c>POST …/archive</c>, das die Ernte-Seite ruft, und <c>PUT …</c> mit Status
/// „Abgeschlossen" aus dem Formular); beide gehören gefahren, sonst repariert man einen und der Nutzer nimmt den anderen.</para>
/// </remarks>
public sealed class GrowAbschlussTests : IDisposable
{
    private readonly string _temp;
    private readonly GrowRepository _repository;
    private readonly SetupRepository _setups;
    private readonly GrowsApiController _controller;
    private readonly Tent _tent;

    public GrowAbschlussTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "GrowAbschluss_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var paths = new AppPaths(_temp);
        _tent = TestDatabase.InitializeWithDefaultTent(paths);
        _repository = new GrowRepository(paths);
        _setups = new SetupRepository(paths);

        var loader = new KnowledgeBaseLoader(paths, NullLogger<KnowledgeBaseLoader>.Instance);
        loader.Initialize();
        _controller = new GrowsApiController(
            _repository,
            new AuditRepository(paths),
            new DeviationAnalyzerService(new TargetValueService(loader)),
            new TreatmentRecommender(loader),
            _setups,
            new HydroSetupRepository(paths, new TentRepository(paths)),
            new SystemAuditRepository(paths));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    private int GrowMitPflanzen()
    {
        var growId = _repository.CreateGrow(new GrowRun
        {
            TentId = _tent.Id, Name = "Lauf", StartDate = new DateTime(2026, 5, 1), Status = GrowStatus.Running,
        });
        foreach (var (label, status) in new[]
        {
            ("Pflanze 1", PlantStatus.Active), ("Pflanze 2", PlantStatus.Active),
            ("Aussortiert", PlantStatus.Culled), ("Geplant", PlantStatus.Planned),
        })
        {
            _setups.CreatePlant(new PlantInstance { Label = label, GrowId = growId, PlantRole = PlantRole.Production, PlantStatus = status });
        }
        return growId;
    }

    private Dictionary<string, PlantStatus> Stati(int growId)
        => _setups.GetPlantsByGrow(growId).ToDictionary(p => p.Label, p => p.PlantStatus);

    private static GrowUpsertRequest Anfrage(GrowStatus status) => new()
    {
        Name = "Lauf",
        StartDate = "2026-05-01",
        Status = status,
        SeedType = SeedType.Feminized,
        EntryPoint = GrowEntryPoint.Germination,
    };

    [Fact]
    public void Archivieren_stellt_die_aktiven_Pflanzen_auf_geerntet_und_fasst_andere_nicht_an()
    {
        var id = GrowMitPflanzen();
        Assert.Equal(PlantStatus.Active, Stati(id)["Pflanze 1"]);

        _controller.Archive(id);

        var stati = Stati(id);
        Assert.Equal(PlantStatus.Harvested, stati["Pflanze 1"]);
        Assert.Equal(PlantStatus.Harvested, stati["Pflanze 2"]);
        // Aussortiert bleibt aussortiert, geplant bleibt geplant: „geerntet" gilt nur für Pflanzen, die wuchsen.
        Assert.Equal(PlantStatus.Culled, stati["Aussortiert"]);
        Assert.Equal(PlantStatus.Planned, stati["Geplant"]);
        Assert.All(_setups.GetPlantsByGrow(id).Where(p => p.PlantStatus == PlantStatus.Harvested), p => Assert.NotNull(p.EndedAt));
    }

    [Fact]
    public void Der_Status_Abgeschlossen_im_Formular_setzt_die_Pflanzen_ebenfalls_auf_geerntet()
    {
        // Der Weg des Nutzers vom 30.10.: nicht „Ernte speichern", sondern der Status im Formular.
        var id = GrowMitPflanzen();

        _controller.Update(id, Anfrage(GrowStatus.Completed));

        Assert.Equal(GrowStatus.Completed, _repository.GetGrow(id)!.Status);
        Assert.Equal(PlantStatus.Harvested, Stati(id)["Pflanze 1"]);
        Assert.Equal(PlantStatus.Harvested, Stati(id)["Pflanze 2"]);
    }

    [Fact]
    public void Solange_der_Grow_laeuft_aendert_Speichern_den_Pflanzenstatus_nicht()
    {
        var id = GrowMitPflanzen();

        _controller.Update(id, Anfrage(GrowStatus.Running));

        Assert.Equal(PlantStatus.Active, Stati(id)["Pflanze 1"]);
    }

    [Fact]
    public void Ein_zweites_Speichern_eines_abgeschlossenen_Grows_stellt_nichts_neu()
    {
        var id = GrowMitPflanzen();
        _controller.Update(id, Anfrage(GrowStatus.Completed));
        // Von Hand zurück auf „aktiv" gestellt (etwa weil eine Pflanze doch weiterwächst) — ein weiteres Speichern des
        // schon abgeschlossenen Grows darf das nicht wieder umstoßen.
        var pflanze = _setups.GetPlantsByGrow(id).First(p => p.Label == "Pflanze 1");
        pflanze.PlantStatus = PlantStatus.Active;
        _setups.UpdatePlant(pflanze);

        _controller.Update(id, Anfrage(GrowStatus.Completed));

        Assert.Equal(PlantStatus.Active, Stati(id)["Pflanze 1"]);
    }

    [Fact]
    public void Eine_Mutterpflanze_am_Grow_waechst_nach_der_Ernte_weiter()
    {
        var id = GrowMitPflanzen();
        _setups.CreatePlant(new PlantInstance { Label = "Mutter", GrowId = id, PlantRole = PlantRole.Mother, PlantStatus = PlantStatus.Active });

        _controller.Archive(id);

        Assert.Equal(PlantStatus.Active, Stati(id)["Mutter"]);
        Assert.Equal(PlantStatus.Harvested, Stati(id)["Pflanze 1"]);
    }

    [Fact]
    public void Das_Enddatum_der_Pflanzen_ist_das_des_Grows()
    {
        var id = GrowMitPflanzen();
        var grow = _repository.GetGrow(id)!;
        grow.Status = GrowStatus.Completed;
        grow.EndDate = new DateTime(2026, 10, 30);
        _repository.UpdateGrow(grow);

        GrowAbschluss.PflanzenAlsGeerntet(_setups, id, grow.EndDate.Value);

        Assert.All(_setups.GetPlantsByGrow(id).Where(p => p.PlantStatus == PlantStatus.Harvested),
            p => Assert.Equal(new DateTime(2026, 10, 30), p.EndedAt!.Value.Date));
    }

    [Fact]
    public void Die_Korrektur_beim_Start_stellt_Pflanzen_abgeschlossener_Grows_um_und_nur_diese()
    {
        // Der Bestand vom 30.10.: der Grow ist abgeschlossen, die Pflanzen stehen noch „aktiv“ — so entstand es bis cc.293.
        var fertig = GrowMitPflanzen();
        var fertigGrow = _repository.GetGrow(fertig)!;
        fertigGrow.Status = GrowStatus.Completed;
        fertigGrow.EndDate = new DateTime(2026, 10, 30);
        _repository.UpdateGrow(fertigGrow);

        var laeuft = GrowMitPflanzen();
        var abgebrochen = GrowMitPflanzen();
        var abgebrochenGrow = _repository.GetGrow(abgebrochen)!;
        abgebrochenGrow.Status = GrowStatus.Aborted;
        _repository.UpdateGrow(abgebrochenGrow);

        var umgestellt = GrowAbschluss.BestandNachziehen(_setups, _repository);

        Assert.Equal(2, umgestellt);
        Assert.Equal(PlantStatus.Harvested, Stati(fertig)["Pflanze 1"]);
        Assert.Equal(PlantStatus.Active, Stati(laeuft)["Pflanze 1"]);        // läuft: bleibt
        Assert.Equal(PlantStatus.Active, Stati(abgebrochen)["Pflanze 1"]);   // abgebrochen: bleibt, wie es ist
        // Idempotent: der zweite Start fasst nichts mehr an.
        Assert.Equal(0, GrowAbschluss.BestandNachziehen(_setups, _repository));
    }
}
