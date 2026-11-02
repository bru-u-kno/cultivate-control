using GrowDiary.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Seit dem 01.10.2026 ist ganz /api geschützt. Das Prüfprotokoll bekam
/// vorher für jeden erlaubten Ingress-Zugriff auf einen geschützten Pfad eine
/// Zeile — mit der weiteren Sperre wäre das jede Anfrage der Oberfläche
/// geworden (die Licht-Seite fragt alle 10 s), und echte Ereignisse wären in
/// den 500 angezeigten Zeilen untergegangen.
/// </summary>
[Collection(IntegrationsSammlung.Name)]
public sealed class ZugriffsProtokollTests
{
    private readonly IntegrationsApp _app;

    public ZugriffsProtokollTests(IntegrationsApp app) => _app = app;

    private int Zeilen(string pfad)
    {
        using var bereich = _app.Services.CreateScope();
        return bereich.ServiceProvider.GetRequiredService<SystemAuditRepository>()
            .GetRecent(500, "security")
            .Count(e => e.Summary == $"GET {pfad}");
    }

    [Fact]
    public async Task NormaleAnfragenUndEinstellungenLesenFuellenDasProtokollNicht_SensiblesSchon()
    {
        var client = _app.IngressClient();

        var vorher = Zeilen("/api/grows");
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync("/api/grows")).EnsureSuccessStatusCode();
        }
        Assert.Equal(vorher, Zeilen("/api/grows"));

        // Seit 02.11.2026: auch das Lesen der Einstellungen steht nicht mehr darin
        // (die Oberfläche liest im Takt, 182.180 Zeilen in drei Monaten) ...
        var einstellungenVorher = Zeilen("/api/settings");
        (await client.GetAsync("/api/settings")).EnsureSuccessStatusCode();
        Assert.Equal(einstellungenVorher, Zeilen("/api/settings"));

        // ... wohl aber das Lesen eines sensiblen Wegs, hier der Home-Assistant-Zugang.
        var sensibelVorher = Zeilen("/api/settings/home-assistant");
        (await client.GetAsync("/api/settings/home-assistant")).EnsureSuccessStatusCode();
        Assert.Equal(sensibelVorher + 1, Zeilen("/api/settings/home-assistant"));
    }
}
