using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

/// <summary>
/// Fork AI (A-029, Etappe 3, Teil B): Folgen die Geräte am Trockenzelt dem Plan — und was stand vorher an den Grenzen?
/// </summary>
/// <remarks>
/// Zwei eigene Tabellen mit dem Zelt als Schlüssel. <c>ForkTrocknungSteuerung</c> hält Betriebsart und die beiden Schalter
/// „Folgt dem Plan“; <c>ForkTrocknungMerker</c> hält je Rolle den Wert, der vor der ersten Übernahme am Gerät stand, damit
/// „Alte Werte zurück“ ihn wiedereinsetzen kann.
/// </remarks>
public sealed class TrocknungSteuerungRepository : RepositoryBase
{
    public const string BetriebMelden = "melden";
    public const string BetriebRegeln = "regeln";

    public sealed record Einstellung(string Betrieb, bool EntfeuchterFolgt, bool KuehlungFolgt)
    {
        public static Einstellung Standard { get; } = new(BetriebMelden, false, false);

        public bool Folgt(string rolle) => rolle == "entfeuchter" ? EntfeuchterFolgt : rolle == "kuehlung" && KuehlungFolgt;
    }

    public sealed record Merker(string Rolle, double Vorher, double Gesetzt, DateTime GesetztAtUtc);

    public TrocknungSteuerungRepository(AppPaths paths) : base(paths)
    {
    }

    private SqliteConnection Open()
    {
        var connection = OpenConnection();
        EigenesSchema.Sicherstellen(nameof(TrocknungSteuerungRepository), Paths.DatabasePath, () => SchemaAnlegen(connection));
        return connection;
    }

    private static void SchemaAnlegen(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ForkTrocknungSteuerung (
                TentId INTEGER NOT NULL PRIMARY KEY,
                Betrieb TEXT NOT NULL,
                EntfeuchterFolgt INTEGER NOT NULL,
                KuehlungFolgt INTEGER NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ForkTrocknungMerker (
                TentId INTEGER NOT NULL,
                Rolle TEXT NOT NULL,
                Vorher REAL NOT NULL,
                Gesetzt REAL NOT NULL,
                GesetztAtUtc TEXT NOT NULL,
                PRIMARY KEY (TentId, Rolle)
            );
            """;
        command.ExecuteNonQuery();
    }

    public Einstellung Get(int tentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Betrieb, EntfeuchterFolgt, KuehlungFolgt FROM ForkTrocknungSteuerung WHERE TentId = $tentId;";
        command.Parameters.AddWithValue("$tentId", tentId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new Einstellung(reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0)
            : Einstellung.Standard;
    }

    public void Set(int tentId, Einstellung einstellung)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ForkTrocknungSteuerung (TentId, Betrieb, EntfeuchterFolgt, KuehlungFolgt, UpdatedAtUtc)
            VALUES ($tentId, $betrieb, $e, $k, $jetzt)
            ON CONFLICT(TentId) DO UPDATE SET Betrieb = excluded.Betrieb, EntfeuchterFolgt = excluded.EntfeuchterFolgt,
                KuehlungFolgt = excluded.KuehlungFolgt, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$tentId", tentId);
        command.Parameters.AddWithValue("$betrieb", einstellung.Betrieb);
        command.Parameters.AddWithValue("$e", einstellung.EntfeuchterFolgt ? 1 : 0);
        command.Parameters.AddWithValue("$k", einstellung.KuehlungFolgt ? 1 : 0);
        command.Parameters.AddWithValue("$jetzt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>Alle Zelte, die gerade auf „Regeln“ stehen — der Hintergrundlauf geht nur diese durch.</summary>
    public IReadOnlyList<int> ZelteImBetrieb(string betrieb)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TentId FROM ForkTrocknungSteuerung WHERE Betrieb = $betrieb;";
        command.Parameters.AddWithValue("$betrieb", betrieb);
        using var reader = command.ExecuteReader();
        var zelte = new List<int>();
        while (reader.Read()) zelte.Add(reader.GetInt32(0));
        return zelte;
    }

    public IReadOnlyDictionary<string, Merker> Merkers(int tentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Rolle, Vorher, Gesetzt, GesetztAtUtc FROM ForkTrocknungMerker WHERE TentId = $tentId;";
        command.Parameters.AddWithValue("$tentId", tentId);
        using var reader = command.ExecuteReader();
        var ergebnis = new Dictionary<string, Merker>(StringComparer.Ordinal);
        while (reader.Read())
        {
            ergebnis[reader.GetString(0)] = new Merker(
                reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2),
                DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }

        return ergebnis;
    }

    /// <summary>
    /// Hält fest, was gesetzt wurde. <paramref name="vorher"/> gilt nur bei der ERSTEN Übernahme: Ein späteres Nachziehen
    /// (neue Etappe) lässt den ursprünglichen Wert stehen — „Alte Werte zurück“ soll auf den Wert des Nutzers zurückführen,
    /// nicht auf die Etappe davor.
    /// </summary>
    public void Gesetzt(int tentId, string rolle, double vorher, double gesetzt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ForkTrocknungMerker (TentId, Rolle, Vorher, Gesetzt, GesetztAtUtc) VALUES ($tentId, $rolle, $vorher, $gesetzt, $jetzt)
            ON CONFLICT(TentId, Rolle) DO UPDATE SET Gesetzt = excluded.Gesetzt, GesetztAtUtc = excluded.GesetztAtUtc;
            """;
        command.Parameters.AddWithValue("$tentId", tentId);
        command.Parameters.AddWithValue("$rolle", rolle);
        command.Parameters.AddWithValue("$vorher", vorher);
        command.Parameters.AddWithValue("$gesetzt", gesetzt);
        command.Parameters.AddWithValue("$jetzt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void MerkerLoeschen(int tentId, string rolle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ForkTrocknungMerker WHERE TentId = $tentId AND Rolle = $rolle;";
        command.Parameters.AddWithValue("$tentId", tentId);
        command.Parameters.AddWithValue("$rolle", rolle);
        command.ExecuteNonQuery();
    }
}
