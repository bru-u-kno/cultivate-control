using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests;

/// <summary>
/// Wann nach einem Grow die Reinigung des leeren Systems empfohlen wird (31.10.2026).
/// </summary>
public sealed class ReinigungNachGrowTests
{
    private static readonly DateTime Heute = new(2026, 11, 1);

    private static GrowRun Grow(int id, int system, GrowStatus status, DateTime? ende)
        => new() { Id = id, Name = $"Grow {id}", SystemId = system, Status = status, EndDate = ende, StartDate = new DateTime(2026, 5, 1) };

    [Fact]
    public void Ein_frisch_abgeschlossener_Grow_auf_freiem_System_bekommt_die_Empfehlung()
    {
        var grow = Grow(1, 5, GrowStatus.Completed, Heute.AddDays(-2));
        Assert.True(ReinigungNachGrowService.Pruefen(grow, [grow], Heute, reinigungFertig: false));
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, false)]   // länger her als das Fenster: sonst mahnte jeder alte Grow
    public void Das_Fenster_endet_nach_sechzig_Tagen(int tageSeitAbschluss, bool erwartet)
    {
        var grow = Grow(1, 5, GrowStatus.Completed, Heute.AddDays(-tageSeitAbschluss));
        Assert.Equal(erwartet, ReinigungNachGrowService.Pruefen(grow, [grow], Heute, false));
    }

    [Fact]
    public void Nach_der_Reinigung_ist_Ruhe()
    {
        var grow = Grow(1, 5, GrowStatus.Completed, Heute.AddDays(-2));
        Assert.False(ReinigungNachGrowService.Pruefen(grow, [grow], Heute, reinigungFertig: true));
    }

    [Theory]
    [InlineData(GrowStatus.Running)]
    [InlineData(GrowStatus.Planning)]
    public void Belegt_ein_laufender_oder_geplanter_Grow_das_System_ist_es_nicht_leer(GrowStatus status)
    {
        var alt = Grow(1, 5, GrowStatus.Completed, Heute.AddDays(-2));
        var neu = Grow(2, 5, status, null);
        Assert.False(ReinigungNachGrowService.Pruefen(alt, [alt, neu], Heute, false));
    }

    [Fact]
    public void Ein_anderes_System_ist_unberuehrt()
    {
        var alt = Grow(1, 5, GrowStatus.Completed, Heute.AddDays(-2));
        var woanders = Grow(2, 6, GrowStatus.Running, null);
        Assert.True(ReinigungNachGrowService.Pruefen(alt, [alt, woanders], Heute, false));
    }

    [Fact]
    public void Ohne_Enddatum_oder_System_wird_nichts_empfohlen()
    {
        Assert.False(ReinigungNachGrowService.Pruefen(Grow(1, 5, GrowStatus.Completed, null), [], Heute, false));
        var ohneSystem = new GrowRun { Id = 1, Status = GrowStatus.Completed, EndDate = Heute.AddDays(-1) };
        Assert.False(ReinigungNachGrowService.Pruefen(ohneSystem, [ohneSystem], Heute, false));
    }

    [Fact]
    public void Die_Reinigung_fuers_leere_System_hat_keine_Pflanzenschritte_und_keinen_Zeitplan()
    {
        // Die SOP gehört zur Empfehlung: ohne Pflanzenschritte (im leeren System gibt es keine) und ohne Zeitplan
        // (sonst stünde sie zusätzlich bei jedem laufenden Grow unter „Fällige Routinen").
        var dir = AppContext.BaseDirectory;
        string? datei = null;
        while (dir != null && datei is null)
        {
            var kandidat = Path.Combine(dir, "GrowDiary.Web", "wwwroot", "knowledge-defaults", "sops", ReinigungNachGrowService.SopId + ".json");
            if (File.Exists(kandidat)) datei = kandidat;
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(datei);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(datei!));
        var wurzel = json.RootElement;
        Assert.Equal(ReinigungNachGrowService.SopId, wurzel.GetProperty("id").GetString());
        var schritte = wurzel.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("title").GetString()!).ToList();
        Assert.True(schritte.Count >= 8, "Die Reinigung hat zu wenige Schritte.");
        Assert.DoesNotContain(schritte, t => t.Contains("Pflanzen", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wurzel.GetProperty("triggers").EnumerateArray(), t => t.GetProperty("type").GetString() == "Schedule");
    }
}
