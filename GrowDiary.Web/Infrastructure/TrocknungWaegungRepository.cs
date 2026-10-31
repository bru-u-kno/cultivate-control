using System.Globalization;
using GrowDiary.Web.Models;
using Microsoft.Data.Sqlite;

namespace GrowDiary.Web.Infrastructure;

/// <summary>
/// Fork AI (A-029, Etappe 4): die Wägungen der Fertig-Erkennung.
/// </summary>
/// <remarks>
/// Die Tabelle legt das Repository selbst an (wie <see cref="KostenRepository"/>): Der Abgleich mit dem Original bleibt
/// frei von Konflikten im Kern-Schema, und ein Export des Originals kennt sie nicht.
/// </remarks>
public sealed class TrocknungWaegungRepository : RepositoryBase
{
    public TrocknungWaegungRepository(AppPaths paths) : base(paths)
    {
    }

    private SqliteConnection Open()
    {
        var connection = OpenConnection();
        EigenesSchema.Sicherstellen(nameof(TrocknungWaegungRepository), Paths.DatabasePath, () => SchemaAnlegen(connection));
        return connection;
    }

    private static void SchemaAnlegen(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ForkTrocknungWaegungen (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GrowId INTEGER NOT NULL,
                ZeitpunktUtc TEXT NOT NULL,
                GewichtG REAL NOT NULL,
                GanzeCharge INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_ForkTrocknungWaegungen_Grow ON ForkTrocknungWaegungen(GrowId, ZeitpunktUtc);
            """;
        command.ExecuteNonQuery();
    }

    public int Add(TrocknungWaegung waegung)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ForkTrocknungWaegungen (GrowId, ZeitpunktUtc, GewichtG, GanzeCharge)
            VALUES ($growId, $zeitpunkt, $gewicht, $ganze);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$growId", waegung.GrowId);
        command.Parameters.AddWithValue("$zeitpunkt", waegung.ZeitpunktUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$gewicht", waegung.GewichtG);
        command.Parameters.AddWithValue("$ganze", waegung.GanzeCharge ? 1 : 0);
        return Convert.ToInt32((long)(command.ExecuteScalar() ?? 0L));
    }

    /// <summary>Alle Wägungen eines Grows, älteste zuerst.</summary>
    public List<TrocknungWaegung> GetForGrow(int growId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, GrowId, ZeitpunktUtc, GewichtG, GanzeCharge FROM ForkTrocknungWaegungen WHERE GrowId = $growId ORDER BY ZeitpunktUtc, Id;";
        command.Parameters.AddWithValue("$growId", growId);
        using var reader = command.ExecuteReader();
        var liste = new List<TrocknungWaegung>();
        while (reader.Read())
        {
            liste.Add(new TrocknungWaegung
            {
                Id = reader.GetInt32(0),
                GrowId = reader.GetInt32(1),
                ZeitpunktUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                GewichtG = reader.GetDouble(3),
                GanzeCharge = reader.GetInt32(4) != 0,
            });
        }

        return liste;
    }

    /// <summary>Löscht eine Wägung (Tippfehler). Nur, wenn sie zu diesem Grow gehört.</summary>
    public bool Delete(int id, int growId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ForkTrocknungWaegungen WHERE Id = $id AND GrowId = $growId;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$growId", growId);
        return command.ExecuteNonQuery() > 0;
    }
}
