using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Einstellungen und Pause des Licht-Wächters überstehen das Speichern. Das Beurteilen selbst steht in
/// <see cref="LichtWaechterRegelnTests"/>; den ganzen Weg bis zum Push fährt die Abnahme am laufenden Stand.
/// </summary>
public sealed class LichtWaechterServiceTests : IDisposable
{
    private readonly string _wurzel;
    private readonly SteuerungRepository _repo;
    private DateTime _jetzt = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    public LichtWaechterServiceTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "LichtWaechter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.Initialize(pfade);
        _repo = new SteuerungRepository(pfade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch (IOException) { /* Aufräumen darf den Test nicht kippen */ }
    }

    // Beurteilt wird hier nichts, deshalb brauchen Einstellungen und Pause nur das Repository.
    private LichtWaechterService Dienst()
        => new(_repo, null!, null!, null!, null!, null!, NullLogger<LichtWaechterService>.Instance, () => _jetzt);

    [Fact]
    public void Ohne_Speichern_gelten_die_Vorgaben()
    {
        var e = Dienst().Einstellungen;
        Assert.True(e.Aktiv);
        Assert.Equal(LichtWaechterEinstellungen.NurMelden, e.Modus);
        Assert.Equal(80, e.HellAbProzent);
        Assert.Null(e.PauseBisUtc);
    }

    [Fact]
    public void JedesFeldUeberlebtDasSpeichern()
    {
        // Überall ANDERE Werte als die Vorgaben, sonst fiele ein verlorenes Feld auf den Standard zurück.
        var dienst = Dienst();
        var pause = _jetzt.AddHours(5);
        var (gespeichert, fehler) = dienst.Speichern(new LichtWaechterEinstellungen
        {
            Aktiv = false, Modus = LichtWaechterEinstellungen.NurMelden, HellAbProzent = 65, PauseBisUtc = pause,
        });

        Assert.Empty(fehler);
        Assert.NotNull(gespeichert);
        var gelesen = dienst.Einstellungen;
        Assert.False(gelesen.Aktiv);
        Assert.Equal(LichtWaechterEinstellungen.NurMelden, gelesen.Modus);
        Assert.Equal(65, gelesen.HellAbProzent);
        Assert.Equal(pause, gelesen.PauseBisUtc);
    }

    [Theory]
    [InlineData("1", 80, "Modus")]
    [InlineData("korrigieren", 0, "HellAbProzent")]
    [InlineData("melden", 101, "HellAbProzent")]
    public void Ungueltiges_wird_abgelehnt_und_nicht_gespeichert(string modus, int hellAb, string feld)
    {
        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(new LichtWaechterEinstellungen { Modus = modus, HellAbProzent = hellAb });

        Assert.Null(gespeichert);
        Assert.True(fehler.ContainsKey(feld));
        Assert.Equal(80, dienst.Einstellungen.HellAbProzent);
    }

    [Fact]
    public void Pause_setzt_das_Ende_und_laesst_sich_aufheben()
    {
        var dienst = Dienst();
        Assert.Equal(_jetzt.AddHours(2), dienst.Pausieren(2).PauseBisUtc);
        Assert.Equal(_jetzt.AddHours(72), dienst.Pausieren(72).PauseBisUtc);
        Assert.Null(dienst.Pausieren(0).PauseBisUtc);
    }

    [Fact]
    public void Pause_aendert_die_uebrigen_Einstellungen_nicht()
    {
        var dienst = Dienst();
        dienst.Speichern(new LichtWaechterEinstellungen { Modus = LichtWaechterEinstellungen.NurMelden, HellAbProzent = 70 });
        dienst.Pausieren(1);
        var e = dienst.Einstellungen;
        Assert.Equal(LichtWaechterEinstellungen.NurMelden, e.Modus);
        Assert.Equal(70, e.HellAbProzent);
    }
}
