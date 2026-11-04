using System.Net;
using System.Text;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Etappe 2, Release 5c: Beim Löschen einer Instanz auf Wunsch auch ihre Bausteine aus Home Assistant entfernen. Der Plan ist rein
/// und beweist, dass nie ein Baustein der ersten Steuerung darin steht; die Ausführung läuft gegen eine nachgestellte Home-Assistant-
/// Schnittstelle.
/// </summary>
[Collection(InstanzRegisterCollection.Name)]
public sealed class SteuerungInstanzAufraeumenTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;

    public SteuerungInstanzAufraeumenTests()
    {
        SteuerungInstanzen.Festlegen([new SteuerungInstanz { Art = "chiller", Nummer = 2, Name = "Reserve" }]);
        _wurzel = Path.Combine(Path.GetTempPath(), "InstanzAufraeumen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        _pfade = new AppPaths(_wurzel);
    }

    public void Dispose()
    {
        SteuerungInstanzen.Festlegen([]);
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    // ------------------------------------------------------------------ Plan

    [Theory]
    [InlineData("chiller")]
    [InlineData("entfeuchter")]
    [InlineData("co2")]
    [InlineData("co2-z2")]
    [InlineData("zuluft")]
    [InlineData("")]
    [InlineData("chiller-i1")]
    [InlineData("gibt-es-nicht")]
    public void FuerKeineWeitereInstanzGibtEsNichtsZuEntfernen(string modul)
        => Assert.Empty(SteuerungInstanzAufraeumenService.Planen(modul));

    [Theory]
    [InlineData("chiller", 2)]
    [InlineData("chiller", 7)]
    [InlineData("entfeuchter", 2)]
    [InlineData("entfeuchter-zusatz", 3)]
    [InlineData("zuluft", 2)]
    public void DerPlanEnthaeltNurKennungenDerInstanz(string art, int nummer)
    {
        var modul = InstanzModul.Fuer(art, nummer);
        var plan = SteuerungInstanzAufraeumenService.Planen(modul);

        // Mengenwächter: ohne Treffer liefe die Prüfung null Mal.
        Assert.True(plan.Count >= 3, $"{modul}: nur {plan.Count} Bausteine im Plan.");
        var basis = SteuerungBauteile.FuerModul(art).Select(b => b.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var b in plan.Where(p => p.Art != "automation"))
        {
            Assert.EndsWith($"_i{nummer}", b.Kennung);
            Assert.DoesNotContain(b.Kennung, basis); // nie eine Kennung der ersten Instanz
        }
        foreach (var a in plan.Where(p => p.Art == "automation")) Assert.StartsWith($"fork_ai_{modul}_", a.Kennung);
        // Reihenfolge: Automationen, dann Rechenwerte, dann Helfer.
        Assert.Equal(plan.OrderBy(p => p.Art switch { "automation" => 0, "rechenwert" => 1, _ => 2 }).Select(p => p.Kennung), plan.Select(p => p.Kennung));
    }

    [Fact]
    public void DerPlanDesWasserkuehlersNenntSeineVierAutomationenUndAlleHelfer()
    {
        var plan = SteuerungInstanzAufraeumenService.Planen("chiller-i2");

        Assert.Equal(
            ["fork_ai_chiller-i2_merker", "fork_ai_chiller-i2_regelung", "fork_ai_chiller-i2_sollwert", "fork_ai_chiller-i2_waechter"],
            plan.Where(p => p.Art == "automation").Select(p => p.Kennung).ToList());
        Assert.Contains(plan, p => p is { Art: "helfer", Kennung: "input_number.chiller_hysterese_i2" });
        Assert.Contains(plan, p => p is { Art: "rechenwert", Kennung: "binary_sensor.chiller_kuhlbedarf_i2" });
        Assert.DoesNotContain(plan, p => p.Kennung == "input_number.chiller_hysterese");
    }

    // ------------------------------------------------------------------ Ausführung

    private sealed record Lauf(SteuerungInstanzAufraeumenService Dienst, RecordingHttpHandler Handler, List<string> Helfer, HomeAssistantSettings Settings);

    private Lauf Aufbau(Func<string, HttpRequestMessage, HttpResponseMessage?> antwort, bool stumm = false, bool helferScheitern = false)
    {
        var plan = SteuerungInstanzAufraeumenService.Planen("chiller-i2");
        var zustaende = stumm
            ? "[]"
            : "[" + string.Join(",", plan.Where(p => p.Art != "automation").Select(p => RecordingHttpHandler.EntityStateJson(p.Kennung, "1"))
                .Append(RecordingHttpHandler.EntityStateJson("input_number.chiller_hysterese", "0.3"))) + "]";
        var handler = new RecordingHttpHandler((req, _) =>
        {
            var pfad = req.RequestUri!.AbsolutePath.TrimStart('/');
            if (req.Method == HttpMethod.Get && pfad == "api/states") return RecordingHttpHandler.Json(zustaende);
            return antwort(pfad + req.RequestUri.Query, req) ?? RecordingHttpHandler.Json("{}", HttpStatusCode.NotFound);
        });
        var ha = new HomeAssistantService(new StubHttpClientFactory(handler), NullLogger<HomeAssistantService>.Instance);
        var automationen = new SteuerungAutomationService(ha, NullLogger<SteuerungAutomationService>.Instance, _pfade);
        var geloescht = new List<string>();
        var dienst = new SteuerungInstanzAufraeumenService(ha, automationen, NullLogger<SteuerungInstanzAufraeumenService>.Instance,
            (_, helfer, _) =>
            {
                geloescht.AddRange(helfer.Select(h => h.Kennung));
                return Task.FromResult<IReadOnlyDictionary<string, (bool Ok, string? Fehler)>>(
                    helfer.ToDictionary(h => h.Kennung, h => (!helferScheitern, helferScheitern ? "abgelehnt" : (string?)null)));
            });
        return new Lauf(dienst, handler, geloescht, new HomeAssistantSettings { Enabled = true, BaseUrl = "http://ha.local:8123", AccessToken = "t" });
    }

    private static HttpResponseMessage? Standard(string pfad, HttpRequestMessage req)
    {
        const string marke = "{\"description\":\"Herkunft: fork-ai/chiller-i2/{0}/3\"}";
        if (req.Method == HttpMethod.Get && pfad.StartsWith("api/config/automation/config/fork_ai_chiller-i2_", StringComparison.Ordinal))
        {
            var name = pfad["api/config/automation/config/fork_ai_chiller-i2_".Length..];
            return name switch
            {
                "waechter" => null,                                                                       // gibt es nicht (404)
                "sollwert" => RecordingHttpHandler.Json("{\"description\":\"von Hand gebaut\"}"),        // ohne Marke
                _ => RecordingHttpHandler.Json(marke.Replace("{0}", name)),
            };
        }
        if (req.Method == HttpMethod.Delete) return RecordingHttpHandler.Json("{\"result\":\"ok\"}");
        if (req.Method == HttpMethod.Get && pfad.StartsWith("api/config/config_entries/entry", StringComparison.Ordinal))
        {
            return RecordingHttpHandler.Json("[" + string.Join(",",
                SteuerungInstanzAufraeumenService.Planen("chiller-i2").Where(p => p.Art == "rechenwert")
                    .Select(p => $"{{\"entry_id\":\"E_{p.Kennung}\",\"title\":\"{p.Titel}\"}}")
                    .Append("{\"entry_id\":\"BASIS\",\"title\":\"Chiller Kuhlbedarf\"}")) + "]");
        }
        return null;
    }

    [Fact]
    public async Task EntferntNurDieBausteineDerInstanzUndLaesstHandgebautesStehen()
    {
        var l = Aufbau(Standard);

        var bilanz = await l.Dienst.EntfernenAsync("chiller-i2", l.Settings);

        Assert.True(bilanz.Erreichbar);
        Assert.True(bilanz.Vollstaendig);
        var loeschungen = l.Handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => r.Uri.AbsolutePath.TrimStart('/')).ToList();
        // Automationen: merker, regelung, waechter(404 → nicht da) bleibt ohne Aufruf, sollwert ohne Marke bleibt stehen.
        Assert.Contains("api/config/automation/config/fork_ai_chiller-i2_regelung", loeschungen);
        Assert.Contains("api/config/automation/config/fork_ai_chiller-i2_merker", loeschungen);
        Assert.DoesNotContain(loeschungen, l => l.EndsWith("_sollwert", StringComparison.Ordinal));
        Assert.DoesNotContain(loeschungen, l => l.EndsWith("_waechter", StringComparison.Ordinal));
        Assert.Contains(bilanz.Einzeln, t => t is { Kennung: "fork_ai_chiller-i2_sollwert", Stand: "fremd" });
        Assert.Contains(bilanz.Einzeln, t => t is { Kennung: "fork_ai_chiller-i2_waechter", Stand: "nicht_da" });
        // Rechenwerte: nur Einträge mit dem Titel der Instanz, nie der des ersten Kühlers.
        var eintraege = loeschungen.Where(x => x.StartsWith("api/config/config_entries/entry/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(eintraege);
        Assert.DoesNotContain("api/config/config_entries/entry/BASIS", loeschungen);
        // Helfer: genau die der Instanz (die nicht in HA standen, werden nicht angefragt).
        Assert.NotEmpty(l.Helfer);
        Assert.All(l.Helfer, h => Assert.EndsWith("_i2", h));
        Assert.DoesNotContain("input_number.chiller_hysterese", l.Helfer);
        // Die Sicherung der entfernten Automationen liegt auf der Platte.
        var sicherungen = Directory.EnumerateFiles(Path.Combine(_wurzel), "fork_ai_chiller-i2_*.json", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains(sicherungen, s => s!.StartsWith("fork_ai_chiller-i2_regelung-", StringComparison.Ordinal));
        Assert.Contains(sicherungen, s => s!.StartsWith("fork_ai_chiller-i2_merker-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OhneAntwortVonHomeAssistantWirdNichtsAngefasst()
    {
        var l = Aufbau(Standard, stumm: true);

        var bilanz = await l.Dienst.EntfernenAsync("chiller-i2", l.Settings);

        Assert.False(bilanz.Erreichbar);
        Assert.False(bilanz.Vollstaendig);
        Assert.DoesNotContain(l.Handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Empty(l.Helfer);
    }

    [Fact]
    public async Task EinMisslungenesEntfernenMachtDieBilanzUnvollstaendig()
    {
        var l = Aufbau(Standard, helferScheitern: true);

        var bilanz = await l.Dienst.EntfernenAsync("chiller-i2", l.Settings);

        Assert.True(bilanz.Erreichbar);
        Assert.False(bilanz.Vollstaendig);
        Assert.Contains(bilanz.Einzeln, t => t is { Art: "helfer", Stand: "fehler", Hinweis: "abgelehnt" });
    }

    [Fact]
    public async Task DerErsteKuehlerWirdNieEntfernt()
    {
        var l = Aufbau(Standard);

        var bilanz = await l.Dienst.EntfernenAsync("chiller", l.Settings);

        Assert.False(bilanz.Erreichbar); // kein Plan → nichts getan, nichts gemeldet
        Assert.DoesNotContain(l.Handler.Requests, r => r.Method == HttpMethod.Delete);
        Assert.Empty(l.Helfer);
    }

    [Fact]
    public async Task EineAutomationOhneSicherungWirdNichtEntfernt()
    {
        // Die Sicherung liegt unter dem Datenpfad; ist er nicht beschreibbar, bleibt die Automation stehen.
        var l = Aufbau(Standard);
        var ordner = new SteuerungAutomationService(
            new HomeAssistantService(new StubHttpClientFactory(l.Handler), NullLogger<HomeAssistantService>.Instance),
            NullLogger<SteuerungAutomationService>.Instance, _pfade).SicherungsOrdner;
        Directory.CreateDirectory(Path.GetDirectoryName(ordner)!);
        File.WriteAllText(ordner, "ich bin eine Datei, kein Ordner"); // Directory.CreateDirectory scheitert daran

        var bilanz = await l.Dienst.EntfernenAsync("chiller-i2", l.Settings);

        Assert.DoesNotContain(l.Handler.Requests, r => r.Method == HttpMethod.Delete && r.Uri.AbsolutePath.Contains("automation", StringComparison.Ordinal));
        Assert.Contains(bilanz.Einzeln, t => t is { Art: "automation", Stand: "fehler" });
        Assert.False(bilanz.Vollstaendig);
    }
}
