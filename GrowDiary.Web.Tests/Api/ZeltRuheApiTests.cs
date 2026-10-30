using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// „Zelt ruhend" (cc.277): die Endpunkte durch die ganze Kette (Routen, Model-Binding, Dienst, Speicher).
/// </summary>
/// <remarks>
/// Die App läuft hier ohne Home Assistant: nichts wird nach draußen geschaltet, und ohne HA-Verbindung meldet das
/// Ruhendschalten ehrlich, dass nichts geschaltet wurde. Geprüft wird, was die Oberfläche sieht — dass die Auswahl nach dem
/// Speichern wieder dasteht, dass Unbekanntes abgewiesen wird und dass ein Zustand nie behauptet wird, der nicht eintrat.
/// Die Zustandswechsel selbst (Gerät aus, Automatik aus, wieder wie vorher) fährt <c>e2e/zelt-ruhend.spec.ts</c> im Demobestand.
/// </remarks>
[Collection(IntegrationsSammlung.Name)]
public sealed class ZeltRuheApiTests
{
    private readonly IntegrationsApp _app;

    public ZeltRuheApiTests(IntegrationsApp app) => _app = app;

    private const string Seite = "/api/steuerung/zelt-ruhe";

    private static StringContent Roh(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<JsonObject> Lesen(string pfad)
    {
        var antwort = await _app.IngressClient().GetAsync(pfad);
        var text = await antwort.Content.ReadAsStringAsync();
        Assert.True(antwort.IsSuccessStatusCode, $"{pfad}: {(int)antwort.StatusCode} {text}");
        return (JsonObject)JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task Der_Stand_nennt_jedes_Geraet_und_ist_ohne_Schalten_nicht_ruhend()
    {
        var stand = await Lesen(Seite);

        Assert.False(stand["ruhend"]!.GetValue<bool>());
        var geraete = stand["geraete"]!.AsArray();
        Assert.True(geraete.Count >= 7, "Es müssen alle Arten dastehen, auch die nicht zugeordneten.");
        foreach (var g in geraete)
        {
            Assert.False(string.IsNullOrWhiteSpace(g!["titel"]!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(g["jetzt"]!.GetValue<string>()));
            // Was es nicht gibt, steht auch nicht in der Auswahl.
            if (!g["vorhanden"]!.GetValue<bool>()) Assert.False(g["ausschalten"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task Die_Auswahl_kommt_nach_dem_Speichern_wieder_heraus_und_Unbekanntes_wird_abgewiesen()
    {
        var client = _app.IngressClient();

        // Unbekannte Kennung: 400 mit Satz, nichts gespeichert.
        var schlecht = await client.PutAsync(Seite + "/auswahl", Roh("""{"geraete":["licht","gibt-es-nicht"]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, schlecht.StatusCode);
        Assert.Contains("kein Gerät", await schlecht.Content.ReadAsStringAsync());

        // Fehlende Liste: ebenfalls abgewiesen, nicht als „alles abwählen" gelesen.
        var leer = await client.PutAsync(Seite + "/auswahl", Roh("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, leer.StatusCode);

        // Gültig, ohne Hauptentfeuchter: er bleibt aus der Auswahl.
        var gut = await client.PutAsync(Seite + "/auswahl", Roh("""{"geraete":["licht","zuluft"]}"""));
        Assert.True(gut.IsSuccessStatusCode);
        var stand = await Lesen(Seite);
        var gewaehlt = stand["geraete"]!.AsArray()
            .Where(g => g!["ausschalten"]!.GetValue<bool>()).Select(g => g!["kennung"]!.GetValue<string>()).ToList();
        foreach (var kennung in gewaehlt) Assert.Contains(kennung, new[] { "licht", "zuluft" });
        Assert.DoesNotContain("entfeuchter", gewaehlt);

        // Aufräumen: die geteilte App soll wieder bei der Vorgabe stehen (leer = nichts gewählt; null gibt es über die API nicht).
        // Die Vorgabe der Art gilt, solange nie gespeichert wurde — hier zählt, dass andere Fälle sich nicht auf „licht,zuluft" stützen.
        await client.PutAsync(Seite + "/auswahl", Roh("""{"geraete":["licht","co2","abluft","zusatz","chiller"]}"""));
    }

    [Fact]
    public async Task Wieder_aktiv_ohne_Ruhezustand_und_ohne_Home_Assistant_schaltet_nichts_und_sagt_es()
    {
        var antwort = await _app.IngressClient().PostAsync(Seite + "/aktiv", Roh("{}"));
        Assert.True(antwort.IsSuccessStatusCode);
        var rumpf = (JsonObject)JsonNode.Parse(await antwort.Content.ReadAsStringAsync())!;

        Assert.False(rumpf["ok"]!.GetValue<bool>());
        Assert.False(rumpf["stand"]!["ruhend"]!.GetValue<bool>());
        Assert.NotEmpty(rumpf["zusammenfassung"]!.GetValue<string>());
        Assert.Empty(rumpf["schritte"]!.AsArray());
    }

    [Fact]
    public async Task Ruhend_ohne_Home_Assistant_behauptet_keinen_Ruhezustand()
    {
        var antwort = await _app.IngressClient().PostAsync(Seite + "/ruhend", Roh("{}"));
        Assert.True(antwort.IsSuccessStatusCode);
        var rumpf = (JsonObject)JsonNode.Parse(await antwort.Content.ReadAsStringAsync())!;

        Assert.False(rumpf["ok"]!.GetValue<bool>());
        Assert.False(rumpf["stand"]!["ruhend"]!.GetValue<bool>(), "Was nicht geschaltet wurde, darf nicht als ruhend gelten.");
        Assert.NotEmpty(rumpf["zusammenfassung"]!.GetValue<string>());
    }
}
