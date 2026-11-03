using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.KiZugriff;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Tests;

/// <summary>
/// Probelauf je Zelt (Etappe 2, Release 4): Ein Probelauf misst das Zelt der Steuerung, an der er eingreift — nie die Werte eines
/// anderen Zeltes — und schaltet nur den Kühler-Regler dieses Zeltes ab.
/// </summary>
public sealed class ProbelaufJeZeltTests : IDisposable
{
    private readonly string _ordner;
    private readonly AppPaths _pfade;
    private readonly VerstellbareUhr _uhr = new();
    private readonly FakeProbelaufHa _ha = new();
    private readonly FakeChillerRegler _chiller = new();
    private readonly FakeProbelaufMessung _messung = new();
    private readonly FakeProbelaufMeldung _meldung = new();
    private readonly ProbelaufRepository _repo;
    private readonly SteuerungZeltService _zuweisung;
    private readonly int _zelt1;
    private readonly int _zelt2;

    public ProbelaufJeZeltTests()
    {
        _ordner = Path.Combine(Path.GetTempPath(), "ProbelaufZelt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_ordner);
        _pfade = new AppPaths(_ordner);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        _repo = new ProbelaufRepository(_pfade);
        var zelte = new TentRepository(_pfade);
        _zelt1 = zelte.GetTents().First().Id;
        _zelt2 = zelte.CreateTent(new Tent { Name = "Zelt B", WidthCm = 120, DepthCm = 120, TentHeightCm = 200 }).Id;
        _zuweisung = new SteuerungZeltService(new SteuerungRepository(_pfade), zelte, new GrowRepository(_pfade));

        _ha.Regelungen.Add("automation.water_chiller_regelung");
        _ha.Zustaende["automation.water_chiller_regelung"] = "on";
        _ha.Geraete["steckdose"] = "switch.kuehler";
        _ha.Zustaende["switch.kuehler"] = "on";
        _messung.Aktuell = new ProbelaufMesswerte(_uhr.Jetzt.UtcDateTime, 55, 25, 1.2);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_ordner, recursive: true); } catch { }
    }

    private ProbelaufService Dienst(bool mitZuweisung = true)
        => new(_repo, new ProbelaufEingriff(_ha, _chiller), _messung, _meldung, _uhr, mitZuweisung ? _zuweisung : null);

    // ------------------------------------------------------------------ Wahl des Fühler-Moduls

    [Fact]
    public void Gemessen_wird_der_Entfeuchter_im_Zelt_der_Steuerung()
    {
        int? Zelt(string modul) => modul switch { "entfeuchter" => 1, "entfeuchter-i2" => 2, _ => null };

        Assert.Equal("entfeuchter", ProbelaufMessung.FuehlerModulFuer(1, Zelt, ["entfeuchter-i2"]));
        Assert.Equal("entfeuchter-i2", ProbelaufMessung.FuehlerModulFuer(2, Zelt, ["entfeuchter-i2"]));
        Assert.Null(ProbelaufMessung.FuehlerModulFuer(3, Zelt, ["entfeuchter-i2"])); // kein Entfeuchter im Zelt: keine Messung, nie fremde Werte
        Assert.Null(ProbelaufMessung.FuehlerModulFuer(2, Zelt, [])); // die Instanz gibt es (noch) nicht
    }

    // ------------------------------------------------------------------ Lauf je Zelt

    [Fact]
    public async Task Der_Lauf_misst_und_greift_im_Zelt_der_Steuerung_ein()
    {
        Assert.Null(_zuweisung.Speichern(new Dictionary<string, int> { ["chiller"] = _zelt2 }));

        var e = await Dienst().StartenAsync(new ProbelaufStart("chiller", 10, null), default);

        Assert.Equal(ProbelaufFehler.Keiner, e.Fehler);
        Assert.Equal(_zelt2, ProbelaufEingriff.ZeltIdAus(e.Lauf!.Ausgangszustand));
        Assert.All(_messung.GemesseneZelte, z => Assert.Equal(_zelt2, z));
        Assert.Equal([(int?)_zelt2], _chiller.AusgeschalteteZelte); // nur der Regler dieses Zeltes

        // Auch der nächste Takt misst dieses Zelt.
        _messung.GemesseneZelte.Clear();
        _uhr.Jetzt += TimeSpan.FromSeconds(30);
        await Dienst().TickAsync(default);
        Assert.NotEmpty(_messung.GemesseneZelte);
        Assert.All(_messung.GemesseneZelte, z => Assert.Equal(_zelt2, z));
    }

    [Fact]
    public async Task Mit_einem_Zelt_oder_ohne_Zuweisung_gilt_alles_wie_bisher()
    {
        var e = await Dienst(mitZuweisung: false).StartenAsync(new ProbelaufStart("chiller", 10, null), default);

        Assert.Equal(ProbelaufFehler.Keiner, e.Fehler);
        Assert.Null(ProbelaufEingriff.ZeltIdAus(e.Lauf!.Ausgangszustand));
        Assert.All(_messung.GemesseneZelte, z => Assert.Null(z));
        Assert.Equal([(int?)null], _chiller.AusgeschalteteZelte);
    }

    [Fact]
    public async Task Ohne_Messung_im_Zelt_startet_kein_Lauf_und_die_Meldung_nennt_das_Zelt()
    {
        Assert.Null(_zuweisung.Speichern(new Dictionary<string, int> { ["chiller"] = _zelt2 }));
        _messung.Aktuell = null;

        var e = await Dienst().StartenAsync(new ProbelaufStart("chiller", 10, null), default);

        Assert.Equal(ProbelaufFehler.KeineMessung, e.Fehler);
        Assert.Contains("dieses Zelt", e.Meldung);
        Assert.Empty(_repo.Offene());
        Assert.Empty(_chiller.AusgeschalteteZelte); // nichts angefasst
    }

    // ------------------------------------------------------------------ Module und Wächter

    [Theory]
    [InlineData("entfeuchter", true)]
    [InlineData("chiller-i2", true)]
    [InlineData("zuluft-i3", true)]
    [InlineData("entfeuchter-zusatz-i2", true)]
    [InlineData("co2-z2", false)]
    [InlineData("raum-i2", false)]
    [InlineData("licht-i2", false)] // die Lampe hat keinen Probelauf
    public void Der_Probelauf_kennt_Instanzen_ihrer_Arten(string modul, bool bekannt)
        => Assert.Equal(bekannt, ProbelaufEingriff.KenntModul(modul));

    private static ProbelaufLauf Lauf(string modul, int? zelt, ProbelaufStatus status = ProbelaufStatus.Laeuft)
        => new() { Modul = modul, Status = status, Ausgangszustand = zelt is null ? "{}" : $"{{\"zeltId\":{zelt}}}" };

    [Fact]
    public void Der_Kuehler_steht_nur_im_Zelt_des_Laufs_absichtlich_aus()
    {
        var offene = new[] { Lauf("chiller", 2) };
        Assert.True(ProbelaufService.KuehlerAbsichtlichAus(offene, 2));
        Assert.False(ProbelaufService.KuehlerAbsichtlichAus(offene, 1)); // der Kühler in Zelt 1 darf gemeldet werden
        Assert.True(ProbelaufService.KuehlerAbsichtlichAus(offene)); // ohne Zelt wie bisher
    }

    [Fact]
    public void Ein_Lauf_ohne_Zelt_gilt_fuer_alle_Zelte_und_eine_Instanz_zaehlt_als_Kuehler()
    {
        Assert.True(ProbelaufService.KuehlerAbsichtlichAus([Lauf("chiller", null)], 1));
        Assert.True(ProbelaufService.KuehlerAbsichtlichAus([Lauf("chiller-i2", 2)], 2));
        Assert.False(ProbelaufService.KuehlerAbsichtlichAus([Lauf("entfeuchter", 2)], 2));
        Assert.False(ProbelaufService.KuehlerAbsichtlichAus([Lauf("chiller", 2, ProbelaufStatus.Nachlauf)], 2)); // im Nachlauf nicht mehr
    }

    [Fact]
    public void Aeltere_Laeufe_ohne_Zelt_im_Ausgangszustand_bleiben_lesbar()
    {
        Assert.Null(ProbelaufEingriff.ZeltIdAus("{}"));
        Assert.Null(ProbelaufEingriff.ZeltIdAus("{\"automationen\":[],\"geraete\":[],\"chillerZelte\":[]}"));
        Assert.Null(ProbelaufEingriff.ZeltIdAus("kein json"));
        Assert.Equal(3, ProbelaufEingriff.ZeltIdAus("{\"zeltId\":3}"));
    }
}
