using System.Net;
using System.Text.Json.Nodes;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Die Zuluft folgt dem Trocknungsplan (cc.337/cc.338): der Weg von der Entscheidung bis zum Schaltbefehl und zurück an die HA-Automatik.
/// Echter Dienst, echte Datenbank, Home Assistant ist eine Attrappe, die jeden Aufruf mitschreibt.
/// </summary>
public sealed class TrocknungZuluftServiceTests : IDisposable
{
    private const string Automatik = "automation.zuluft_keller_regelung";
    private const string Schalter = "select.air_zuluft_aktiver_modus";
    private const string Stufe = "number.air_zuluft_einschaltleistung";

    private readonly string _wurzel;
    private readonly AppPaths _paths;
    private readonly GrowRepository _grows;
    private readonly HarvestRepository _ernten;
    private readonly TrocknungSteuerungRepository _steuerung;
    private readonly SteuerungRepository _steuerungRepo;
    private readonly Tent _trockenZelt;
    private readonly Dictionary<string, string> _zustaende = new(StringComparer.Ordinal);
    private readonly RecordingHttpHandler _ha;
    private HttpStatusCode _dienstAntwort = HttpStatusCode.OK;

    public TrocknungZuluftServiceTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "TrocknungZuluftDienst_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", Path.Combine(_wurzel, "test.db"));
        _paths = new AppPaths(_wurzel);
        var bluete = TestDatabase.InitializeWithDefaultTent(_paths);
        new HomeAssistantSettingsRepository(_paths).SaveHomeAssistantSettings(new HomeAssistantSettings { BaseUrl = "http://ha.local:8123", AccessToken = "token", Enabled = true });
        _grows = new GrowRepository(_paths);
        _ernten = new HarvestRepository(_paths);
        _steuerung = new TrocknungSteuerungRepository(_paths);
        _steuerungRepo = new SteuerungRepository(_paths);

        _trockenZelt = _grows.CreateTent(new Tent { Name = "Trockenzelt", TentType = TentType.Drying });
        var growId = _grows.CreateGrow(new GrowRun { Name = "Charge A", TentId = bluete.Id, StartDate = DateTime.Today.AddDays(-90), Status = GrowStatus.Running });
        _ernten.Create(new HarvestEntry { GrowId = growId, HarvestedAt = DateTime.Today.AddDays(-1), DryingTentId = _trockenZelt.Id });
        _grows.AddTentSensor(new TentSensor { TentId = _trockenZelt.Id, MetricType = SensorMetricType.AirTemperature, HaEntityId = "sensor.zelt_temp", IsActive = true });
        _grows.AddTentSensor(new TentSensor { TentId = _trockenZelt.Id, MetricType = SensorMetricType.Humidity, HaEntityId = "sensor.zelt_feuchte", IsActive = true });

        // Die Zuordnung der Zuluft-Geräte (wie bei der Anlage) und was die Seite gespeichert hat.
        foreach (var (rolle, entitaet) in new[]
                 {
                     ("aussen_temp", "sensor.air_temperatur"), ("aussen_rh", "sensor.air_luftfeuchtigkeit"), ("port_schalter", Schalter),
                     ("port_zustand", "binary_sensor.air_zuluft_zustand"), ("port_stufe", Stufe), ("port_ist_stufe", "sensor.air_zuluft_aktuelle_leistung"),
                     ("zelt_temp", "sensor.big_probe_sensor_sonden_temperatur"),
                 })
            _steuerungRepo.SetGeraet(ZuluftSteuerungService.Modul, rolle, entitaet);
        Gespeichert(automatik: true);

        // Zelt deutlich feuchter als der Plan, Außenluft warm genug und trocken: Die Zuluft soll saugen.
        Setze("sensor.zelt_temp", "18"); Setze("sensor.zelt_feuchte", "90");
        Setze("sensor.air_temperatur", "15"); Setze("sensor.air_luftfeuchtigkeit", "40");
        Setze("binary_sensor.air_zuluft_zustand", "off"); Setze("sensor.air_zuluft_aktuelle_leistung", "0");
        Setze(Schalter, "Off"); Setze(Stufe, "1"); Setze(Automatik, "on");

        _ha = new RecordingHttpHandler((anfrage, _) =>
        {
            var pfad = anfrage.RequestUri!.AbsolutePath;
            var i = pfad.IndexOf("/api/states/", StringComparison.Ordinal);
            if (anfrage.Method == HttpMethod.Get && i >= 0)
            {
                var id = pfad[(i + "/api/states/".Length)..];
                return _zustaende.TryGetValue(id, out var wert)
                    ? RecordingHttpHandler.Json(RecordingHttpHandler.EntityStateJson(id, wert))
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (anfrage.Method == HttpMethod.Post && pfad.Contains("/api/services/", StringComparison.Ordinal))
                return RecordingHttpHandler.Json("[]", _dienstAntwort);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GROWDIARY_DB_PATH", null);
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private void Setze(string id, string wert) => _zustaende[id] = wert;

    /// <summary>Was die Zuluft-Seite für die Automatik gespeichert hat (ohne gespeicherten Stand gäbe es nichts Eigenes).</summary>
    private void Gespeichert(bool automatik)
        => _steuerungRepo.SetEinstellungen(ZuluftSteuerungService.Modul, new ZuluftEinstellungen { ZeltTemperaturMinC = 14, AussentemperaturMinC = 2, StufeMin = 1, StufeMax = 6, MindestlaufzeitMin = 10, MindestpauseMin = 10, AutomatikAktiv = automatik });

    private TrocknungZuluftService Dienst(ZuluftFuehrung? fuehrung = null)
    {
        var ha = new HomeAssistantService(new StubHttpClientFactory(_ha), NullLogger<HomeAssistantService>.Instance);
        var einstellungen = new HomeAssistantSettingsRepository(_paths);
        var geraete = new SteuerungGeraeteService(_steuerungRepo);
        var zuluft = new ZuluftSteuerungService(_steuerungRepo, ha, einstellungen, geraete, new HelferBereichService(NullLogger<HelferBereichService>.Instance), NullLogger<ZuluftSteuerungService>.Instance);
        var bereiche = new ServiceCollection().AddSingleton(zuluft).AddSingleton(geraete).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new TrocknungZuluftService(_grows, _ernten, new TrocknungPlanQuelle(new KnowledgeBaseLoader(_paths, NullLogger<KnowledgeBaseLoader>.Instance), new TrocknungPlanRepository(_paths)),
            _steuerung, ha, einstellungen, bereiche, fuehrung ?? new ZuluftFuehrung(), NullLogger<TrocknungZuluftService>.Instance);
    }

    private void Einschalten(bool folgt = true, string betrieb = TrocknungSteuerungRepository.BetriebRegeln)
        => _steuerung.Set(_trockenZelt.Id, _steuerung.Get(_trockenZelt.Id) with { Betrieb = betrieb, ZuluftFolgt = folgt, ZuluftHoechststufe = 4, ZuluftAbstandProzent = 3 });

    /// <summary>Die Dienstaufrufe seit dem letzten Aufruf von <see cref="Vergessen"/>: „domain.dienst entity [Daten]".</summary>
    private List<string> Aufrufe() => _ha.Requests
        .Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/api/services/", StringComparison.Ordinal))
        .Select(r =>
        {
            var dienst = r.Uri.AbsolutePath[(r.Uri.AbsolutePath.IndexOf("/api/services/", StringComparison.Ordinal) + "/api/services/".Length)..].Replace('/', '.');
            var daten = r.Body is null ? null : JsonNode.Parse(r.Body);
            var extra = string.Join(' ', (daten?.AsObject() ?? new JsonObject()).Where(p => p.Key != "entity_id").Select(p => $"{p.Key}={p.Value}"));
            return $"{dienst} {daten?["entity_id"]} {extra}".Trim().Replace("\"", "");
        }).ToList();

    private void Vergessen() => _ha.Requests.Clear();

    [Fact]
    public async Task Ohne_Haken_wird_nichts_geschaltet_und_nichts_angefasst()
    {
        var dienst = Dienst();
        Assert.False(await dienst.LaufAsync(_trockenZelt.Id, default));
        Assert.Empty(Aufrufe());
        Assert.Empty(_steuerung.FuehrungZelte());
    }

    [Fact]
    public async Task Mit_Haken_haelt_der_Fork_die_Automatik_an_setzt_die_Stufe_und_schaltet_ein()
    {
        Einschalten();
        var fuehrung = new ZuluftFuehrung();
        var dienst = Dienst(fuehrung);

        Assert.True(await dienst.LaufAsync(_trockenZelt.Id, default));

        var aufrufe = Aufrufe();
        Assert.Contains($"automation.turn_off {Automatik}", aufrufe);
        var stufe = Assert.Single(aufrufe, a => a.StartsWith("number.set_value", StringComparison.Ordinal));
        Assert.Contains(Stufe, stufe);
        var wert = int.Parse(stufe[(stufe.LastIndexOf("value=", StringComparison.Ordinal) + 6)..]);
        Assert.InRange(wert, 1, 4); // nie über der Höchststufe
        Assert.Contains(aufrufe, a => a.StartsWith("select.select_option", StringComparison.Ordinal) && a.Contains("option=On", StringComparison.Ordinal) && a.Contains(Schalter, StringComparison.Ordinal));
        // Erst die Stufe, dann der Schalter.
        Assert.True(aufrufe.FindIndex(a => a.StartsWith("number.set_value", StringComparison.Ordinal)) < aufrufe.FindIndex(a => a.StartsWith("select.select_option", StringComparison.Ordinal)));
        Assert.Equal([_trockenZelt.Id], _steuerung.FuehrungZelte());
        Assert.True(fuehrung.Gefuehrt);
    }

    [Fact]
    public async Task Die_Mindestlaufzeit_haelt_die_Zuluft_an_auch_wenn_die_Entscheidung_Aus_waere()
    {
        Einschalten();
        var dienst = Dienst();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        // Der Lüfter läuft jetzt; draußen wird es plötzlich zu kalt — aber die Mindestlaufzeit (10 min) ist noch nicht um.
        Setze("binary_sensor.air_zuluft_zustand", "on"); Setze(Schalter, "On"); Setze("sensor.air_zuluft_aktuelle_leistung", "3");
        Setze("sensor.air_temperatur", "-3");
        Vergessen();

        Assert.False(await dienst.LaufAsync(_trockenZelt.Id, default));

        Assert.DoesNotContain(Aufrufe(), a => a.StartsWith("select.select_option", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Laeuft_die_Zuluft_auf_falscher_Stufe_fuehrt_der_Fork_sie_nach_ohne_den_Modus_anzufassen()
    {
        Einschalten();
        var dienst = Dienst();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        var gewollt = (await dienst.StandAsync(_trockenZelt.Id, default)).Stufe;
        Setze("binary_sensor.air_zuluft_zustand", "on"); Setze(Schalter, "On");
        Setze("sensor.air_zuluft_aktuelle_leistung", gewollt == 1 ? "2" : "1");
        Vergessen();

        await dienst.LaufAsync(_trockenZelt.Id, default);

        var aufrufe = Aufrufe();
        Assert.Contains(aufrufe, a => a.StartsWith("number.set_value", StringComparison.Ordinal) && a.Contains($"value={gewollt}", StringComparison.Ordinal));
        Assert.DoesNotContain(aufrufe, a => a.StartsWith("select.select_option", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wird_der_Haken_geloescht_geht_die_Zuluft_zurueck_an_die_Regelung_wie_die_Seite_es_will()
    {
        Einschalten();
        var fuehrung = new ZuluftFuehrung();
        var dienst = Dienst(fuehrung);
        await dienst.LaufAsync(_trockenZelt.Id, default);
        Setze(Automatik, "off"); // der Fork hat sie angehalten
        Einschalten(folgt: false);
        Vergessen();

        await dienst.LaufAsync(_trockenZelt.Id, default);

        Assert.Equal(["automation.turn_on " + Automatik], Aufrufe().ToList());
        Assert.Empty(_steuerung.FuehrungZelte());
        Assert.False(fuehrung.Gefuehrt);
        // Ein weiterer Lauf fasst nichts mehr an.
        Vergessen();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        Assert.Empty(Aufrufe());
    }

    [Fact]
    public async Task Hat_die_Seite_die_Automatik_aus_gespeichert_bleibt_sie_nach_der_Rueckgabe_aus()
    {
        Einschalten();
        var dienst = Dienst();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        Gespeichert(automatik: false);
        Einschalten(folgt: false);
        Vergessen();

        await dienst.LaufAsync(_trockenZelt.Id, default);

        Assert.Equal(["automation.turn_off " + Automatik], Aufrufe().ToList());
    }

    [Fact]
    public async Task Auch_nach_einem_Neustart_gibt_der_Fork_die_Zuluft_zurueck()
    {
        Einschalten();
        await Dienst().LaufAsync(_trockenZelt.Id, default);
        Setze(Automatik, "off");

        // Neustart des Add-ons: neuer Dienst, nur die Datenbank bleibt. Die Trocknung endet, bevor der Fork wieder läuft.
        var fuehrung = new ZuluftFuehrung();
        var neu = Dienst(fuehrung);
        Assert.True(fuehrung.Gefuehrt);
        Assert.Equal([_trockenZelt.Id], neu.GefuehrteZelte());
        Einschalten(folgt: false, betrieb: TrocknungSteuerungRepository.BetriebMelden);
        Vergessen();

        await neu.LaufAsync(_trockenZelt.Id, default);

        Assert.Equal(["automation.turn_on " + Automatik], Aufrufe().ToList());
        Assert.Empty(neu.GefuehrteZelte());
    }

    [Fact]
    public async Task Lehnt_Home_Assistant_die_Rueckgabe_ab_bleibt_die_Fuehrung_und_der_naechste_Lauf_versucht_es_wieder()
    {
        Einschalten();
        var dienst = Dienst();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        Einschalten(folgt: false);
        _dienstAntwort = HttpStatusCode.InternalServerError;

        await dienst.LaufAsync(_trockenZelt.Id, default);
        Assert.Equal([_trockenZelt.Id], _steuerung.FuehrungZelte());

        _dienstAntwort = HttpStatusCode.OK;
        Vergessen();
        await dienst.LaufAsync(_trockenZelt.Id, default);
        Assert.Empty(_steuerung.FuehrungZelte());
        Assert.Contains(Aufrufe(), a => a == "automation.turn_on " + Automatik);
    }

    [Fact]
    public async Task Der_Zustand_vor_der_Uebernahme_zaehlt_wenn_die_Seite_nie_gespeichert_hat()
    {
        // Kein gespeicherter Stand und die Automatik stand VOR der Übernahme auf aus: Sie bleibt nach der Rückgabe aus.
        _steuerungRepo.SetEinstellungen(ZuluftSteuerungService.Modul, new ZuluftEinstellungen()); // ungültig (ohne Zelttemperatur) = nie gespeichert
        Setze(Automatik, "off");
        Setze("sensor.big_probe_sensor_sonden_temperatur", "19");
        foreach (var id in new[] { "input_number.zuluft_mindest_differenz", "input_number.zuluft_abschalt_abstand", "input_number.zuluft_aussentemperatur_min", "input_number.zuluft_zelttemperatur_min", "input_number.zuluft_stufe_min", "input_number.zuluft_stufe_max", "input_number.zuluft_mindestlaufzeit", "input_number.zuluft_mindestpause" })
            Setze(id, "5");
        var wert = _steuerung.FuehrungLesen(_trockenZelt.Id);
        Assert.Null(wert);
        _steuerung.FuehrungBeginnen(_trockenZelt.Id, false);
        Assert.Equal(false, _steuerung.FuehrungLesen(_trockenZelt.Id)!.AutomatikVorher);
        // Ein zweites Beginnen überschreibt den Stand von VOR der ersten Übernahme nicht.
        _steuerung.FuehrungBeginnen(_trockenZelt.Id, true);
        Assert.Equal(false, _steuerung.FuehrungLesen(_trockenZelt.Id)!.AutomatikVorher);
    }
}
