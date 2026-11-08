using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Services;

/// <summary>Einstellungen, Pause und das Wieder-Einschalten der Steckdose des Controller-Wächters.</summary>
public sealed class ControllerWaechterServiceTests : IDisposable
{
    private readonly string _wurzel;
    private readonly SteuerungRepository _repo;
    private readonly AppSettingsRepository _ablage;
    private DateTime _jetzt = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    public ControllerWaechterServiceTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "ControllerWaechter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wurzel);
        var pfade = new AppPaths(_wurzel);
        TestDatabase.Initialize(pfade);
        _repo = new SteuerungRepository(pfade);
        _ablage = new AppSettingsRepository(pfade);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch (IOException) { /* Aufräumen darf den Test nicht kippen */ }
    }

    private ControllerWaechterService Dienst(IAcFunk? funk = null)
        => new(_repo, funk!, null!, null!, _ablage, null!, null!, null!, null!, NullLogger<ControllerWaechterService>.Instance,
            () => _jetzt, (_, _) => Task.CompletedTask);

    private static ControllerWaechterEintrag Gueltig() => new()
    {
        Name = "Trockenzelt", StatusEntity = "binary_sensor.t_status", FeuchteEntity = "sensor.t_feuchte", SteckdoseEntity = "switch.t_dose",
    };

    [Fact]
    public void Ohne_Speichern_gibt_es_keine_Controller()
        => Assert.Empty(Dienst().Einstellungen.Controller);

    [Fact]
    public void Die_Vorgaben_sind_Melden_und_Neustart_nach_15_Minuten()
    {
        var e = new ControllerWaechterEintrag();
        Assert.Equal(ControllerWaechterEintrag.Neustarten, e.Modus);
        Assert.True(e.Aktiv);
        Assert.Equal(10, e.MeldenNachMin);
        Assert.Equal(15, e.NeustartNachMin);
        Assert.Equal(15, e.AusDauerSek);
        Assert.Equal(2, e.MaxNeustartsProTag);
    }

    [Fact]
    public void JedesFeldUeberlebtDasSpeichern()
    {
        // Überall ANDERE Werte als die Vorgaben, sonst fiele ein verlorenes Feld auf den Standard zurück.
        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(new ControllerWaechterEinstellungen
        {
            Controller =
            {
                new ControllerWaechterEintrag
                {
                    Name = " Lager ", StatusEntity = "binary_sensor.a", FeuchteEntity = "sensor.b", SteckdoseEntity = "switch.c",
                    Aktiv = false, Modus = ControllerWaechterEintrag.NurMelden, MeldenNachMin = 7, NeustartNachMin = 21,
                    AusDauerSek = 30, MaxNeustartsProTag = 4,
                },
            },
        });

        Assert.Empty(fehler);
        Assert.NotNull(gespeichert);
        var c = Assert.Single(dienst.Einstellungen.Controller);
        Assert.False(string.IsNullOrEmpty(c.Id));
        Assert.Equal("Lager", c.Name);
        Assert.Equal("binary_sensor.a", c.StatusEntity);
        Assert.Equal("sensor.b", c.FeuchteEntity);
        Assert.Equal("switch.c", c.SteckdoseEntity);
        Assert.False(c.Aktiv);
        Assert.Equal(ControllerWaechterEintrag.NurMelden, c.Modus);
        Assert.Equal(7, c.MeldenNachMin);
        Assert.Equal(21, c.NeustartNachMin);
        Assert.Equal(30, c.AusDauerSek);
        Assert.Equal(4, c.MaxNeustartsProTag);
    }

    [Fact]
    public void Zweimal_speichern_behaelt_Kennung_und_Pause()
    {
        var dienst = Dienst();
        dienst.Speichern(new ControllerWaechterEinstellungen { Controller = { Gueltig() } });
        var id = dienst.Einstellungen.Controller[0].Id;
        Assert.True(dienst.Pausieren(id, 72));

        var zweites = Gueltig();
        zweites.Id = id;
        zweites.PauseBisUtc = null; // das Formular schickt die Pause nicht mit
        var (gespeichert, _) = dienst.Speichern(new ControllerWaechterEinstellungen { Controller = { zweites } });

        Assert.NotNull(gespeichert);
        var c = Assert.Single(dienst.Einstellungen.Controller);
        Assert.Equal(id, c.Id);
        Assert.Equal(_jetzt.AddHours(72), c.PauseBisUtc);
    }

    [Fact]
    public void Pause_laesst_sich_aufheben_und_ein_unbekannter_Controller_wird_abgewiesen()
    {
        var dienst = Dienst();
        dienst.Speichern(new ControllerWaechterEinstellungen { Controller = { Gueltig() } });
        var id = dienst.Einstellungen.Controller[0].Id;
        dienst.Pausieren(id, 1);
        Assert.NotNull(dienst.Einstellungen.Controller[0].PauseBisUtc);
        dienst.Pausieren(id, 0);
        Assert.Null(dienst.Einstellungen.Controller[0].PauseBisUtc);
        Assert.False(dienst.Pausieren("gibt-es-nicht", 1));
    }

    [Fact]
    public void Ein_entfernter_Controller_nimmt_seinen_Merker_mit()
    {
        var dienst = Dienst();
        dienst.Speichern(new ControllerWaechterEinstellungen { Controller = { Gueltig() } });
        var id = dienst.Einstellungen.Controller[0].Id;
        _ablage.SetValue("controller-waechter:merker:" + id, "{}");

        dienst.Speichern(new ControllerWaechterEinstellungen());

        Assert.Empty(dienst.Einstellungen.Controller);
        Assert.Null(_ablage.GetValue("controller-waechter:merker:" + id));
    }

    [Theory]
    [InlineData("Modus", "1")]
    [InlineData("MeldenNachMin", "0")]
    [InlineData("MeldenNachMin", "121")]
    [InlineData("NeustartNachMin", "5")]      // vor der Meldung (Vorgabe 10)
    [InlineData("AusDauerSek", "2")]
    [InlineData("MaxNeustartsProTag", "0")]
    [InlineData("MaxNeustartsProTag", "7")]
    [InlineData("StatusEntity", "sensor.falsch")]
    [InlineData("FeuchteEntity", "switch.falsch")]
    [InlineData("SteckdoseEntity", "sensor.falsch")]
    [InlineData("Name", " ")]
    public void Ungueltiges_wird_abgelehnt_und_nicht_gespeichert(string feld, string wert)
    {
        var c = Gueltig();
        switch (feld)
        {
            case "Modus": c.Modus = wert; break;
            case "MeldenNachMin": c.MeldenNachMin = int.Parse(wert); break;
            case "NeustartNachMin": c.NeustartNachMin = int.Parse(wert); break;
            case "AusDauerSek": c.AusDauerSek = int.Parse(wert); break;
            case "MaxNeustartsProTag": c.MaxNeustartsProTag = int.Parse(wert); break;
            case "StatusEntity": c.StatusEntity = wert; break;
            case "FeuchteEntity": c.FeuchteEntity = wert; break;
            case "SteckdoseEntity": c.SteckdoseEntity = wert; break;
            case "Name": c.Name = wert; break;
        }

        var dienst = Dienst();
        var (gespeichert, fehler) = dienst.Speichern(new ControllerWaechterEinstellungen { Controller = { c } });

        Assert.Null(gespeichert);
        Assert.Contains(fehler.Keys, k => k.EndsWith("." + feld, StringComparison.Ordinal));
        Assert.Empty(dienst.Einstellungen.Controller);
    }

    [Fact]
    public void Ohne_Verbindung_und_ohne_Feuchtefuehler_gibt_es_nichts_zu_pruefen()
    {
        var c = Gueltig();
        c.StatusEntity = null;
        c.FeuchteEntity = null;
        var (gespeichert, fehler) = Dienst().Speichern(new ControllerWaechterEinstellungen { Controller = { c } });
        Assert.Null(gespeichert);
        Assert.NotEmpty(fehler);
    }

    // ------------------------------------------------------- Wieder einschalten

    [Fact]
    public async Task Einschalten_ist_bestaetigt_wenn_die_Steckdose_an_meldet()
    {
        var funk = new Dose { AnNachBefehl = 1 };
        Assert.True(await Dienst(funk).EinschaltenAsync(new HomeAssistantSettings(), "switch.t_dose"));
        Assert.Equal(1, funk.AnBefehle);
    }

    [Fact]
    public async Task Einschalten_wiederholt_bis_die_Steckdose_wirklich_an_ist()
    {
        // Der erste Befehl kommt nicht an (Shelly kurz weg): Die Steckdose darf nach einem Neustart nicht aus bleiben.
        var funk = new Dose { AnNachBefehl = 3 };
        Assert.True(await Dienst(funk).EinschaltenAsync(new HomeAssistantSettings(), "switch.t_dose"));
        Assert.Equal(3, funk.AnBefehle);
    }

    [Fact]
    public async Task Einschalten_meldet_Misserfolg_wenn_die_Steckdose_aus_bleibt()
    {
        var funk = new Dose { AnNachBefehl = int.MaxValue };
        Assert.False(await Dienst(funk).EinschaltenAsync(new HomeAssistantSettings(), "switch.t_dose"));
        Assert.Equal(Ausschalter.Versuche, funk.AnBefehle);
    }

    private sealed class Dose : IAcFunk
    {
        public int AnNachBefehl { get; init; } = 1;
        public int AnBefehle { get; private set; }

        public Task<HomeAssistantState?> ZustandAsync(HomeAssistantSettings einstellungen, string entityId, CancellationToken ct)
            => Task.FromResult<HomeAssistantState?>(new HomeAssistantState { EntityId = entityId, State = AnBefehle >= AnNachBefehl ? "on" : "off" });

        public Task<bool> SchickenAsync(HomeAssistantSettings einstellungen, string domain, string dienst, string entityId,
            IReadOnlyDictionary<string, object> daten, CancellationToken ct)
        {
            if (dienst == "turn_on") AnBefehle++;
            return Task.FromResult(true);
        }
    }
}
