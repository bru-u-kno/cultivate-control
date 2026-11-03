using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// „Zelt ruhend“ je Zelt (Etappe 2, Release 2): jedes Zelt hat seinen eigenen Zustand, seine eigenen Geräte und seine eigenen Alarme.
/// Das Zelt der Steuerung behält den Zustand unter dem alten Modul.
/// </summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class ZeltRuheJeZeltTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;
    private readonly TentRepository _zelte;
    private readonly Co2Zelte.Stand _registerVorher = Co2Zelte.Aktuell;
    private readonly int _zelt1;
    private readonly int _zelt2;

    public ZeltRuheJeZeltTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "RuheJeZelt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        _pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        _zelte = new TentRepository(_pfade);
        var z1 = _zelte.GetTents().First();
        z1.Name = "Zelt A";
        _zelte.UpdateTent(z1);
        _zelt1 = z1.Id;
        _zelt2 = _zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        Co2Zelte.Festlegen(null, []);
    }

    public void Dispose()
    {
        Co2Zelte.Festlegen(_registerVorher.BestandZeltId, _registerVorher.ZeltIds);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    [Fact]
    public void ZeltDerSteuerung_BehaeltDasAlteModul_JedesAndereBekommtEigenes()
    {
        Assert.Equal("zeltruhe", ZeltRuheService.ModulFuer(null, 1));
        Assert.Equal("zeltruhe", ZeltRuheService.ModulFuer(1, 1));
        Assert.Equal("zeltruhe-z2", ZeltRuheService.ModulFuer(2, 1));
        Assert.Equal("zeltruhe-z2", ZeltRuheService.ModulFuer(2, null));
    }

    [Fact]
    public void Alarme_SchweigenNurImRuhendenZelt()
    {
        var grows = new GrowRepository(_pfade);
        grows.CreateGrow(new GrowRun { Name = "Grow A", TentId = _zelt1, StartDate = DateTime.Today, Status = GrowStatus.Running });
        var repo = new SteuerungRepository(_pfade);

        // Zelt B (nicht das Zelt der Steuerung) ruht: nur seine Alarme schweigen.
        repo.SetEinstellungen(ZeltRuheService.ModulFuer(_zelt2, _zelt1), new ZeltRuheEinstellungen { Ruhend = true });
        Assert.True(ZeltRuheService.IstRuhend(repo, grows, _zelt2));
        Assert.False(ZeltRuheService.IstRuhend(repo, grows, _zelt1));

        // Zelt A (das Zelt der Steuerung) ruht unter dem alten Modul: Zelt B bleibt davon unberührt.
        repo.SetEinstellungen(ZeltRuheService.Modul, new ZeltRuheEinstellungen { Ruhend = true });
        Assert.True(ZeltRuheService.IstRuhend(repo, grows, _zelt1));
        repo.SetEinstellungen(ZeltRuheService.ModulFuer(_zelt2, _zelt1), new ZeltRuheEinstellungen { Ruhend = false });
        Assert.False(ZeltRuheService.IstRuhend(repo, grows, _zelt2));
    }

    private ZeltRuheService Dienst(bool mitZuweisung, out SteuerungZeltService zuweisung)
    {
        var steuerung = new SteuerungRepository(_pfade);
        var grows = new GrowRepository(_pfade);
        grows.CreateGrow(new GrowRun { Name = "Grow A", TentId = _zelt1, StartDate = DateTime.Today, Status = GrowStatus.Running });
        zuweisung = new SteuerungZeltService(steuerung, _zelte, grows);
        var ha = new HomeAssistantService(new StubHttpClientFactory(new RecordingHttpHandler((_, _) => RecordingHttpHandler.Json("[]"))), NullLogger<HomeAssistantService>.Instance);
        return new ZeltRuheService(steuerung, new SteuerungGeraeteService(steuerung), ha, new HomeAssistantSettingsRepository(_pfade), grows,
            new NichtsWerkzeug(), NullLogger<ZeltRuheService>.Instance, mitZuweisung ? zuweisung : null);
    }

    [Fact]
    public void Geraete_GehoerenDemZeltDerSteuerung_WennNichtsZugewiesenIst()
    {
        var dienst = Dienst(mitZuweisung: false, out _);
        Assert.Equal(ZeltRuheArten.Alle.Count, dienst.ArtenFuer(null).Count); // ohne Zeltangabe alles wie bisher
        Assert.Equal(ZeltRuheArten.Alle.Count, dienst.ArtenFuer(_zelt1).Count); // das Zelt der Steuerung
        Assert.Empty(dienst.ArtenFuer(_zelt2));
    }

    [Fact]
    public void Geraete_FolgenDerZuweisungJeZelt()
    {
        var dienst = Dienst(mitZuweisung: true, out var zuweisung);
        Assert.Null(zuweisung.Speichern(new Dictionary<string, int> { ["chiller"] = _zelt2 }));

        Assert.Equal(ZeltRuheArten.Alle.Count, dienst.ArtenFuer(null).Count); // alte Aufrufer sehen weiter alles
        var a = dienst.ArtenFuer(_zelt1).Select(x => x.Kennung).ToList();
        var b = dienst.ArtenFuer(_zelt2).Select(x => x.Kennung).ToList();

        Assert.DoesNotContain(ZeltRuheArten.Chiller, a);
        Assert.Contains(ZeltRuheArten.Licht, a);
        Assert.Contains(ZeltRuheArten.Entfeuchter, a);
        Assert.Equal([ZeltRuheArten.Chiller], b);
    }

    [Fact]
    public void KuehlerRegler_SchaltetNurDasGenannteZeltAus()
    {
        foreach (var id in new[] { _zelt1, _zelt2 })
        {
            var z = _zelte.GetTent(id)!;
            z.ChillerControlEnabled = true;
            _zelte.UpdateTent(z);
        }
        var regler = new ChillerReglerImZelt(_zelte);

        var betroffen = regler.Ausschalten(_zelt2);

        Assert.Equal([_zelt2], betroffen);
        Assert.True(_zelte.GetTent(_zelt1)!.ChillerControlEnabled);
        Assert.False(_zelte.GetTent(_zelt2)!.ChillerControlEnabled);
        Assert.Equal([_zelt1], regler.Ausschalten()); // ohne Zelt wie bisher: alle, die noch an sind
    }

    private sealed class NichtsWerkzeug : IZeltRuheWerkzeug
    {
        public Task<string?> ZustandAsync(string entity, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<Schaltausgang> SchaltenAsync(string entity, (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten) aktion, string? soll, CancellationToken ct) => Task.FromResult(Schaltausgang.Abgelehnt);
        public Task<(bool Ok, bool? WarAn)> AutomatikAusAsync(string kennung, int? zeltId, CancellationToken ct) => Task.FromResult<(bool, bool?)>((true, null));
        public Task<bool> AutomatikAnAsync(string kennung, int? zeltId, CancellationToken ct) => Task.FromResult(true);
        public IReadOnlyList<int> KuehlerReglerAus(int? zeltId) => [];
        public void KuehlerReglerAn(IEnumerable<int> zeltIds) { }
        public void WaechterPausierenBis(DateTime bisUtc) { }
        public void WaechterFortsetzen() { }
    }
}
