using GrowDiary.Web.Api.Controllers;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Models;
using GrowDiary.Web.Services;
using GrowDiary.Web.Services.GrowPlan;
using GrowDiary.Web.Services.Knowledge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GrowDiary.Web.Tests.Api;

/// <summary>
/// Programme ohne Grow ansehen und bearbeiten (A-029, Etappe 1).
/// </summary>
/// <remarks>
/// <para><b>Der Anlass (30.10.2026).</b> Zwischen zwei Grows zeigte die Seite „Plan" nur
/// „Kein laufender Grow". SKX, Canna Aqua und eigene Programme waren nirgends änderbar —
/// Bearbeiten ging nur über den Plan eines laufenden Grows.</para>
///
/// <para><b>Was gilt.</b> Ein mitgeliefertes Programm bleibt unverändert; wer es ändert,
/// bekommt eine Kopie als eigenes Programm. Ein eigenes wird an Ort und Stelle geändert.
/// Dieselben Prüfregeln wie am Grow (Bereich, „von" nicht über „bis", EC im Band).</para>
/// </remarks>
public sealed class ProgrammeApiTests : IDisposable
{
    private const string Mitgeliefert = "skx-canna-aqua";

    private readonly string _wurzel;
    private readonly KnowledgeBaseLoader _wissen;
    private readonly WochenwertRepository _wochenwerte;
    private readonly WochenwertUeberlagerung _ueberlagerung;
    private readonly ProgrammeApiController _api;

    public ProgrammeApiTests()
    {
        _wurzel = Path.Combine(Path.GetTempPath(), "ProgrammeApi_" + Guid.NewGuid().ToString("N"));
        var pfade = new AppPaths(_wurzel);
        KopiereWissen(_wurzel);
        TestDatabase.Initialize(pfade);
        _wochenwerte = new WochenwertRepository(pfade);
        _ueberlagerung = new WochenwertUeberlagerung(_wochenwerte, NullLogger<WochenwertUeberlagerung>.Instance);
        _wissen = new KnowledgeBaseLoader(pfade, NullLogger<KnowledgeBaseLoader>.Instance) { NachDemLaden = _ueberlagerung.Anwenden };
        _wissen.Initialize();
        _api = new ProgrammeApiController(
            _wissen,
            new EigeneProgramme(pfade, _wissen, NullLogger<EigeneProgramme>.Instance),
            _ueberlagerung,
            _wochenwerte,
            new GrowRepository(pfade));
    }

    public void Dispose()
    {
        try { Directory.Delete(_wurzel, recursive: true); } catch { }
    }

    private ActionResult<ProgrammWerteGespeichertDto> Speichern(string id, ProgrammWerteSpeichernRequest anfrage)
        => _api.WerteSpeichern(id, anfrage, CancellationToken.None).GetAwaiter().GetResult();

    private static T Wert<T>(ActionResult<T> antwort)
        => Assert.IsType<OkObjectResult>(antwort.Result).Value is T wert ? wert : throw new InvalidOperationException("Falscher Antworttyp.");

    private double? Luft(string programmId, string spalteId)
        => _wissen.NutrientPrograms.First(p => p.Id == programmId).FeedChart!.Columns.First(s => s.Id == spalteId).AirTempC;

    private string ErsteSpalte(string programmId)
        => _wissen.NutrientPrograms.First(p => p.Id == programmId).FeedChart!.Columns[0].Id;

    private static ProgrammWerteSpeichernRequest Aendern(string spalte, string feld, double? wert, string? name = null)
        => new() { Name = name, Aenderungen = [new WochenwertAenderung { SpalteId = spalte, Feld = feld, Wert = wert }] };

    // ---- Auswahl ------------------------------------------------------------------------

    [Fact]
    public void Die_Auswahl_nennt_Wochen_und_ob_Klima_dabei_ist()
    {
        var liste = Wert(_api.Liste());

        var skx = liste.Single(p => p.Id == Mitgeliefert);
        Assert.False(skx.Eigen);
        Assert.True(skx.Wochen > 0);
        Assert.True(skx.MitKlima);

        // Programme ohne Wochenraster stehen trotzdem in der Auswahl — die Seite sagt, warum nichts zu bearbeiten ist.
        Assert.Contains(liste, p => p.Wochen == 0);
        Assert.All(liste.Where(p => p.Eigen), p => Assert.StartsWith("eigen-", p.Id));
    }

    [Fact]
    public void Ein_Programm_ohne_Wochenraster_hat_keine_Werte()
    {
        var ohneRaster = Wert(_api.Liste()).First(p => p.Wochen == 0).Id;

        Assert.IsType<NotFoundObjectResult>(_api.Werte(ohneRaster).Result);
        Assert.IsType<NotFoundObjectResult>(_api.Werte("gibt-es-nicht").Result);
    }

    [Fact]
    public void Die_Werte_zeigen_jede_Woche_mit_Wert_und_Planwert()
    {
        var dto = Wert(_api.Werte(Mitgeliefert));

        Assert.Equal(Mitgeliefert, dto.ProgrammId);
        Assert.False(dto.Eigen);
        Assert.Equal(0, dto.AndereGrows);
        Assert.NotEmpty(dto.Spalten);
        Assert.All(dto.Spalten, s =>
        {
            Assert.False(s.IstJetzt); // ohne Grow gibt es keine „laufende" Woche
            var luft = s.Felder.Single(f => f.Feld == "airTempC");
            Assert.Equal(luft.Wert, luft.Plan);
            Assert.False(luft.Geaendert);
        });
    }

    // ---- Mitgeliefertes ändern = Kopie ----------------------------------------------------

    [Fact]
    public void Ein_mitgeliefertes_Programm_bleibt_unveraendert_und_die_Aenderung_landet_in_einer_Kopie()
    {
        var woche = ErsteSpalte(Mitgeliefert);
        var vorher = Luft(Mitgeliefert, woche);

        var antwort = Wert(Speichern(Mitgeliefert, Aendern(woche, "airTempC", 21.5, "SKX · langsam")));

        Assert.True(antwort.NeuAngelegt);
        Assert.StartsWith("eigen-", antwort.Werte.ProgrammId);
        Assert.Equal("SKX · langsam", antwort.Werte.ProgrammName);
        Assert.Equal(21.5, Luft(antwort.Werte.ProgrammId, woche));
        // Das Original: Wert, Datei und Auswahl unberührt.
        Assert.Equal(vorher, Luft(Mitgeliefert, woche));
        Assert.Contains(Wert(_api.Liste()), p => p.Id == Mitgeliefert && !p.Eigen);
        Assert.Contains(Wert(_api.Liste()), p => p.Id == antwort.Werte.ProgrammId && p.Eigen);
    }

    [Fact]
    public void Ohne_Namen_heisst_die_Kopie_wie_die_Vorlage_mit_Zusatz_eigen()
    {
        var woche = ErsteSpalte(Mitgeliefert);

        var antwort = Wert(Speichern(Mitgeliefert, Aendern(woche, "airTempC", 22, null)));

        Assert.EndsWith("(eigen)", antwort.Werte.ProgrammName);
    }

    // ---- Eigenes ändern = an Ort und Stelle -------------------------------------------------

    [Fact]
    public void Ein_eigenes_Programm_wird_an_Ort_und_Stelle_geaendert()
    {
        var woche = ErsteSpalte(Mitgeliefert);
        var kopie = Wert(Speichern(Mitgeliefert, Aendern(woche, "airTempC", 21.5, "Meins"))).Werte.ProgrammId;
        var anzahl = Wert(_api.Liste()).Count;

        var antwort = Wert(Speichern(kopie, Aendern(woche, "airTempC", 23, null)));

        Assert.False(antwort.NeuAngelegt);
        Assert.Equal(kopie, antwort.Werte.ProgrammId);
        Assert.Equal(23, Luft(kopie, woche));
        Assert.Equal(anzahl, Wert(_api.Liste()).Count); // keine dritte Kopie
    }

    [Fact]
    public void Ein_Feld_ohne_Wert_bleibt_unveraendert()
    {
        // Eine Kopie führt keine Verbindung zur Vorlage zurück: „kein Wert“ heißt hier „nicht anfassen“.
        var woche = ErsteSpalte(Mitgeliefert);
        var vorlage = Luft(Mitgeliefert, woche);

        var kopie = Wert(Speichern(Mitgeliefert, Aendern(woche, "airTempC", null, "Meins"))).Werte.ProgrammId;
        Assert.Equal(vorlage, Luft(kopie, woche));

        Wert(Speichern(kopie, Aendern(woche, "airTempC", 25)));
        Wert(Speichern(kopie, Aendern(woche, "airTempC", null)));
        Assert.Equal(25, Luft(kopie, woche));
    }

    [Fact]
    public void Eine_aeltere_Abweichung_verdeckt_den_neuen_Wert_nicht()
    {
        var woche = ErsteSpalte(Mitgeliefert);
        var kopie = Wert(Speichern(Mitgeliefert, Aendern(woche, "airTempC", 21, "Meins"))).Werte.ProgrammId;
        // Der alte Weg (Grow ohne Plan) legte Abweichungen über das Programm.
        _wochenwerte.Speichern(kopie, [(woche, "airTempC", (double?)19)]);
        _ueberlagerung.Auffrischen();

        Wert(Speichern(kopie, Aendern(woche, "airTempC", 24, null)));

        Assert.Equal(24, Luft(kopie, woche));
        Assert.DoesNotContain(_wochenwerte.Alle(), w => w.ProgrammId == kopie);
    }

    // ---- Prüfregeln -----------------------------------------------------------------------

    [Theory]
    [InlineData("airTempC", 99)]   // über dem Bereich
    [InlineData("airTempC", -5)]   // unter dem Bereich
    [InlineData("gibtEsNicht", 1)] // kein bearbeitbares Feld
    public void Ungueltiges_wird_abgelehnt_und_nichts_wird_angelegt(string feld, double wert)
    {
        var vorher = Wert(_api.Liste()).Count;

        var antwort = Speichern(Mitgeliefert, Aendern(ErsteSpalte(Mitgeliefert), feld, wert, "X"));

        Assert.IsNotType<OkObjectResult>(antwort.Result);
        Assert.Equal(vorher, Wert(_api.Liste()).Count);
    }

    [Fact]
    public void Von_ueber_bis_wird_abgelehnt()
    {
        var woche = ErsteSpalte(Mitgeliefert);
        var anfrage = new ProgrammWerteSpeichernRequest
        {
            Name = "X",
            Aenderungen =
            [
                new WochenwertAenderung { SpalteId = woche, Feld = "phMin", Wert = 6.5 },
                new WochenwertAenderung { SpalteId = woche, Feld = "phMax", Wert = 5.5 },
            ],
        };

        Assert.IsNotType<OkObjectResult>(Speichern(Mitgeliefert, anfrage).Result);
    }

    [Fact]
    public void Eine_unbekannte_Woche_und_eine_leere_Liste_werden_abgelehnt()
    {
        Assert.IsNotType<OkObjectResult>(Speichern(Mitgeliefert, Aendern("woche-99", "airTempC", 22, "X")).Result);
        Assert.IsNotType<OkObjectResult>(Speichern(Mitgeliefert, new ProgrammWerteSpeichernRequest { Name = "X" }).Result);
    }

    [Fact]
    public void Ein_zu_langer_Name_wird_abgelehnt()
    {
        var antwort = Speichern(Mitgeliefert, Aendern(ErsteSpalte(Mitgeliefert), "airTempC", 22, new string('x', 81)));

        Assert.IsNotType<OkObjectResult>(antwort.Result);
    }

    [Fact]
    public void Ein_unbekanntes_Programm_wird_beim_Speichern_nicht_gefunden()
    {
        Assert.IsType<NotFoundObjectResult>(Speichern("gibt-es-nicht", Aendern("x", "airTempC", 22, "X")).Result);
    }

    [Theory]
    [InlineData(true, 1, true)]    // eigenes Programm mit laufendem Grow ohne Plan: übergeben
    [InlineData(true, 0, false)]   // niemand liest es: nichts zu übergeben
    [InlineData(false, 3, false)]  // eine neue Kopie hat noch keinen Nutzer, die Vorlage ändert sich nie
    public void Die_Uebergabe_an_Home_Assistant_nur_dort_wo_ein_laufender_Grow_mitliest(bool eigen, int andereGrows, bool erwartet)
    {
        Assert.Equal(erwartet, ProgrammeApiController.UebergabeNoetig(eigen, andereGrows));
    }

    private static void KopiereWissen(string wurzel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "GrowDiary.Web"))) dir = Path.GetDirectoryName(dir);
        var quelle = Path.Combine(dir ?? throw new InvalidOperationException("Projektwurzel nicht gefunden."),
            "GrowDiary.Web", "wwwroot", "knowledge-defaults");
        var ziel = Path.Combine(wurzel, "wwwroot", "knowledge-defaults");
        foreach (var datei in Directory.EnumerateFiles(quelle, "*.json", SearchOption.AllDirectories))
        {
            var pfad = Path.Combine(ziel, Path.GetRelativePath(quelle, datei));
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.Copy(datei, pfad);
        }
    }
}
