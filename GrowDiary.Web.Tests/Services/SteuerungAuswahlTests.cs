using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;

namespace GrowDiary.Web.Tests.Services;

/// <summary>
/// Fork AI (A-016, Etappe 2): Welche Steuerungen der Nutzer hat — echte Datenbank, kein Mock.
/// </summary>
[Collection(Co2ZelteRegisterCollection.Name)]
public sealed class SteuerungAuswahlTests : IDisposable
{
    private readonly string _temp;
    private readonly AppSettingsRepository _einstellungen;
    private readonly SteuerungRepository _steuerung;
    private readonly SteuerungAuswahlService _auswahl;

    public SteuerungAuswahlTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "Auswahl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        var pfade = new AppPaths(_temp);
        TestDatabase.Initialize(pfade);
        _einstellungen = new AppSettingsRepository(pfade);
        _steuerung = new SteuerungRepository(pfade);
        _auswahl = new SteuerungAuswahlService(_einstellungen, _steuerung);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* Aufräumen darf scheitern. */ }
    }

    [Fact]
    public void EineFrischeAnlageHatKeineSteuerungGewaehlt()
    {
        // Ohne Zuordnung und ohne Auswahl: nichts. Die Rückfall-Vorgaben der Rollen zählen nicht
        // (sonst wäre jede Steuerung auf einer neuen Anlage „gewählt").
        Assert.Empty(_auswahl.Gewaehlt());
        Assert.False(_auswahl.Gespeichert());
    }

    [Fact]
    public void EineBestandsanlageBehaeltIhreSteuerungenOhneEtwasZuSpeichern()
    {
        _steuerung.SetGeraet("co2", "co2_sensor", "sensor.irgendein_co2");
        _steuerung.SetGeraet(EntfeuchterZusatzSteuerungService.Modul, "zusatz_schalter", "switch.zweiter");
        _steuerung.SetGeraet("licht", "licht_modus", "select.lampe");

        // Der Zusatz-Entfeuchter gehört zur Auswahl „Entfeuchter".
        Assert.Equal(["co2", "entfeuchter", "licht"], _auswahl.Gewaehlt());
        Assert.False(_auswahl.Gespeichert());
    }

    [Fact]
    public void EineLeereZuordnungZaehltNicht()
    {
        _steuerung.SetGeraet("zuluft", "aussen_temp", "sensor.x");
        _steuerung.SetGeraet("zuluft", "aussen_temp", null); // wieder entfernt
        Assert.Empty(_auswahl.Gewaehlt());
    }

    [Fact]
    public void DieGespeicherteAuswahlGiltWoertlichAuchGegenZugeordneteGeraete()
    {
        _steuerung.SetGeraet("co2", "co2_sensor", "sensor.irgendein_co2");

        Assert.Empty(_auswahl.Speichern(["chiller"]));

        Assert.Equal(["chiller"], _auswahl.Gewaehlt());
        Assert.True(_auswahl.Gespeichert());
    }

    [Fact]
    public void EineLeereAuswahlIstEineAuswahl()
    {
        // „Ich habe nichts" ist eine Antwort — sie darf nicht als „nie gewählt" gelten und
        // auf die zugeordneten Geräte zurückfallen.
        _steuerung.SetGeraet("co2", "co2_sensor", "sensor.irgendein_co2");
        Assert.Empty(_auswahl.Speichern([]));

        Assert.Empty(_auswahl.Gewaehlt());
        Assert.True(_auswahl.Gespeichert());
    }

    [Fact]
    public void UnbekannteKennungenWerdenAbgelehntUndNichtGespeichert()
    {
        var unbekannt = _auswahl.Speichern(["co2", "kaffeemaschine"]);

        Assert.Equal(["kaffeemaschine"], unbekannt);
        Assert.False(_auswahl.Gespeichert());
    }

    [Fact]
    public void Doppeltes_Und_Leeres_WirdBereinigt()
    {
        Assert.Empty(_auswahl.Speichern(["licht", " licht ", "", "co2"]));
        Assert.Equal(["co2", "licht"], _auswahl.Gewaehlt()); // Reihenfolge der Übersicht, nicht der Eingabe
    }

    [Fact]
    public void EinKaputterEintragBlendetNichtAlleAus()
    {
        _einstellungen.SetValue(SteuerungAuswahlService.Schluessel, "{kein json");
        _steuerung.SetGeraet("zuluft", "aussen_temp", "sensor.x");

        Assert.False(_auswahl.Gespeichert());
        Assert.Equal(["zuluft"], _auswahl.Gewaehlt());
    }

    [Theory]
    [InlineData("co2", "co2", true)]
    [InlineData("abluft", "co2", true)]           // die Abluft-Zeile gehört zur CO₂-Begasung
    [InlineData("abluft", "zuluft", false)]
    [InlineData("entfeuchter-zusatz", "entfeuchter", true)]
    [InlineData("entfeuchter", "co2", false)]
    public void DieUebersichtsZeilenFolgenIhrerSteuerung(string zeile, string gewaehlt, bool sichtbar)
        => Assert.Equal(sichtbar, SteuerungAuswahlService.Sichtbar(zeile, [gewaehlt]));

    [Fact]
    public void JedeZeileDerUebersichtGehoertZuGenauEinerSteuerung()
    {
        // Sonst bliebe eine Zeile für immer unsichtbar oder erschiene doppelt.
        var kennungen = new[] { "co2", "abluft", "entfeuchter", "entfeuchter-zusatz", "zuluft", "chiller", "licht" };
        foreach (var k in kennungen)
        {
            Assert.Single(SteuerungAuswahlService.Alle, a => a.Uebersicht.Contains(k));
        }
    }

    [Fact]
    public void JedesModulDesKatalogsGehoertZuEinerSteuerung()
    {
        // Ein Modul mit Rollen, das in keiner Steuerung vorkommt, ließe sich nie auswählen.
        var vergeben = SteuerungAuswahlService.Alle.SelectMany(a => a.Module).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ohne = SteuerungGeraeteRollen.Module.Where(m => !vergeben.Contains(m)).ToList();
        // Bluelab ist ein Zusatz am Gerätealarm, keine Steuerung (eigene Seite); der Raum ist keine Steuerung, sondern
        // der Ort, an dem Zuluft und Entfeuchter hängen (eigener Reiter „Räume“, forkai.241).
        Assert.Equal(["raum", "bluelab"], ohne);
    }

    [Fact]
    public void DiePflichtrollenWerdenGezaehlt()
    {
        var art = SteuerungAuswahlService.Finden("zuluft")!;
        var (davor, gesamt) = _auswahl.Pflichtrollen(art);
        Assert.Equal(0, davor);
        Assert.True(gesamt >= 5);

        _steuerung.SetGeraet("zuluft", "aussen_temp", "sensor.draussen");
        Assert.Equal((1, gesamt), _auswahl.Pflichtrollen(art));
    }

    private static readonly string[] HauptPflicht =
        SteuerungGeraeteRollen.FuerModul("entfeuchter").Where(r => r.Pflicht).Select(r => r.Schluessel).ToArray();

    private void HauptGeraetVollstaendigZuordnen()
    {
        // Mengenwächter: ohne Pflichtrollen liefe jede Zählung unten null Mal und wäre grün.
        Assert.True(HauptPflicht.Length >= 5, "Der Entfeuchter hat Pflichtrollen.");
        foreach (var rolle in HauptPflicht) _steuerung.SetGeraet("entfeuchter", rolle, $"sensor.test_{rolle}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void EinEntfeuchterOhneZusatzIstMitSeinenPflichtrollenVollstaendig(bool? zusatzVorhanden)
    {
        // Neuling-Test 17.10.2026: „5 von 7 Pflichtgeräten", obwohl alles Sichtbare zugeordnet war — die
        // Pflichtrollen des Zusatz-Entfeuchters zählten mit, auch bei „kein Zusatz".
        _steuerung.SetEinstellungen(EntfeuchterZusatzSteuerungService.EinrichtungModul, new EntfeuchtungEinrichtung { ZusatzVorhanden = zusatzVorhanden });
        HauptGeraetVollstaendigZuordnen();
        var art = SteuerungAuswahlService.Finden("entfeuchter")!;

        Assert.False(_auswahl.ZusatzGilt());
        Assert.Equal((HauptPflicht.Length, HauptPflicht.Length), _auswahl.Pflichtrollen(art));
        Assert.Empty(_auswahl.FehlendePflichtrollen(art));
    }

    [Fact]
    public void MitZweitemEntfeuchterZaehlenDessenPflichtrollenMit()
    {
        _steuerung.SetEinstellungen(EntfeuchterZusatzSteuerungService.EinrichtungModul, new EntfeuchtungEinrichtung { ZusatzVorhanden = true });
        HauptGeraetVollstaendigZuordnen();
        var art = SteuerungAuswahlService.Finden("entfeuchter")!;
        var zusatzPflicht = SteuerungGeraeteRollen.FuerModul(EntfeuchterZusatzSteuerungService.Modul).Where(r => r.Pflicht).ToList();
        Assert.True(zusatzPflicht.Count >= 2, "Der Zusatz hat Pflichtrollen.");

        Assert.True(_auswahl.ZusatzGilt());
        Assert.Equal((HauptPflicht.Length, HauptPflicht.Length + zusatzPflicht.Count), _auswahl.Pflichtrollen(art));
        // Die Meldung nennt, was fehlt — mit den Namen, die der Nutzer auf der Seite liest.
        Assert.Equal(zusatzPflicht.Select(r => r.Label), _auswahl.FehlendePflichtrollen(art));

        foreach (var r in zusatzPflicht) _steuerung.SetGeraet(EntfeuchterZusatzSteuerungService.Modul, r.Schluessel, "switch.zweiter");
        Assert.Equal((HauptPflicht.Length + zusatzPflicht.Count, HauptPflicht.Length + zusatzPflicht.Count), _auswahl.Pflichtrollen(art));
        Assert.Empty(_auswahl.FehlendePflichtrollen(art));
    }

    [Fact]
    public void FehlendePflichtrollenNennenDieNamenDerLuecken()
    {
        var art = SteuerungAuswahlService.Finden("entfeuchter")!;
        var erste = HauptPflicht[0];
        _steuerung.SetGeraet("entfeuchter", erste, "sensor.x");

        var fehlt = _auswahl.FehlendePflichtrollen(art);
        var name = SteuerungGeraeteRollen.Finden("entfeuchter", erste)!.Label;

        Assert.Equal(HauptPflicht.Length - 1, fehlt.Count);
        Assert.DoesNotContain(name, fehlt);
        Assert.All(fehlt, f => Assert.False(string.IsNullOrWhiteSpace(f)));
    }

    [Fact]
    public void BausteineGibtEsNurFuerModuleMitKatalogUndInAnlegeReihenfolge()
    {
        Assert.Equal(["entfeuchter", EntfeuchterZusatzSteuerungService.Modul],
            SteuerungAuswahlService.Bausteinmodule(SteuerungAuswahlService.Finden("entfeuchter")!));
        Assert.Equal(["co2"], SteuerungAuswahlService.Bausteinmodule(SteuerungAuswahlService.Finden("co2")!));
        // Die Lampe hat Rollen, aber keine Bausteine — bei ihr genügt die Zuordnung.
        Assert.Empty(SteuerungAuswahlService.Bausteinmodule(SteuerungAuswahlService.Finden("licht")!));
    }

    [Fact]
    public void DieCo2BegasungUmfasstJedesZeltMitCo2UndLegtFuerJedesBausteineAn()
    {
        var vorher = Co2Zelte.Aktuell;
        try
        {
            var art = SteuerungAuswahlService.Finden("co2")!;

            Co2Zelte.Festlegen(null, []);
            Assert.Equal(["co2"], SteuerungAuswahlService.ModuleVon(art)); // noch kein Zelt: wie bisher

            Co2Zelte.Festlegen(1, [1, 3]);
            Assert.Equal(["co2", "co2-z3"], SteuerungAuswahlService.ModuleVon(art));
            Assert.Equal(["co2", "co2-z3"], SteuerungAuswahlService.Bausteinmodule(art));
            // Andere Steuerungen bleiben, wie sie sind.
            Assert.Equal(["zuluft"], SteuerungAuswahlService.ModuleVon(SteuerungAuswahlService.Finden("zuluft")!));
        }
        finally { Co2Zelte.Festlegen(vorher.BestandZeltId, vorher.ZeltIds); }
    }

    [Fact]
    public void DiePflichtgeraeteZaehlenJeZeltUndDieMeldungNenntDasZelt()
    {
        var vorher = Co2Zelte.Aktuell;
        try
        {
            Co2Zelte.Festlegen(1, [1, 3]);
            var art = SteuerungAuswahlService.Finden("co2")!;
            var proZelt = SteuerungGeraeteRollen.FuerModul("co2").Count(r => r.Pflicht);
            Assert.True(proZelt > 0);

            Assert.Equal((0, 2 * proZelt), _auswahl.Pflichtrollen(art));

            // Eine Zuordnung am zweiten Zelt zählt nur dort — und nicht für das Bestand-Zelt.
            var rolle = SteuerungGeraeteRollen.FuerModul("co2").First(r => r.Pflicht);
            _steuerung.SetGeraet("co2-z3", rolle.Schluessel, "sensor.eigene_flasche");
            Assert.Equal((1, 2 * proZelt), _auswahl.Pflichtrollen(art));
            var fehlt = _auswahl.FehlendePflichtrollen(art);
            Assert.Contains($"{rolle.Label} (Zelt 1)", fehlt);
            Assert.DoesNotContain($"{rolle.Label} (Zelt 3)", fehlt);

            // Ein Zelt mit CO₂ allein: keine Zelt-Angabe in der Meldung.
            Co2Zelte.Festlegen(1, [1]);
            Assert.All(_auswahl.FehlendePflichtrollen(art), f => Assert.DoesNotContain("(Zelt", f));
        }
        finally { Co2Zelte.Festlegen(vorher.BestandZeltId, vorher.ZeltIds); }
    }
}
