using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Die Regeln des Licht-Wächters. Anlass: 25.10.2026, Betriebsart „Aus“ statt „Zeitplan“,
/// das Zelt blieb 36 Stunden dunkel.
/// </summary>
public sealed class LichtWaechterRegelnTests
{
    private static WaechterLage Lage(
        bool sollHell = true, double? sensor = 100, bool zugeordnet = true, bool erreichbar = true,
        string? modus = "Schedule", string? ein = "05:00:00", string? aus = "17:00:00", int? stufe = 3)
        => new(sollHell, sensor, zugeordnet, 80, erreichbar, modus, ein, aus, stufe, "05:00", "17:00");

    [Fact]
    public void Hell_bei_Soll_hell_ist_in_Ordnung()
        => Assert.Equal(WaechterBefund.Ok, LichtWaechterRegeln.Beurteilen(Lage()));

    [Fact]
    public void Dunkel_bei_Soll_dunkel_ist_in_Ordnung_auch_bei_Modus_Aus()
        => Assert.Equal(WaechterBefund.Ok, LichtWaechterRegeln.Beurteilen(Lage(sollHell: false, sensor: 0.3, modus: "Off")));

    [Fact]
    public void Der_Vorfall_vom_25_10_Modus_Aus_und_dunkel_ist_ein_Modusfehler()
        => Assert.Equal(WaechterBefund.ModusFalsch, LichtWaechterRegeln.Beurteilen(Lage(sensor: 0.3, modus: "Off")));

    [Fact]
    public void Falsche_Zeiten_werden_erkannt_auch_mit_Sekunden()
    {
        Assert.Equal(WaechterBefund.ZeitenFalsch, LichtWaechterRegeln.Beurteilen(Lage(sensor: 0.3, ein: "08:00:00")));
        Assert.Equal(WaechterBefund.Ungeklaert, LichtWaechterRegeln.Beurteilen(Lage(sensor: 0.3)));
    }

    [Fact]
    public void Stufe_null_wird_nur_gemeldet()
    {
        var befund = LichtWaechterRegeln.Beurteilen(Lage(sensor: 0.3, stufe: 0));
        Assert.Equal(WaechterBefund.StufeNull, befund);
        Assert.False(LichtWaechterRegeln.Korrigierbar(befund));
    }

    [Fact]
    public void Nur_Modus_und_Zeiten_sind_korrigierbar()
    {
        Assert.True(LichtWaechterRegeln.Korrigierbar(WaechterBefund.ModusFalsch));
        Assert.True(LichtWaechterRegeln.Korrigierbar(WaechterBefund.ZeitenFalsch));
        foreach (var b in new[] { WaechterBefund.Ungeklaert, WaechterBefund.NichtErreichbar, WaechterBefund.SensorAusgefallen, WaechterBefund.StufeNull, WaechterBefund.Ok })
            Assert.False(LichtWaechterRegeln.Korrigierbar(b));
    }

    [Fact]
    public void Nicht_erreichbar_schaltet_nichts_und_hat_Vorrang()
        => Assert.Equal(WaechterBefund.NichtErreichbar, LichtWaechterRegeln.Beurteilen(Lage(sensor: 0.3, modus: "Off", erreichbar: false)));

    [Fact]
    public void Sensor_ohne_Wert_prueft_Modus_weiter_und_meldet_sonst_Ausfall()
    {
        Assert.Equal(WaechterBefund.ModusFalsch, LichtWaechterRegeln.Beurteilen(Lage(sensor: null, modus: "Off")));
        Assert.Equal(WaechterBefund.SensorAusgefallen, LichtWaechterRegeln.Beurteilen(Lage(sensor: null)));
    }

    [Fact]
    public void Ohne_zugeordneten_Sensor_bleibt_es_still_wenn_Modus_und_Zeiten_stimmen()
        => Assert.Equal(WaechterBefund.Ok, LichtWaechterRegeln.Beurteilen(Lage(sensor: null, zugeordnet: false)));

    [Fact]
    public void Hell_in_der_Dunkelphase_wird_gleich_geprueft()
    {
        Assert.Equal(WaechterBefund.ModusFalsch, LichtWaechterRegeln.Beurteilen(Lage(sollHell: false, sensor: 100, modus: "On")));
        Assert.Equal(WaechterBefund.Ungeklaert, LichtWaechterRegeln.Beurteilen(Lage(sollHell: false, sensor: 100)));
    }

    [Fact]
    public void Schwelle_gilt_ab_Hell_ab()
    {
        Assert.Equal(WaechterBefund.Ok, LichtWaechterRegeln.Beurteilen(Lage(sensor: 80)));
        Assert.Equal(WaechterBefund.Ungeklaert, LichtWaechterRegeln.Beurteilen(Lage(sensor: 79.9)));
    }

    [Fact]
    public void Letzte_Grenze_ist_die_juengste_Ein_oder_Ausschaltzeit()
    {
        var (z, hell) = LichtWaechterRegeln.LetzteGrenze(new DateTime(2026, 10, 27, 5, 7, 0), "05:00", "17:00")!.Value;
        Assert.Equal(new DateTime(2026, 10, 27, 5, 0, 0), z);
        Assert.True(hell);

        (z, hell) = LichtWaechterRegeln.LetzteGrenze(new DateTime(2026, 10, 27, 3, 0, 0), "05:00", "17:00")!.Value;
        Assert.Equal(new DateTime(2026, 10, 26, 17, 0, 0), z);
        Assert.False(hell);
    }

    [Fact]
    public void Letzte_Grenze_geht_auch_ueber_Mitternacht()
    {
        var (z, hell) = LichtWaechterRegeln.LetzteGrenze(new DateTime(2026, 10, 27, 1, 0, 0), "20:00", "08:00")!.Value;
        Assert.Equal(new DateTime(2026, 10, 26, 20, 0, 0), z);
        Assert.True(hell);
        Assert.Null(LichtWaechterRegeln.LetzteGrenze(DateTime.Now, "08:00", "08:00"));
    }
}
