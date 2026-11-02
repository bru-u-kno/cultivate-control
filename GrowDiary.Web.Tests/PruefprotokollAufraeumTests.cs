using System.Globalization;
using GrowDiary.Web.Infrastructure;
using GrowDiary.Web.Services;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Tests;

/// <summary>
/// Das Prüfprotokoll bleibt klein (02.11.2026): Lesen steht nicht darin, Ändern und
/// Abweisen schon, und Altes verschwindet.
///
/// <para><b>Der Anlass.</b> 182.180 von 182.400 Zeilen waren <c>GET /api/settings/…</c>
/// (81 von 87 MB). Dieser Test stellt sicher, dass dabei nichts Wertvolles mitgeht:
/// Abweisungen, Änderungen, KI-Zugriffe und Sicherungen.</para>
/// </summary>
public sealed class PruefprotokollAufraeumTests : IDisposable
{
    private readonly string _temp;
    private readonly AppPaths _paths;
    private readonly SystemAuditRepository _audit;
    private static readonly DateTime Jetzt = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    public PruefprotokollAufraeumTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "Pruefprotokoll_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _paths = new AppPaths(_temp);
        TestDatabase.InitializeWithDefaultTent(_paths);
        _audit = new SystemAuditRepository(_paths);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }

    private void Zeile(string quelle, string aktion, string text, DateTime am)
    {
        using var verbindung = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        verbindung.Open();
        using var befehl = verbindung.CreateCommand();
        befehl.CommandText = @"INSERT INTO SystemAuditEvents (EventType, Action, Summary, Severity, Source, Success, CreatedAtUtc)
                               VALUES ('security', $a, $t, 'info', $q, 1, $am);";
        befehl.Parameters.AddWithValue("$a", aktion);
        befehl.Parameters.AddWithValue("$t", text);
        befehl.Parameters.AddWithValue("$q", quelle);
        befehl.Parameters.AddWithValue("$am", am.ToString("O", CultureInfo.InvariantCulture));
        befehl.ExecuteNonQuery();
    }

    private List<string> Texte()
    {
        using var verbindung = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        verbindung.Open();
        using var befehl = verbindung.CreateCommand();
        befehl.CommandText = "SELECT Summary FROM SystemAuditEvents ORDER BY Id;";
        using var leser = befehl.ExecuteReader();
        var liste = new List<string>();
        while (leser.Read()) liste.Add(leser.GetString(0));
        return liste;
    }

    [Theory]
    [InlineData("GET", "/api/settings/tents", true, false)]
    [InlineData("GET", "/api/grows", true, false)]
    [InlineData("HEAD", "/api/settings", true, false)]
    [InlineData("OPTIONS", "/api/settings/tents", true, false)]
    [InlineData("GET", "/api/system/backup/media-size", true, false)]
    [InlineData("GET", "/api/system/backup/sicherung.zip", true, true)]
    [InlineData("GET", "/api/exports/grows/1", true, true)]
    [InlineData("GET", "/api/settings/home-assistant", true, true)]
    [InlineData("GET", "/api/settings/ki-zugriff", true, true)]
    [InlineData("POST", "/api/settings/tents", true, true)]
    [InlineData("PUT", "/api/settings/tents/1", true, true)]
    [InlineData("DELETE", "/api/plants/1", true, true)]
    [InlineData("GET", "/api/settings/tents", false, true)]
    [InlineData("POST", "/api/settings/tents", false, true)]
    public void Erlaubtes_Lesen_steht_nicht_im_Protokoll_Aendern_Abweisen_und_sensibles_Lesen_schon(string methode, string pfad, bool erlaubt, bool erwartet)
        => Assert.Equal(erwartet, AdminAccessPolicy.ProtokolliereZugriff(methode, pfad, erlaubt));

    [Fact]
    public void Aufraeumen_loescht_Lesezugriffe_und_Altes_aber_nichts_Wertvolles()
    {
        var frisch = Jetzt.AddDays(-1);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/settings/tents", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "HEAD /api/settings/x", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "POST /api/settings/tents", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/system/backup/media-size", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/system/backup/sicherung.zip", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/exports/grows/1", frisch);
        Zeile("admin-access-middleware", "remote-admin-access-blocked", "GET /api/settings/tents", frisch);
        Zeile("system-api", "backup-created", "Sicherung angelegt", frisch);
        Zeile("system-api", "backup-created", "Alte Sicherung", Jetzt.AddDays(-100));
        Zeile("ki-zugriff", "ki-call", "KI vor 100 Tagen", Jetzt.AddDays(-100));
        Zeile("ki-zugriff", "ki-call", "KI vor 400 Tagen", Jetzt.AddDays(-400));

        var geloescht = PruefprotokollAufraeumWorker.Einmal(_audit, Jetzt);

        Assert.Equal(5, geloescht);
        Assert.Equal(
            new[]
            {
                "POST /api/settings/tents", "GET /api/system/backup/sicherung.zip", "GET /api/exports/grows/1",
                "GET /api/settings/tents", "Sicherung angelegt", "KI vor 100 Tagen"
            },
            Texte());
    }

    [Fact]
    public void Ein_zweiter_Durchlauf_loescht_nichts_mehr()
    {
        Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/settings/tents", Jetzt.AddDays(-1));
        Assert.Equal(1, PruefprotokollAufraeumWorker.Einmal(_audit, Jetzt));
        Assert.Equal(0, PruefprotokollAufraeumWorker.Einmal(_audit, Jetzt));
    }

    [Fact]
    public void Nach_grossem_Loeschen_wird_die_Datei_wirklich_kleiner()
    {
        for (var i = 0; i < PruefprotokollAufraeumWorker.VerdichtenAb + 100; i++)
        {
            Zeile("admin-access-middleware", "remote-admin-access-allowed", "GET /api/settings/tents/" + i + new string('x', 200), Jetzt.AddDays(-1));
        }
        Zeile("ki-zugriff", "ki-call", "bleibt", Jetzt.AddDays(-1));
        SqliteConnection.ClearAllPools();
        var vorher = new FileInfo(_paths.DatabasePath).Length;

        PruefprotokollAufraeumWorker.Einmal(_audit, Jetzt);
        SqliteConnection.ClearAllPools();

        Assert.True(new FileInfo(_paths.DatabasePath).Length < vorher / 2, "Die Datei ist nicht geschrumpft: VACUUM fehlt.");
        Assert.Equal(new[] { "bleibt" }, Texte());
    }
}
