using System.Text.Json;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests;

/// <summary>
/// „Zelt ruhend" (cc.277): der Ablauf, mit einer Anlage zum Anfassen — Reihenfolge, Teilfehler, Abbruch, Nachholen.
///
/// <para><b>Anlass.</b> Der Prüfer hat gefunden, dass kein einziger Test den Ablauf deckte: Merker erst nach der Schleife, eine
/// Automatik, die nach Fehlschlag aus blieb, ein Kühler-Regler, der den Kompressor wieder einschaltete. Die Attrappe führt ein
/// Protokoll der Aufrufe; der Speicher läuft durch JSON hin und zurück, damit ein vergessenes <c>Speichern</c> auffällt.</para>
/// </summary>
public sealed class ZeltRuheAblaufTests
{
    private sealed class Anlage : IZeltRuheWerkzeug
    {
        public readonly List<string> Protokoll = new();
        public readonly Dictionary<string, string?> Zustand = new();
        public readonly HashSet<string> AutomatikKaputt = new();
        public readonly HashSet<string> SchaltenAbgelehnt = new();
        public readonly HashSet<string> SchaltenUnbestaetigt = new();
        public readonly HashSet<string> SchaltenAbbruch = new();
        public readonly Dictionary<string, bool> AutomatikWar = new();
        public IReadOnlyList<int> KuehlerZelte = new[] { 7 };
        public Func<string>? BeiAutomatikAus;
        public string Json = JsonSerializer.Serialize(new ZeltRuheEinstellungen());

        public ZeltRuheEinstellungen Laden() => JsonSerializer.Deserialize<ZeltRuheEinstellungen>(Json)!;
        public void Speichern(ZeltRuheEinstellungen e) => Json = JsonSerializer.Serialize(e);
        public ZeltRuheAblauf Ablauf() => new(this, Laden, Speichern, NullLogger.Instance);

        public Task<string?> ZustandAsync(string entity, CancellationToken ct)
            => Task.FromResult(Zustand.TryGetValue(entity, out var z) ? z : null);

        public Task<Schaltausgang> SchaltenAsync(string entity, (string Domain, string Dienst, IReadOnlyDictionary<string, object>? Daten) aktion, string? soll, CancellationToken ct)
        {
            Protokoll.Add($"schalten {entity} {aktion.Dienst} {soll}");
            if (SchaltenAbbruch.Contains(entity)) throw new OperationCanceledException();
            if (SchaltenAbgelehnt.Contains(entity)) return Task.FromResult(Schaltausgang.Abgelehnt);
            if (SchaltenUnbestaetigt.Contains(entity)) return Task.FromResult(Schaltausgang.Unbestaetigt);
            return Task.FromResult(Schaltausgang.Bestaetigt);
        }

        public Task<(bool Ok, bool? WarAn)> AutomatikAusAsync(string kennung, CancellationToken ct)
        {
            Protokoll.Add($"automatik-aus {kennung}");
            BeiAutomatikAus?.Invoke();
            if (AutomatikKaputt.Contains(kennung)) return Task.FromResult<(bool, bool?)>((false, null));
            if (kennung is ZeltRuheArten.Licht or ZeltRuheArten.Abluft) return Task.FromResult<(bool, bool?)>((true, null));
            return Task.FromResult<(bool, bool?)>((true, AutomatikWar.GetValueOrDefault(kennung, true)));
        }

        public Task<bool> AutomatikAnAsync(string kennung, CancellationToken ct)
        {
            Protokoll.Add($"automatik-an {kennung}");
            return Task.FromResult(!AutomatikKaputt.Contains(kennung));
        }

        public IReadOnlyList<int> KuehlerReglerAus() { Protokoll.Add("kuehler-regler-aus"); return KuehlerZelte; }
        public void KuehlerReglerAn(IEnumerable<int> zeltIds) => Protokoll.Add("kuehler-regler-an " + string.Join(",", zeltIds));
        public void WaechterPausierenBis(DateTime bisUtc) => Protokoll.Add("waechter-pause");
        public void WaechterFortsetzen() => Protokoll.Add("waechter-weiter");
    }

    private static (ZeltRuheArt, string) Art(string kennung, string entity) => (ZeltRuheArten.Finden(kennung)!, entity);

    private static Anlage Standardanlage()
    {
        var a = new Anlage();
        a.Zustand["select.licht"] = "Schedule";
        a.Zustand["select.co2"] = "On";
        a.Zustand["switch.kuehler"] = "on";
        return a;
    }

    private static readonly (ZeltRuheArt, string)[] LichtCo2Kuehler =
    [
        Art(ZeltRuheArten.Licht, "select.licht"), Art(ZeltRuheArten.Co2, "select.co2"), Art(ZeltRuheArten.Chiller, "switch.kuehler"),
    ];

    [Fact]
    public async Task Ruhend_schaltet_je_Geraet_erst_die_Automatik_dann_das_Geraet_und_merkt_den_Zustand_davor()
    {
        var a = Standardanlage();
        var schritte = await a.Ablauf().RuhendAsync(LichtCo2Kuehler, default);

        Assert.All(schritte, s => Assert.True(s.Ok, s.Text));
        // Reihenfolge je Gerät: Automatik zuerst, sonst schaltete sie das Gerät im nächsten Takt wieder ein.
        Assert.True(a.Protokoll.IndexOf("automatik-aus co2") < a.Protokoll.IndexOf("schalten select.co2 select_option Off"));
        var e = a.Laden();
        Assert.True(e.Ruhend);
        Assert.NotNull(e.SeitUtc);
        Assert.Equal("Schedule", e.Gemerkt.Single(m => m.Kennung == ZeltRuheArten.Licht).Vorher);
        Assert.True(e.Gemerkt.Single(m => m.Kennung == ZeltRuheArten.Co2).AutomatikWarAn);
        Assert.Contains("waechter-pause", a.Protokoll);
        Assert.True(e.WaechterAngehalten);
    }

    [Fact]
    public async Task Der_Merker_steht_im_Speicher_bevor_irgendetwas_geschaltet_wird()
    {
        // Bricht die Anfrage nach der Automatik ab, muss „Zelt wieder aktiv" den Weg zurück kennen (Befund B1 des Prüfers).
        var a = Standardanlage();
        ZeltRuheEinstellungen? beiAutomatik = null;
        a.BeiAutomatikAus = () => { beiAutomatik = a.Laden(); return ""; };

        await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Co2, "select.co2")], default);

        Assert.NotNull(beiAutomatik);
        Assert.True(beiAutomatik!.Ruhend, "Vor dem ersten Eingriff muss „ruhend“ schon gespeichert sein.");
        Assert.Contains(beiAutomatik.Gemerkt, m => m.Kennung == ZeltRuheArten.Co2 && m.Vorher == "On");
    }

    [Fact]
    public async Task Bricht_die_Anfrage_mitten_im_Schalten_ab_bleibt_der_Weg_zurueck()
    {
        var a = Standardanlage();
        a.SchaltenAbbruch.Add("select.co2");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Licht, "select.licht"), Art(ZeltRuheArten.Co2, "select.co2")], default));

        var e = a.Laden();
        Assert.True(e.Ruhend);
        Assert.Contains(e.Gemerkt, m => m.Kennung == ZeltRuheArten.Licht);
        Assert.Contains(e.Gemerkt, m => m.Kennung == ZeltRuheArten.Co2 && m.AutomatikWarAn == true);

        // … und „Zelt wieder aktiv" führt zurück.
        a.SchaltenAbbruch.Clear();
        var weg = await a.Ablauf().AktivAsync(default);
        Assert.All(weg, s => Assert.True(s.Ok, s.Text));
        Assert.False(a.Laden().Ruhend);
    }

    [Fact]
    public async Task Scheitert_die_Automatik_bleibt_das_Geraet_unberuehrt_und_kein_Merker_zurueck()
    {
        // Befund B2: vorher blieb die Automatik aus, und danach ging sie nie mehr an.
        var a = Standardanlage();
        a.AutomatikKaputt.Add(ZeltRuheArten.Co2);

        var schritte = await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Co2, "select.co2")], default);

        var s = Assert.Single(schritte);
        Assert.False(s.Ok);
        Assert.Contains("unberührt", s.Text);
        Assert.DoesNotContain(a.Protokoll, p => p.StartsWith("schalten select.co2"));
        var e = a.Laden();
        Assert.False(e.Ruhend);
        Assert.Empty(e.Gemerkt);
    }

    [Fact]
    public async Task Teilfehler_ein_Geraet_klappt_das_andere_antwortet_nicht_und_laesst_sich_nachholen()
    {
        var a = Standardanlage();
        a.Zustand.Remove("select.co2"); // antwortet nicht

        var erster = await a.Ablauf().RuhendAsync(LichtCo2Kuehler, default);
        Assert.Equal([true, false, true], erster.Select(s => s.Ok).ToArray());
        var e = a.Laden();
        Assert.True(e.Ruhend);
        Assert.DoesNotContain(e.Gemerkt, m => m.Kennung == ZeltRuheArten.Co2);

        // Das fehlende Gerät kommt später dazu, ohne dass die anderen noch einmal angefasst werden.
        a.Zustand["select.co2"] = "On";
        a.Protokoll.Clear();
        var zweiter = await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Co2, "select.co2")], default);
        Assert.True(Assert.Single(zweiter).Ok);
        Assert.DoesNotContain(a.Protokoll, p => p.Contains("select.licht"));
        Assert.Equal(3, a.Laden().Gemerkt.Count);
    }

    [Fact]
    public async Task Kein_Merker_wenn_nichts_geschaltet_wurde()
    {
        var a = Standardanlage();
        a.Zustand["select.licht"] = null; // antwortet nicht
        a.Zustand.Remove("select.co2");

        var schritte = await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Licht, "select.licht"), Art(ZeltRuheArten.Co2, "select.co2")], default);

        Assert.All(schritte, s => Assert.False(s.Ok));
        var e = a.Laden();
        Assert.False(e.Ruhend, "Was nicht geschaltet wurde, darf nicht als ruhend gelten.");
        Assert.Empty(e.Gemerkt);
        Assert.Null(e.SeitUtc);
        Assert.DoesNotContain("waechter-pause", a.Protokoll);
    }

    [Fact]
    public async Task Abgelehnter_Befehl_ohne_Regelung_hinterlaesst_keinen_Merker_mit_Regelung_bleibt_er()
    {
        var a = Standardanlage();
        a.SchaltenAbgelehnt.Add("select.licht");
        a.SchaltenAbgelehnt.Add("select.co2");

        var schritte = await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Licht, "select.licht"), Art(ZeltRuheArten.Co2, "select.co2")], default);

        Assert.All(schritte, s => Assert.False(s.Ok));
        var e = a.Laden();
        // Das Licht hat keine Regelung: nichts hat sich geändert, also nichts zurückzustellen.
        Assert.DoesNotContain(e.Gemerkt, m => m.Kennung == ZeltRuheArten.Licht);
        // CO₂: die Automatik IST aus — beim Wecken muss sie zurück.
        Assert.Contains(e.Gemerkt, m => m.Kennung == ZeltRuheArten.Co2 && m.AutomatikWarAn == true);
        Assert.True(e.Ruhend);
    }

    [Fact]
    public async Task Unbestaetigt_bleibt_gemerkt_und_wird_nicht_als_Erfolg_gemeldet()
    {
        var a = Standardanlage();
        a.SchaltenUnbestaetigt.Add("select.licht");

        var s = Assert.Single(await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Licht, "select.licht")], default));

        Assert.False(s.Ok);
        Assert.Contains("nicht bestätigt", s.Text);
        Assert.Contains(a.Laden().Gemerkt, m => m.Kennung == ZeltRuheArten.Licht);
    }

    [Fact]
    public async Task Der_Kuehler_Regler_des_Forks_geht_mit_aus_und_beim_Wecken_nur_er_wieder_an()
    {
        // Befund B4: der KuehlerWorker schaltet die Steckdose sonst binnen einer Minute wieder ein.
        var a = Standardanlage();
        await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Chiller, "switch.kuehler")], default);

        Assert.Contains("kuehler-regler-aus", a.Protokoll);
        Assert.Equal([7], a.Laden().Gemerkt.Single().ChillerZelte);

        a.Protokoll.Clear();
        var weg = await a.Ablauf().AktivAsync(default);

        Assert.True(Assert.Single(weg).Ok);
        Assert.Contains("kuehler-regler-an 7", a.Protokoll);
        // Der Regler schaltet mit seinen Schutzzeiten selbst: das Gerät wird nicht direkt eingeschaltet.
        Assert.DoesNotContain(a.Protokoll, p => p.StartsWith("schalten switch.kuehler"));
        Assert.False(a.Laden().Ruhend);
    }

    [Fact]
    public async Task Wecken_bringt_bei_geregelten_Geraeten_nur_die_Regelung_zurueck_und_stellt_Geraete_ohne_Regelung_direkt_her()
    {
        var a = Standardanlage();
        await a.Ablauf().RuhendAsync(LichtCo2Kuehler, default);
        a.Protokoll.Clear();

        await a.Ablauf().AktivAsync(default);

        // Licht: keine Regelung → genau der Zustand von vorher.
        Assert.Contains("schalten select.licht select_option Schedule", a.Protokoll);
        // CO₂: eine Automatik führt das Ventil (mit Schutzzeiten) → nur sie kommt zurück, das Ventil öffnet nicht von selbst.
        Assert.Contains("automatik-an co2", a.Protokoll);
        Assert.DoesNotContain(a.Protokoll, p => p.StartsWith("schalten select.co2"));
        Assert.Contains("waechter-weiter", a.Protokoll);
    }

    [Fact]
    public async Task War_die_Automatik_schon_vorher_aus_bleibt_sie_aus_und_das_Geraet_geht_in_seinen_alten_Zustand()
    {
        var a = Standardanlage();
        a.AutomatikWar[ZeltRuheArten.Co2] = false;
        await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Co2, "select.co2")], default);
        a.Protokoll.Clear();

        await a.Ablauf().AktivAsync(default);

        Assert.DoesNotContain("automatik-an co2", a.Protokoll);
        Assert.Contains("schalten select.co2 select_option On", a.Protokoll);
    }

    [Fact]
    public async Task Fehler_beim_Wecken_bleiben_gemerkt_und_ein_zweiter_Versuch_holt_sie_nach()
    {
        var a = Standardanlage();
        await a.Ablauf().RuhendAsync(LichtCo2Kuehler, default);
        a.AutomatikKaputt.Add(ZeltRuheArten.Co2);
        a.SchaltenAbgelehnt.Add("select.licht");

        var erster = await a.Ablauf().AktivAsync(default);

        Assert.Equal(2, erster.Count(s => !s.Ok));
        var e = a.Laden();
        Assert.True(e.Ruhend);
        Assert.Equal([ZeltRuheArten.Licht, ZeltRuheArten.Co2], e.Gemerkt.Select(m => m.Kennung).ToArray());
        // Die Lampe ist noch nicht wieder in ihrem Zustand: der Wächter bleibt angehalten (Befund S5).
        Assert.DoesNotContain("waechter-weiter", a.Protokoll);

        a.AutomatikKaputt.Clear();
        a.SchaltenAbgelehnt.Clear();
        a.Protokoll.Clear();
        var zweiter = await a.Ablauf().AktivAsync(default);

        Assert.All(zweiter, s => Assert.True(s.Ok, s.Text));
        Assert.False(a.Laden().Ruhend);
        Assert.Contains("waechter-weiter", a.Protokoll);
        // Der Kühler, der beim ersten Mal schon stand, wird nicht noch einmal angefasst.
        Assert.DoesNotContain(a.Protokoll, p => p.Contains("kuehler"));
    }

    [Fact]
    public async Task Ein_Geraet_das_schon_aus_war_bleibt_nach_dem_Wecken_aus()
    {
        var a = Standardanlage();
        a.Zustand["select.licht"] = "Off";
        await a.Ablauf().RuhendAsync([Art(ZeltRuheArten.Licht, "select.licht")], default);
        a.Protokoll.Clear();

        await a.Ablauf().AktivAsync(default);

        Assert.Contains("schalten select.licht select_option Off", a.Protokoll);
    }

    [Fact]
    public async Task Ohne_Merker_schaltet_Wecken_nichts()
    {
        var a = Standardanlage();
        var schritte = await a.Ablauf().AktivAsync(default);
        Assert.Empty(schritte);
        Assert.Empty(a.Protokoll);
    }
}
