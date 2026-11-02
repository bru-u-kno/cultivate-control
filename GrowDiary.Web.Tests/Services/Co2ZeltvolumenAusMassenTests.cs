using System.Net;
using GrowDiary.Web.Api.Mapping;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.Knowledge;
using GrowDiary.Web.Tests.TestFakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Das Zeltvolumen der CO₂-Steuerung kommt aus den Maßen des Zeltes mit CO₂ — kein zweiter Eintrag von Hand.
/// </summary>
/// <remarks>
/// Vorher führte die CO₂-Seite ein eigenes Handfeld (Standard 5,76 m³), das von den Zeltmaßen nichts wusste.
/// Wer die Maße änderte, änderte das Volumen der Impulsdauer nicht mit (02.11.2026, belegt am Quellcode).
/// </remarks>
public sealed class Co2ZeltvolumenAusMassenTests : IDisposable
{
    private readonly string _wurzel;
    private readonly AppPaths _pfade;
    private readonly RecordingHttpHandler _ha;
    private readonly TentRepository _zelte;

    public Co2ZeltvolumenAusMassenTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "Co2Volumen_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        KopiereWissen(Path.Combine(ProjektWurzel(), "GrowDiary.Web", "wwwroot", "knowledge-defaults"), _wurzel);
        _pfade = new AppPaths(_wurzel);
        TestDatabase.InitializeWithDefaultTent(_pfade);
        new HomeAssistantSettingsRepository(_pfade).SaveHomeAssistantSettings(new HomeAssistantSettings
        {
            BaseUrl = "http://ha.local:8123", AccessToken = "token", Enabled = true,
        });
        _ha = new RecordingHttpHandler((anfrage, _) => anfrage.Method == HttpMethod.Get
            ? RecordingHttpHandler.Json("[]")
            : RecordingHttpHandler.Json("[]"));
        _zelte = new TentRepository(_pfade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch { /* Windows haelt manchmal fest */ }
    }

    private Co2SteuerungService Dienst()
    {
        var ha = new HomeAssistantService(new StubHttpClientFactory(_ha), NullLogger<HomeAssistantService>.Instance);
        var haSettings = new HomeAssistantSettingsRepository(_pfade);
        var steuerung = new SteuerungRepository(_pfade);
        var grows = new GrowRepository(_pfade);
        var wissen = new KnowledgeBaseLoader(_pfade, NullLogger<KnowledgeBaseLoader>.Instance);
        wissen.Initialize();
        return new Co2SteuerungService(
            steuerung, new KostenRepository(_pfade), new JournalRepository(_pfade), grows,
            new HydroSetupRepository(_pfade, _zelte), _zelte, new TargetValueService(wissen), wissen,
            ha, haSettings, new SteuerungGeraeteService(steuerung),
            new WochenplanSyncService(grows, wissen, steuerung, new AlertRuleRepository(_pfade), ha, haSettings,
                NullLogger<WochenplanSyncService>.Instance),
            new SteuerungMittelwertService(ha, haSettings, NullLogger<SteuerungMittelwertService>.Instance),
            NullLogger<Co2SteuerungService>.Instance);
    }

    /// <summary>Das Standardzelt des Testbestands — ohne CO₂-Flag und ohne Maße — wird zum CO₂-Zelt mit diesen Maßen.</summary>
    private Tent CO2Zelt(int? b, int? t, int? h, bool co2 = true, string name = "Zelt-RDWC")
    {
        var z = _zelte.GetTents().First();
        z.Name = name; z.WidthCm = b; z.DepthCm = t; z.TentHeightCm = h; z.HasCo2Enrichment = co2;
        _zelte.UpdateTent(z);
        return z;
    }

    private IEnumerable<string> GeschriebeneHelfer()
        => _ha.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/api/services/input_number/set_value", StringComparison.Ordinal))
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Body!).RootElement.GetProperty("entity_id").GetString()!);

    [Fact]
    public void EinCo2Zelt_MitMassen_LiefertDasVolumenAusDenMassen()
    {
        CO2Zelt(240, 120, 200);
        var (volumen, quelle) = Dienst().WirksamesZeltvolumen(handM3: 9.99);

        Assert.Equal(5.76, volumen);
        Assert.True(quelle.AusMassen);
        Assert.Equal("Zelt-RDWC", quelle.ZeltName);
        Assert.Equal("240×120×200 cm", quelle.Masse);
        Assert.Null(quelle.Grund);
        // Auch die Einstellungen, aus denen die Seite und der HA-Abgleich lesen, tragen den gerechneten Wert.
        Assert.Equal(5.76, Dienst().Einstellungen.ZeltvolumenM3);
    }

    [Fact]
    public void MassAendern_AendertDasVolumenDerSteuerungMit()
    {
        CO2Zelt(240, 120, 200);
        Assert.Equal(5.76, Dienst().Einstellungen.ZeltvolumenM3);

        CO2Zelt(240, 120, 220);

        Assert.Equal(6.34, Dienst().Einstellungen.ZeltvolumenM3);
    }

    [Fact]
    public void KeinCo2Zelt_GiltDerHandwert_MitGrund()
    {
        CO2Zelt(240, 120, 200, co2: false);
        var (volumen, quelle) = Dienst().WirksamesZeltvolumen(3.5);

        Assert.Equal(3.5, volumen);
        Assert.False(quelle.AusMassen);
        Assert.Contains("CO₂", quelle.Grund);
    }

    [Fact]
    public void ZweiCo2Zelte_GiltDerHandwert_MitGrund()
    {
        CO2Zelt(240, 120, 200);
        _zelte.CreateTent(new Tent { Name = "Zweites", HasCo2Enrichment = true, WidthCm = 100, DepthCm = 100, TentHeightCm = 200 });

        var (volumen, quelle) = Dienst().WirksamesZeltvolumen(3.5);

        Assert.Equal(3.5, volumen);
        Assert.False(quelle.AusMassen);
        Assert.Contains("Mehrere", quelle.Grund);
    }

    [Fact]
    public void MassFehlt_GiltDerHandwert_UndDerGrundNenntDasZelt()
    {
        CO2Zelt(240, 120, null);
        var (volumen, quelle) = Dienst().WirksamesZeltvolumen(3.5);

        Assert.Equal(3.5, volumen);
        Assert.Contains("Zelt-RDWC", quelle.Grund);
    }

    [Fact]
    public async Task VolumenAusserhalbDerHelferSpanne_GiltDerHandwert_DamitSpeichernNichtAbgelehntWird()
    {
        CO2Zelt(60, 60, 160); // 0,58 m³ — der Helfer nimmt 1 bis 20
        var dienst = Dienst();
        var (volumen, quelle) = dienst.WirksamesZeltvolumen(5.76);

        Assert.Equal(5.76, volumen);
        Assert.False(quelle.AusMassen);
        Assert.Contains("außerhalb", quelle.Grund);

        // Selbsttest gegen den eigentlichen Schaden: Speichern der Seite geht durch.
        var (gespeichert, fehler, _) = await dienst.SpeichernAsync(new Co2Einstellungen(), CancellationToken.None);
        Assert.NotNull(gespeichert);
        Assert.Empty(fehler);
    }

    [Fact]
    public async Task SpeichernAusMassen_IgnoriertDenWertDesFormulars()
    {
        CO2Zelt(240, 120, 200);
        var dienst = Dienst();

        var (gespeichert, _, _) = await dienst.SpeichernAsync(new Co2Einstellungen { ZeltvolumenM3 = 12.34 }, CancellationToken.None);

        Assert.Equal(5.76, gespeichert!.ZeltvolumenM3);
        var gelesen = new SteuerungRepository(_pfade).GetEinstellungen<Co2Einstellungen>(Co2SteuerungService.Modul)!;
        Assert.Equal(5.76, gelesen.ZeltvolumenM3);
    }

    [Fact]
    public async Task Nachziehen_SchreibtNurDenVolumenHelfer_MitDemGerechnetenWert()
    {
        CO2Zelt(240, 120, 220);

        var ok = await Dienst().ZeltvolumenNachziehenAsync(CancellationToken.None);

        Assert.True(ok);
        Assert.Equal([Co2SteuerungService.Entitaeten.Zeltvolumen], GeschriebeneHelfer().ToList());
        var wert = _ha.Requests.Where(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.Contains("/input_number/set_value"))
            .Select(r => System.Text.Json.JsonDocument.Parse(r.Body!).RootElement.GetProperty("value").GetDouble()).Single();
        Assert.Equal(6.34, wert);
    }

    [Fact]
    public async Task Nachziehen_OhneMassBestimmtesVolumen_SchreibtNichts()
    {
        CO2Zelt(240, 120, 200, co2: false);

        await Dienst().ZeltvolumenNachziehenAsync(CancellationToken.None);

        Assert.Empty(GeschriebeneHelfer());
    }

    [Fact]
    public void ZeltDto_TraegtVolumenFlaecheUndLuftwechsel_NichtEingetragen()
    {
        var z = CO2Zelt(240, 120, 200);
        z.ExhaustM3h = 683; z.ExhaustFanCount = 1;
        var dto = z.ToDto();

        Assert.Equal(5.76, dto.VolumeM3);
        Assert.Equal(2.88, dto.FloorAreaM2);
        Assert.Equal(119, dto.AirChangesPerHour);
    }

    [Fact]
    public void ZeltDto_OhneMasse_HatKeineAbgeleitetenWerte()
    {
        var z = CO2Zelt(240, null, 200);
        var dto = z.ToDto();

        Assert.Null(dto.VolumeM3);
        Assert.Null(dto.FloorAreaM2);
        Assert.Null(dto.AirChangesPerHour);
    }

    private static string ProjektWurzel()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Projektwurzel nicht gefunden.");
    }

    private static void KopiereWissen(string quelle, string ziel)
    {
        var nach = Path.Combine(ziel, "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(nach, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
    }
}
